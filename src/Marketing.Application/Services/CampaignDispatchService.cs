using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.Services.Campaigns;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <summary>Drives campaigns from scheduled to completed.</summary>
public interface ICampaignDispatchService
{
    /// <summary>
    /// Advances every campaign that is due, across every tenant.
    /// <para>
    /// Called by the scheduler. Safe to call concurrently with itself in the sense that it cannot
    /// send a message twice - the unique constraint on the message log is what guarantees that -
    /// though two instances racing on the same campaign will do redundant work.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many campaigns were advanced.</returns>
    public Task<int> DispatchDueCampaignsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances one campaign by at most one batch. The caller must already be inside the tenant.
    /// </summary>
    /// <param name="campaignId">Campaign to advance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task DispatchCampaignAsync(long campaignId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICampaignDispatchService" />
public sealed partial class CampaignDispatchService : ICampaignDispatchService
{
    /// <summary>Campaigns advanced per poll. Beyond this, the next tick picks up the rest.</summary>
    private const int CampaignsPerPoll = 25;

    /// <summary>
    /// Messages sent per campaign per tick.
    /// <para>
    /// Deliberately modest. Each send is a round trip to Meta, and a batch that runs for minutes
    /// holds a transaction open, delays every other tenant's campaign behind it, and loses all its
    /// progress if the host is recycled mid-flight.
    /// </para>
    /// </summary>
    private const int MessagesPerBatch = 100;

    /// <summary>Attempts before a message is abandoned, so one bad recipient cannot loop forever.</summary>
    private const int MaximumAttempts = 3;

    /// <summary>Meta's rolling window for the messaging limit.</summary>
    private static readonly TimeSpan RollingWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// How long to wait before re-checking when the allowance is exhausted but the exact moment
    /// capacity returns cannot be derived.
    /// </summary>
    private static readonly TimeSpan FallbackResumeDelay = TimeSpan.FromMinutes(15);

    private readonly ICampaignMessageRepository _messages;
    private readonly IRepository<Campaign> _campaigns;
    private readonly IRepository<CampaignRun> _runs;
    private readonly IRecurrenceCalculator _recurrence;
    private readonly IRepository<ContactGroupMember> _groupMembers;
    private readonly IRepository<MessageTemplate> _templates;
    private readonly IRepository<DeliveryFailure> _failures;
    private readonly IWhatsAppConnectionRepository _connections;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWhatsAppGateway _gateway;
    private readonly ISecretProtector _protector;
    private readonly IRealtimeNotifier _realtime;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<CampaignDispatchService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public CampaignDispatchService(
        ICampaignMessageRepository messages,
        IRepository<Campaign> campaigns,
        IRepository<CampaignRun> runs,
        IRecurrenceCalculator recurrence,
        IRepository<ContactGroupMember> groupMembers,
        IRepository<MessageTemplate> templates,
        IRepository<DeliveryFailure> failures,
        IWhatsAppConnectionRepository connections,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IWhatsAppGateway gateway,
        ISecretProtector protector,
        IRealtimeNotifier realtime,
        ITenantContext tenantContext,
        IDateTimeProvider clock,
        ILogger<CampaignDispatchService> logger)
    {
        _messages = messages;
        _campaigns = campaigns;
        _runs = runs;
        _recurrence = recurrence;
        _groupMembers = groupMembers;
        _templates = templates;
        _failures = failures;
        _connections = connections;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _gateway = gateway;
        _protector = protector;
        _realtime = realtime;
        _tenantContext = tenantContext;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> DispatchDueCampaignsAsync(CancellationToken cancellationToken = default)
    {
        var due = await _messages.FindDueCampaignsAsync(_clock.UtcNow, CampaignsPerPoll, cancellationToken);
        var advanced = 0;

        foreach (var candidate in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Entered per campaign, so every query below - including the ones inside the Graph
            // handler that fetches the access token - is filtered to this tenant and no other.
            using (_tenantContext.BeginScope(candidate.TenantId))
            {
                try
                {
                    await DispatchCampaignAsync(candidate.CampaignId, cancellationToken);
                    advanced++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One tenant's broken connection must not stop every other tenant's campaign,
                    // so the loop continues. The campaign stays in its current state and is picked
                    // up again on the next tick.
                    LogCampaignFailed(exception, candidate.CampaignId, candidate.TenantId);
                }
            }
        }

        return advanced;
    }

    /// <inheritdoc />
    public async Task DispatchCampaignAsync(long campaignId, CancellationToken cancellationToken = default)
    {
        var campaign = await _campaigns.GetForUpdateAsync(campaignId, cancellationToken);

        if (campaign is null || !await TryStartAsync(campaign, cancellationToken))
        {
            return;
        }

        var connection = await _connections.FindForTenantAsync(
            _tenantContext.RequireTenantId(),
            cancellationToken);

        if (connection is not { Status: ConnectionStatus.Connected, PhoneNumberId: { Length: > 0 } phoneNumberId })
        {
            await AbandonAsync(
                campaign,
                "WhatsApp is not connected. Reconnect the account and start the campaign again.",
                cancellationToken);

            return;
        }

        // Decrypted here and passed down to each send, rather than left to the handler that
        // normally looks it up. That handler resolves the tenant inside its own dependency-injection
        // scope, and a scheduled job has no signed-in user for it to fall back on - so it finds no
        // tenant, sends no credential, and Meta refuses every message with "an access token is
        // required". The connection is already loaded here; using it is both cheaper and honest.
        var accessToken = connection.EncryptedAccessToken is { Length: > 0 } encrypted
            ? _protector.Unprotect(encrypted)
            : null;

        var template = await LoadTemplateAsync(campaign, cancellationToken);

        if (template is null)
        {
            await AbandonAsync(
                campaign,
                "The campaign's template is no longer available.",
                cancellationToken);

            return;
        }

        // Refused rather than sent with empty placeholders. The campaign wizard does not yet
        // collect variable bindings, and a message reading "Hi , your order  is ready" is worse
        // than one that never left - it is unrecoverable and it is what the recipient remembers.
        if (template.Variables.Count > 0)
        {
            await AbandonAsync(
                campaign,
                $"\"{template.Name}\" expects {template.Variables.Count} variable value(s), which this "
                + "campaign does not supply. Choose a template without placeholders.",
                cancellationToken);

            return;
        }

        await MaterialiseRecipientsAsync(campaign, cancellationToken);

        var allowance = await CalculateAllowanceAsync(connection, cancellationToken);

        if (allowance.Remaining <= 0)
        {
            await PauseForAllowanceAsync(campaign, allowance, cancellationToken);

            return;
        }

        var batch = await _messages.ClaimPendingAsync(
            campaign.Id,
            Math.Min(MessagesPerBatch, allowance.Remaining),
            cancellationToken);

        foreach (var message in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await SendAsync(campaign, message, phoneNumberId, accessToken, template, cancellationToken);
        }

        // Written once for the whole batch. Saving per message would triple the round trips to the
        // database for no benefit - the message rows are the durable record either way, and a crash
        // mid-batch leaves them exactly as the last commit saw them.
        await FinaliseAsync(campaign, connection, cancellationToken);
    }

    /// <summary>
    /// Moves a due campaign into the sending state, or reports that there is nothing to do.
    /// </summary>
    private async Task<bool> TryStartAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        // A recurring campaign that is due opens a new firing rather than resuming the last one.
        if (campaign.Status == CampaignStatus.Scheduled
            && campaign.NextRunAtUtc is { } dueAt
            && dueAt <= now
            && CampaignMapper.ReadRecurrence(campaign.RecurrenceJson) is { } rule)
        {
            return await TryOpenOccurrenceAsync(campaign, rule, dueAt, cancellationToken);
        }

        switch (campaign.Status)
        {
            case CampaignStatus.Scheduled when campaign.ScheduledAt <= now:
            case CampaignStatus.Paused when campaign.ResumeAfter is { } resume && resume <= now:
                campaign.Status = CampaignStatus.Sending;
                campaign.ResumeAfter = null;

                // A one-off gets a run too, so every message ever written belongs to a firing and
                // the run history reads the same for both kinds of campaign.
                if (campaign.ActiveCampaignRunId is null)
                {
                    await OpenRunAsync(campaign, campaign.ScheduledAt ?? now, false, cancellationToken);
                }

                await _unitOfWork.SaveChangesAsync(cancellationToken);

                return true;

            case CampaignStatus.Sending:
                return true;

            default:
                // Cancelled, completed, or paused by a person. A campaign someone paused by hand
                // stays paused: ResumeAfter is null, and only the allowance path ever sets it.
                return false;
        }
    }

    /// <summary>
    /// Opens the firing a recurring campaign is due for, after re-checking that it can still send.
    /// </summary>
    /// <remarks>
    /// Everything checked here was already checked when the campaign was scheduled. It is checked
    /// again because a campaign scheduled in September and firing in December has had three months
    /// in which Meta could un-approve the template, the account could be disconnected, or the groups
    /// could be emptied.
    /// </remarks>
    private async Task<bool> TryOpenOccurrenceAsync(
        Campaign campaign,
        RecurrenceRule rule,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        // Catch-up: after an outage a daily campaign has one missed occurrence and an hourly one has
        // six. Firing them all sends six identical messages to the same people, so only the most
        // recent is fired and the rest are recorded as skipped - visible in the history rather than
        // silently absent.
        var missed = _recurrence.Between(rule, dueAt, now, campaign.OccurrencesRun);

        foreach (var skipped in missed.Take(Math.Max(0, missed.Count - 1)))
        {
            await OpenRunAsync(campaign, skipped, false, cancellationToken);
            await CloseRunAsync(
                campaign,
                CampaignRunStatus.Skipped,
                "Missed while the service was unavailable. Only the most recent occurrence was sent.",
                cancellationToken);
        }

        var fireAt = missed.Count > 0 ? missed[^1] : dueAt;

        if (await FindBlockingReasonAsync(campaign, cancellationToken) is { } reason)
        {
            // Paused, not failed. A paused campaign can be fixed and resumed; a failed one usually
            // means somebody rebuilds it from scratch.
            await OpenRunAsync(campaign, fireAt, false, cancellationToken);
            await CloseRunAsync(campaign, CampaignRunStatus.Skipped, reason, cancellationToken);

            campaign.Status = CampaignStatus.Paused;
            campaign.NextRunAtUtc = null;

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            LogOccurrenceBlocked(campaign.Id, reason);

            return false;
        }

        await OpenRunAsync(campaign, fireAt, false, cancellationToken);

        campaign.Status = CampaignStatus.Sending;
        campaign.ResumeAfter = null;

        // A fresh audience for each firing. Without this reset the campaign would re-send to the
        // snapshot taken the first time it ran, which is the opposite of what a recurring campaign
        // to "new customers this week" is for.
        campaign.RecipientsQueuedOn = null;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>Why this campaign cannot send right now, or null when it can.</summary>
    private async Task<string?> FindBlockingReasonAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        var connection = await _connections.FindForTenantAsync(
            _tenantContext.RequireTenantId(),
            cancellationToken);

        if (connection is not { Status: ConnectionStatus.Connected, PhoneNumberId: { Length: > 0 } })
        {
            return "WhatsApp is not connected. Reconnect the account to resume this campaign.";
        }

        if (campaign.MessageTemplateId is not { } templateId)
        {
            return "The campaign has no template.";
        }

        var status = await _queries.FirstOrDefaultAsync(
            _templates.Query()
                .Where(template => template.Id == templateId)
                .Select(template => (TemplateStatus?)template.Status),
            cancellationToken);

        // Meta pauses and disables templates on poor recipient feedback, without warning and long
        // after approval.
        return status switch
        {
            null => "The template this campaign uses no longer exists.",
            TemplateStatus.Approved => null,
            _ => $"\"{campaign.TemplateName}\" is {status.ToString()!.ToLowerInvariant()} at Meta "
                 + "and cannot be sent. Fix the template, then resume the campaign.",
        };
    }

    /// <summary>Claims one occurrence, which is what stops it being dispatched twice.</summary>
    /// <remarks>
    /// The row is written before any message goes out and <c>(CampaignId, OccurrenceNumber)</c> is
    /// unique, so two pollers in the same minute, a redelivery, or a retry after a timeout collide
    /// on the insert rather than each sending to the whole audience.
    /// </remarks>
    private async Task OpenRunAsync(
        Campaign campaign,
        DateTimeOffset scheduledFor,
        bool triggeredManually,
        CancellationToken cancellationToken)
    {
        var highest = await _queries.FirstOrDefaultAsync(
            _runs.Query()
                .Where(run => run.CampaignId == campaign.Id)
                .OrderByDescending(run => run.OccurrenceNumber)
                .Select(run => (int?)run.OccurrenceNumber),
            cancellationToken);

        var run = new CampaignRun
        {
            TenantId = campaign.TenantId,
            CampaignId = campaign.Id,
            OccurrenceNumber = (highest ?? 0) + 1,
            Status = CampaignRunStatus.Running,
            TriggeredManually = triggeredManually,
            ScheduledForUtc = scheduledFor,
            StartedAt = _clock.UtcNow,
        };

        _runs.Add(run);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        campaign.ActiveCampaignRunId = run.Id;
        campaign.LastRunAtUtc = scheduledFor;
        campaign.OccurrencesRun++;
    }

    /// <summary>Closes the active firing and rolls its counters up from the messages it sent.</summary>
    /// <remarks>
    /// Counted from the message rows rather than incremented alongside the campaign's own counters.
    /// Two counters maintained in parallel drift the first time a send path forgets one of them, and
    /// the message rows are the only record that cannot be wrong.
    /// </remarks>
    private async Task CloseRunAsync(
        Campaign campaign,
        CampaignRunStatus status,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        if (campaign.ActiveCampaignRunId is not { } runId)
        {
            return;
        }

        var run = await _runs.GetForUpdateAsync(runId, cancellationToken);

        if (run is not null)
        {
            var counts = await _queries.ToListAsync(
                _messages.Query()
                    .Where(message => message.CampaignRunId == runId)
                    .GroupBy(message => message.Status)
                    .Select(group => new { Status = group.Key, Count = group.Count() }),
                cancellationToken);

            int CountOf(CampaignMessageStatus wanted) =>
                counts.FirstOrDefault(entry => entry.Status == wanted)?.Count ?? 0;

            run.Status = status;
            run.FailureReason = failureReason;
            run.CompletedAt = _clock.UtcNow;
            run.AudienceSize = counts.Sum(entry => entry.Count);
            run.SentCount = CountOf(CampaignMessageStatus.Sent)
                            + CountOf(CampaignMessageStatus.Delivered)
                            + CountOf(CampaignMessageStatus.Read);
            run.DeliveredCount = CountOf(CampaignMessageStatus.Delivered) + CountOf(CampaignMessageStatus.Read);
            run.ReadCount = CountOf(CampaignMessageStatus.Read);
            run.FailedCount = CountOf(CampaignMessageStatus.Failed);
        }

        campaign.ActiveCampaignRunId = null;
    }

    /// <summary>Writes one message row per recipient, once per campaign.</summary>
    private async Task MaterialiseRecipientsAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        if (campaign.RecipientsQueuedOn is not null)
        {
            return;
        }

        var groupIds = campaign.AudienceGroupIds;
        var tenantId = _tenantContext.RequireTenantId();

        // Subscribed only. A contact who opted out after the campaign was composed must not be
        // messaged, which is why the audience is resolved here and not when the draft was saved.
        var recipients = await _queries.ToListAsync(
            _groupMembers.Query()
                .Where(member =>
                    groupIds.Contains(member.ContactGroupId)
                    && !member.Contact.IsDeleted
                    && member.Contact.Status == ContactStatus.Subscribed)
                // The normalised form, not the display one. This value goes straight to Meta,
                // which wants a dialable international number - the display column carries a
                // leading plus and whatever punctuation the contact was saved with.
                .Select(member => new { member.ContactId, PhoneNumber = member.Contact.NormalizedPhoneNumber })
                .Distinct(),
            cancellationToken);

        // Belt and braces against a re-run that got half way: the unique index would reject the
        // duplicates anyway, but as a failed INSERT for the whole batch rather than a skip.
        var alreadyQueued = await _messages.GetQueuedContactIdsAsync(campaign.Id, cancellationToken);
        var existing = alreadyQueued.ToHashSet();

        var rows = recipients
            .Where(recipient => !existing.Contains(recipient.ContactId))
            .Select(recipient => new CampaignMessage
            {
                TenantId = tenantId,
                CampaignId = campaign.Id,
                CampaignRunId = campaign.ActiveCampaignRunId,
                ContactId = recipient.ContactId,
                PhoneNumber = recipient.PhoneNumber,
                Status = CampaignMessageStatus.Pending,
            })
            .ToList();

        _messages.AddRange(rows);

        campaign.RecipientsQueuedOn = _clock.UtcNow;

        // Corrected to what will actually be attempted. The figure recorded when the draft was
        // saved counted everyone in the groups, including contacts who have since opted out.
        campaign.AudienceSize = existing.Count + rows.Count;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogRecipientsQueued(campaign.Id, rows.Count);
    }

    /// <summary>Sends one message and records the outcome against its row.</summary>
    private async Task SendAsync(
        Campaign campaign,
        CampaignMessage message,
        string phoneNumberId,
        string? accessToken,
        MessageTemplate template,
        CancellationToken cancellationToken)
    {
        message.AttemptCount++;

        try
        {
            var metaMessageId = await _gateway.SendTemplateAsync(
                phoneNumberId,
                message.PhoneNumber,
                template.Name,
                template.Language,
                // Empty by design: templates carrying placeholders are refused before the batch
                // starts, so anything reaching here has nothing to substitute.
                [],
                accessToken,
                cancellationToken);

            message.MetaMessageId = metaMessageId;
            message.Status = CampaignMessageStatus.Sent;
            message.SentOn = _clock.UtcNow;

            campaign.SentCount++;
        }
        catch (ExternalServiceException exception)
        {
            // Left pending when attempts remain, so a Meta blip costs a retry rather than a
            // recipient. Only a message that has exhausted them is written off.
            if (message.AttemptCount < MaximumAttempts)
            {
                LogSendRetryable(exception, message.Id, message.AttemptCount);

                return;
            }

            message.Status = CampaignMessageStatus.Failed;
            message.ErrorReason = exception.Message;

            campaign.FailedCount++;

            RecordFailure(campaign, message, exception.Message);
        }
    }

    /// <summary>Adds a row to the failures report the client renders under Reports.</summary>
    private void RecordFailure(Campaign campaign, CampaignMessage message, string reason)
    {
        _failures.Add(new DeliveryFailure
        {
            TenantId = campaign.TenantId,
            CampaignId = campaign.Id,
            CampaignName = campaign.Name,
            ContactId = message.ContactId,
            PhoneNumber = message.PhoneNumber,
            Reason = reason.Length > 500 ? reason[..500] : reason,
            ErrorCode = message.ErrorCode ?? 0,
            OccurredOn = _clock.UtcNow,
        });
    }

    /// <summary>Commits the batch, updates the connection counter and announces progress.</summary>
    private async Task FinaliseAsync(
        Campaign campaign,
        WhatsAppConnection connection,
        CancellationToken cancellationToken)
    {
        var remaining = await _messages.CountPendingAsync(campaign.Id, cancellationToken);

        if (remaining == 0)
        {
            await CloseRunAsync(campaign, CampaignRunStatus.Completed, null, cancellationToken);

            var rule = CampaignMapper.ReadRecurrence(campaign.RecurrenceJson);
            var next = rule is null
                ? null
                : _recurrence.Next(rule, _clock.UtcNow, campaign.OccurrencesRun);

            if (next is { } nextRun)
            {
                // Back to scheduled, not completed. The campaign fires again, and stamping a
                // completion time on it would have the client render "Completed 14 Aug" on
                // something due to run on Monday.
                campaign.Status = CampaignStatus.Scheduled;
                campaign.NextRunAtUtc = nextRun;
            }
            else
            {
                campaign.Status = CampaignStatus.Completed;
                campaign.CompletedAt = _clock.UtcNow;
                campaign.NextRunAtUtc = null;
            }
        }

        connection.MessagesLast24h = (await _messages.GetSendTimesSinceAsync(
            _tenantContext.RequireTenantId(),
            _clock.UtcNow - RollingWindow,
            cancellationToken)).Count;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await PublishAsync(campaign, cancellationToken);
    }

    /// <summary>
    /// Works out how many more messages the tenant may send inside Meta's rolling window.
    /// </summary>
    private async Task<Allowance> CalculateAllowanceAsync(
        WhatsAppConnection connection,
        CancellationToken cancellationToken)
    {
        // Zero means Meta has not told us a limit yet - a freshly connected number before its first
        // tier is assigned. Treated as unlimited rather than as "send nothing", because refusing to
        // send at all would be a worse failure than letting Meta enforce its own ceiling.
        if (connection.MessagingLimit <= 0)
        {
            return new Allowance(int.MaxValue, null);
        }

        var since = _clock.UtcNow - RollingWindow;

        var sendTimes = await _messages.GetSendTimesSinceAsync(
            _tenantContext.RequireTenantId(),
            since,
            cancellationToken);

        var remaining = connection.MessagingLimit - sendTimes.Count;

        // The window is rolling, so capacity returns the moment the oldest send inside it ages out.
        // Waiting a flat interval instead would either resume too early and fail, or idle for hours
        // after capacity was already free.
        var capacityReturnsAt = sendTimes.Count > 0 ? sendTimes[0] + RollingWindow : (DateTimeOffset?)null;

        return new Allowance(remaining, capacityReturnsAt);
    }

    /// <summary>Pauses a campaign that has run out of allowance, with an automatic resume time.</summary>
    private async Task PauseForAllowanceAsync(
        Campaign campaign,
        Allowance allowance,
        CancellationToken cancellationToken)
    {
        campaign.Status = CampaignStatus.Paused;
        campaign.ResumeAfter = allowance.CapacityReturnsAt ?? _clock.UtcNow + FallbackResumeDelay;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogRateLimited(campaign.Id, campaign.ResumeAfter.Value);

        await PublishAsync(campaign, cancellationToken);
    }

    /// <summary>Ends a campaign that cannot proceed, recording why on the failures report.</summary>
    private async Task AbandonAsync(Campaign campaign, string reason, CancellationToken cancellationToken)
    {
        campaign.Status = CampaignStatus.Failed;
        campaign.CompletedAt = _clock.UtcNow;
        campaign.ResumeAfter = null;

        _failures.Add(new DeliveryFailure
        {
            TenantId = campaign.TenantId,
            CampaignId = campaign.Id,
            CampaignName = campaign.Name,
            Reason = reason,
            OccurredOn = _clock.UtcNow,
        });

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogCampaignAbandoned(campaign.Id, reason);

        await PublishAsync(campaign, cancellationToken);
    }

    private async Task<MessageTemplate?> LoadTemplateAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        if (campaign.MessageTemplateId is not { } templateId)
        {
            return null;
        }

        return await _queries.FirstOrDefaultAsync(
            _templates.Query().Where(template => template.Id == templateId),
            cancellationToken);
    }

    private async Task PublishAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        if (campaign.TenantId is not { } tenantId)
        {
            return;
        }

        await _realtime.PublishCampaignProgressAsync(
            tenantId,
            CampaignMapper.ToResponse(campaign),
            cancellationToken);
    }

    /// <summary>How much of the rolling window is left, and when more becomes available.</summary>
    private readonly record struct Allowance(int Remaining, DateTimeOffset? CapacityReturnsAt);

    [LoggerMessage(
        EventId = 2706,
        Level = LogLevel.Warning,
        Message = "Campaign {CampaignId} was paused instead of firing: {Reason}")]
    private partial void LogOccurrenceBlocked(long campaignId, string reason);

    [LoggerMessage(
        EventId = 2701,
        Level = LogLevel.Information,
        Message = "Queued {RecipientCount} recipients for campaign {CampaignId}.")]
    private partial void LogRecipientsQueued(long campaignId, int recipientCount);

    [LoggerMessage(
        EventId = 2702,
        Level = LogLevel.Warning,
        Message = "Campaign {CampaignId} hit the 24-hour messaging limit; resuming after {ResumeAfter}.")]
    private partial void LogRateLimited(long campaignId, DateTimeOffset resumeAfter);

    [LoggerMessage(
        EventId = 2703,
        Level = LogLevel.Warning,
        Message = "Message {MessageId} failed on attempt {AttemptCount} and will be retried.")]
    private partial void LogSendRetryable(Exception exception, long messageId, int attemptCount);

    [LoggerMessage(
        EventId = 2704,
        Level = LogLevel.Error,
        Message = "Campaign {CampaignId} for tenant {TenantId} could not be advanced.")]
    private partial void LogCampaignFailed(Exception exception, long campaignId, long tenantId);

    [LoggerMessage(
        EventId = 2705,
        Level = LogLevel.Warning,
        Message = "Campaign {CampaignId} was abandoned: {Reason}")]
    private partial void LogCampaignAbandoned(long campaignId, string reason);
}
