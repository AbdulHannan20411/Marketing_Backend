using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <summary>Campaign writes and lifecycle transitions.</summary>
public interface ICampaignWriteService
{
    /// <summary>Creates a campaign in the draft state.</summary>
    public Task<CampaignResponse> CreateAsync(CampaignDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Replaces a campaign. Only a draft or a scheduled campaign may be edited.</summary>
    public Task<CampaignResponse> UpdateAsync(
        string campaignId,
        CampaignDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a campaign. A running campaign must be cancelled first.</summary>
    public Task DeleteAsync(string campaignId, CancellationToken cancellationToken = default);

    /// <summary>Schedules a campaign for a future dispatch.</summary>
    public Task<CampaignResponse> ScheduleAsync(
        string campaignId,
        ScheduleCampaignRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Starts dispatching a campaign now.</summary>
    public Task<CampaignResponse> SendAsync(
        string campaignId,
        string? idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Pauses a running campaign.</summary>
    public Task<CampaignResponse> PauseAsync(string campaignId, CancellationToken cancellationToken = default);

    /// <summary>Cancels a scheduled, running or paused campaign.</summary>
    public Task<CampaignResponse> CancelAsync(string campaignId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICampaignWriteService" />
public sealed class CampaignWriteService : ICampaignWriteService
{
    private readonly IRepository<Campaign> _campaigns;
    private readonly IRepository<MessageTemplate> _templates;
    private readonly IRepository<ContactGroupMember> _groupMembers;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IRealtimeNotifier _realtime;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public CampaignWriteService(
        IRepository<Campaign> campaigns,
        IRepository<MessageTemplate> templates,
        IRepository<ContactGroupMember> groupMembers,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IRealtimeNotifier realtime,
        ICurrentUser currentUser,
        ITenantContext tenantContext,
        IDateTimeProvider clock)
    {
        _campaigns = campaigns;
        _templates = templates;
        _groupMembers = groupMembers;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _realtime = realtime;
        _currentUser = currentUser;
        _tenantContext = tenantContext;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<CampaignResponse> CreateAsync(
        CampaignDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var template = await LoadTemplateAsync(draft.TemplateId, cancellationToken);
        var tenantId = _tenantContext.RequireTenantId();

        var campaign = new Campaign
        {
            Id = SequentialGuid.Create(),
            TenantId = tenantId,
            Name = draft.Name.Trim(),
            MessageTemplateId = template.Id,
            TemplateName = template.Name,
            Status = CampaignStatus.Draft,
            AudienceLabel = draft.AudienceLabel,
            AudienceGroupIds = ParseGroupIds(draft.GroupIds),
            AudienceSize = await CountAudienceAsync(draft.GroupIds, cancellationToken),
            CreatedByName = _currentUser.DisplayName ?? "Unknown",
        };

        _campaigns.Add(campaign);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(campaign);
    }

    /// <inheritdoc />
    public async Task<CampaignResponse> UpdateAsync(
        string campaignId,
        CampaignDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var campaign = await LoadForUpdateAsync(campaignId, cancellationToken);

        // A campaign that has started sending cannot be edited. Changing the template or audience
        // mid-flight would mean half the recipients got one message and half another.
        if (campaign.Status is not (CampaignStatus.Draft or CampaignStatus.Scheduled))
        {
            throw new BusinessRuleException(
                "campaign_not_editable",
                $"A {campaign.Status.ToString().ToLowerInvariant()} campaign cannot be edited.");
        }

        var template = await LoadTemplateAsync(draft.TemplateId, cancellationToken);

        campaign.Name = draft.Name.Trim();
        campaign.MessageTemplateId = template.Id;
        campaign.TemplateName = template.Name;
        campaign.AudienceLabel = draft.AudienceLabel;
        campaign.AudienceGroupIds = ParseGroupIds(draft.GroupIds);
        campaign.AudienceSize = await CountAudienceAsync(draft.GroupIds, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(campaign);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string campaignId, CancellationToken cancellationToken = default)
    {
        var campaign = await LoadForUpdateAsync(campaignId, cancellationToken);

        if (campaign.Status == CampaignStatus.Sending)
        {
            throw new BusinessRuleException(
                "campaign_running",
                "Cancel the campaign before deleting it.");
        }

        _campaigns.Remove(campaign);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CampaignResponse> ScheduleAsync(
        string campaignId,
        ScheduleCampaignRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var campaign = await LoadForUpdateAsync(campaignId, cancellationToken);

        if (request.ScheduledAt <= _clock.UtcNow)
        {
            throw new ValidationException(
                nameof(request.ScheduledAt),
                "Choose a time in the future.");
        }

        EnsureTransitionAllowed(campaign, CampaignStatus.Scheduled, [CampaignStatus.Draft, CampaignStatus.Scheduled]);

        campaign.Status = CampaignStatus.Scheduled;
        campaign.ScheduledAt = request.ScheduledAt;

        return await CommitAndPublishAsync(campaign, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CampaignResponse> SendAsync(
        string campaignId,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var campaign = await LoadForUpdateAsync(campaignId, cancellationToken);

        // Already sending or sent: return the current state rather than starting a second
        // dispatch. This is the guard that makes a retried request safe even without the header.
        if (campaign.Status is CampaignStatus.Sending or CampaignStatus.Completed)
        {
            return Map(campaign);
        }

        EnsureTransitionAllowed(
            campaign,
            CampaignStatus.Sending,
            [CampaignStatus.Draft, CampaignStatus.Scheduled, CampaignStatus.Paused]);

        if (campaign.AudienceSize == 0)
        {
            throw new BusinessRuleException(
                "empty_audience",
                "This campaign has no recipients. Choose an audience before sending.");
        }

        campaign.Status = CampaignStatus.Sending;
        campaign.ScheduledAt ??= _clock.UtcNow;

        // A pause someone applied by hand is cleared here, so resuming a paused campaign does not
        // leave a stale automatic-resume time that would restart it a second time.
        campaign.ResumeAfter = null;

        // The dispatcher takes it from here. Sending happens on the scheduler rather than on this
        // request: fifty thousand round trips to Meta cannot happen inside an HTTP call, and a
        // dropped connection must not abandon a campaign half sent.
        return await CommitAndPublishAsync(campaign, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CampaignResponse> PauseAsync(string campaignId, CancellationToken cancellationToken = default)
    {
        var campaign = await LoadForUpdateAsync(campaignId, cancellationToken);

        EnsureTransitionAllowed(campaign, CampaignStatus.Paused, [CampaignStatus.Sending]);

        campaign.Status = CampaignStatus.Paused;

        return await CommitAndPublishAsync(campaign, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CampaignResponse> CancelAsync(string campaignId, CancellationToken cancellationToken = default)
    {
        var campaign = await LoadForUpdateAsync(campaignId, cancellationToken);

        EnsureTransitionAllowed(
            campaign,
            CampaignStatus.Failed,
            [CampaignStatus.Scheduled, CampaignStatus.Sending, CampaignStatus.Paused]);

        // Cancelled maps onto Failed, because the contract's status set has no cancelled state and
        // a cancelled campaign did not complete. Completion time is stamped so the list can sort
        // it alongside finished ones.
        campaign.Status = CampaignStatus.Failed;
        campaign.CompletedAt = _clock.UtcNow;
        campaign.ScheduledAt = null;

        return await CommitAndPublishAsync(campaign, cancellationToken);
    }

    /// <summary>Saves the change, then announces it to anyone watching the campaigns screen.</summary>
    private async Task<CampaignResponse> CommitAndPublishAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var response = Map(campaign);

        if (campaign.TenantId is { } tenantId)
        {
            // After the commit, never before: a push describing a state that then failed to save
            // would leave every connected client showing something that is not true.
            await _realtime.PublishCampaignProgressAsync(tenantId, response, cancellationToken);
        }

        return response;
    }

    /// <summary>
    /// Refuses a transition the lifecycle does not allow.
    /// <para>
    /// Stated as an explicit allow-list per transition rather than a general "is it different"
    /// check, because the invalid moves are the dangerous ones - resuming a completed campaign
    /// would re-send to everybody.
    /// </para>
    /// </summary>
    private static void EnsureTransitionAllowed(Campaign campaign, CampaignStatus target, CampaignStatus[] allowedFrom)
    {
        if (allowedFrom.Contains(campaign.Status))
        {
            return;
        }

        throw new BusinessRuleException(
            "invalid_campaign_transition",
            $"A {campaign.Status.ToString().ToLowerInvariant()} campaign cannot be moved to "
            + $"{target.ToString().ToLowerInvariant()}.");
    }

    private async Task<Campaign> LoadForUpdateAsync(string campaignId, CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.Campaign, campaignId, "campaign");

        return await _campaigns.GetForUpdateAsync(id, cancellationToken)
               ?? throw new NotFoundException("Campaign", campaignId);
    }

    private async Task<MessageTemplate> LoadTemplateAsync(string templateId, CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.Template, templateId, "template");

        var template = await _queries.FirstOrDefaultAsync(
            _templates.Query().Where(existing => existing.Id == id),
            cancellationToken)
            ?? throw new NotFoundException("Template", templateId);

        if (template.Status != TemplateStatus.Approved)
        {
            throw new BusinessRuleException(
                "template_not_approved",
                $"\"{template.Name}\" is {template.Status.ToString().ToLowerInvariant()} and cannot be sent. "
                + "Meta must approve a template before a campaign can use it.");
        }

        return template;
    }

    /// <summary>Turns the client's prefixed group identifiers into keys, dropping anything unparseable.</summary>
    private static List<Guid> ParseGroupIds(IReadOnlyList<string>? groupIds)
    {
        if (groupIds is not { Count: > 0 })
        {
            return [];
        }

        var parsed = new List<Guid>(groupIds.Count);

        foreach (var groupId in groupIds)
        {
            if (PublicId.TryParse(PublicId.Group, groupId, out var value))
            {
                parsed.Add(value);
            }
        }

        return parsed;
    }

    /// <summary>Counts distinct contacts across the chosen groups.</summary>
    private async Task<int> CountAudienceAsync(IReadOnlyList<string>? groupIds, CancellationToken cancellationToken)
    {
        var parsed = ParseGroupIds(groupIds);

        if (parsed.Count == 0)
        {
            return 0;
        }

        // Distinct, because a contact in two chosen groups is one recipient, not two - and the
        // audience size is what the customer is billed against.
        return await _queries.CountAsync(
            _groupMembers.Query()
                .Where(member => parsed.Contains(member.ContactGroupId) && !member.Contact.IsDeleted)
                .Select(member => member.ContactId)
                .Distinct(),
            cancellationToken);
    }

    private static CampaignResponse Map(Campaign campaign) =>
        new(
            PublicId.From(PublicId.Campaign, campaign.Id),
            campaign.Name,
            campaign.TemplateName,
            campaign.Status,
            new CampaignMetricsResponse(
                campaign.AudienceSize,
                campaign.SentCount,
                campaign.DeliveredCount,
                campaign.ReadCount,
                campaign.ClickedCount,
                campaign.FailedCount),
            campaign.AudienceLabel,
            campaign.ScheduledAt,
            campaign.CompletedAt,
            campaign.CreatedByName,
            campaign.CreatedOn);
}
