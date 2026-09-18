using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.DTOs.Contacts;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Business.Extensions;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <summary>Writes for groups, tags and message templates.</summary>
public interface ICatalogService
{
    /// <summary>Creates a contact group.</summary>
    public Task<ContactGroupResponse> CreateGroupAsync(ContactGroupDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Replaces a contact group.</summary>
    public Task<ContactGroupResponse> UpdateGroupAsync(string groupId, ContactGroupDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Deletes a contact group. Membership rows go with it; the contacts do not.</summary>
    public Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>Creates a tag.</summary>
    public Task<ContactTagResponse> CreateTagAsync(ContactTagDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Replaces a tag.</summary>
    public Task<ContactTagResponse> UpdateTagAsync(string tagId, ContactTagDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Deletes a tag and returns how many contacts carried it.</summary>
    public Task<int> DeleteTagAsync(string tagId, CancellationToken cancellationToken = default);

    /// <summary>Creates a message template on the connected WhatsApp account and submits it to Meta for review.</summary>
    public Task<MessageTemplateResponse> CreateTemplateAsync(MessageTemplateDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Replaces a message template's content and resubmits it to Meta.</summary>
    public Task<MessageTemplateResponse> UpdateTemplateAsync(string templateId, MessageTemplateDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Deletes a message template, at Meta as well as here.</summary>
    public Task DeleteTemplateAsync(string templateId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICatalogService" />
public sealed class CatalogService : ICatalogService
{
    private readonly IRepository<ContactGroup> _groups;
    private readonly IRepository<ContactTag> _tags;
    private readonly IRepository<MessageTemplate> _templates;
    private readonly IRepository<Campaign> _campaigns;
    private readonly IWhatsAppConnectionRepository _connections;
    private readonly ISecretProtector _protector;
    private readonly IWhatsAppGateway _gateway;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;

    /// <summary>Initialises a new instance.</summary>
    public CatalogService(
        IRepository<ContactGroup> groups,
        IRepository<ContactTag> tags,
        IRepository<MessageTemplate> templates,
        IRepository<Campaign> campaigns,
        IWhatsAppConnectionRepository connections,
        ISecretProtector protector,
        IWhatsAppGateway gateway,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext)
    {
        _groups = groups;
        _tags = tags;
        _templates = templates;
        _campaigns = campaigns;
        _connections = connections;
        _protector = protector;
        _gateway = gateway;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task<ContactGroupResponse> CreateGroupAsync(
        ContactGroupDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var name = CatalogRules.NormaliseGroupName(draft.Name);

        await EnsureGroupNameFreeAsync(name, null, cancellationToken);

        var group = new ContactGroup
        {
            TenantId = _tenantContext.RequireTenantId(),
            Name = name,
            Description = CatalogRules.NormaliseDescription(draft.Description),
        };

        _groups.Add(group);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new ContactGroupResponse(
            PublicId.From(PublicId.Group, group.Id),
            group.Name,
            group.Description,
            0,
            group.CreatedOn,
            group.CreatedOn);
    }

    /// <inheritdoc />
    public async Task<ContactGroupResponse> UpdateGroupAsync(
        string groupId,
        ContactGroupDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var id = PublicId.Parse(PublicId.Group, groupId, "group");

        var group = await _groups.GetForUpdateAsync(id, cancellationToken)
                    ?? throw new NotFoundException("Group", groupId);

        var name = CatalogRules.NormaliseGroupName(draft.Name);

        await EnsureGroupNameFreeAsync(name, id, cancellationToken);

        group.Name = name;
        group.Description = CatalogRules.NormaliseDescription(draft.Description);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var count = await _queries.CountAsync(
            _groups.Query().Where(existing => existing.Id == id)
                .SelectMany(existing => existing.Members.Where(member => !member.IsDeleted)),
            cancellationToken);

        return new ContactGroupResponse(
            PublicId.From(PublicId.Group, group.Id),
            group.Name,
            group.Description,
            count,
            group.CreatedOn,
            group.ModifiedOn ?? group.CreatedOn);
    }

    /// <inheritdoc />
    public async Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Group, groupId, "group");

        var group = await _groups.GetForUpdateAsync(id, cancellationToken)
                    ?? throw new NotFoundException("Group", groupId);

        // A campaign that has not gone out yet resolves its audience from this group at
        // dispatch time. Deleting it now would silently turn a scheduled send into an empty
        // one, which the operator would discover only when nobody received anything.
        var scheduled = await _queries.FirstOrDefaultAsync(
            _campaigns.Query()
                .Where(campaign =>
                    campaign.AudienceGroupIds.Contains(id)
                    && (campaign.Status == CampaignStatus.Scheduled
                        || campaign.Status == CampaignStatus.Sending))
                .Select(campaign => campaign.Name),
            cancellationToken);

        if (scheduled is not null)
        {
            throw new BusinessRuleException(
                "group_in_use",
                $"\"{group.Name}\" is the audience for the campaign \"{scheduled}\". "
                + "Cancel or reschedule that campaign first.");
        }

        // Soft delete. Membership rows are cascaded by the relationship; the contacts themselves
        // are untouched, because deleting a segment must never delete the people in it.
        _groups.Remove(group);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ContactTagResponse> CreateTagAsync(
        ContactTagDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var name = CatalogRules.NormaliseTagName(draft.Name);

        await EnsureTagNameFreeAsync(name, null, cancellationToken);

        var tag = new ContactTag
        {
            TenantId = _tenantContext.RequireTenantId(),
            Name = name,
            Color = draft.Color,
        };

        _tags.Add(tag);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new ContactTagResponse(
            PublicId.From(PublicId.Tag, tag.Id),
            tag.Name,
            tag.Color,
            0,
            tag.CreatedOn);
    }

    /// <inheritdoc />
    public async Task<ContactTagResponse> UpdateTagAsync(
        string tagId,
        ContactTagDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var id = PublicId.Parse(PublicId.Tag, tagId, "tag");

        var tag = await _tags.GetForUpdateAsync(id, cancellationToken)
                  ?? throw new NotFoundException("Tag", tagId);

        var name = CatalogRules.NormaliseTagName(draft.Name);

        await EnsureTagNameFreeAsync(name, id, cancellationToken);

        tag.Name = name;
        tag.Color = draft.Color;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var count = await _queries.CountAsync(
            _tags.Query().Where(existing => existing.Id == id)
                .SelectMany(existing => existing.Assignments.Where(assignment => !assignment.IsDeleted)),
            cancellationToken);

        return new ContactTagResponse(
            PublicId.From(PublicId.Tag, tag.Id),
            tag.Name,
            tag.Color,
            count,
            tag.CreatedOn);
    }

    /// <inheritdoc />
    public async Task<int> DeleteTagAsync(string tagId, CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Tag, tagId, "tag");

        var tag = await _tags.GetForUpdateAsync(id, cancellationToken)
                  ?? throw new NotFoundException("Tag", tagId);

        // Counted before the delete so the confirmation can say how many contacts lost the
        // label. Deleting a tag never touches a contact, but the operator still deserves to
        // know the blast radius after the fact.
        var affected = await _queries.CountAsync(
            _tags.Query()
                .Where(existing => existing.Id == id)
                .SelectMany(existing => existing.Assignments.Where(assignment => !assignment.IsDeleted)),
            cancellationToken);

        _tags.Remove(tag);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return affected;
    }

    /// <summary>Refuses a group name another live group already uses.</summary>
    private async Task EnsureGroupNameFreeAsync(string name, long? excluding, CancellationToken cancellationToken)
    {
        var clash = await _queries.CountAsync(
            _groups.Query().WhereNameMatches(name).Where(group => group.Id != excluding),
            cancellationToken);

        // Checked here as well as by the unique index, so the caller gets a message naming the
        // group rather than the index's generic duplicate-record wording.
        if (clash > 0)
        {
            throw new BusinessRuleException(
                "group_name_taken",
                $"A group called \"{name}\" already exists.");
        }
    }

    /// <summary>Refuses a tag name another live tag already uses.</summary>
    private async Task EnsureTagNameFreeAsync(string name, long? excluding, CancellationToken cancellationToken)
    {
        var clash = await _queries.CountAsync(
            _tags.Query().WhereNameMatches(name).Where(tag => tag.Id != excluding),
            cancellationToken);

        if (clash > 0)
        {
            throw new BusinessRuleException(
                "tag_name_taken",
                $"A tag called \"{name}\" already exists.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Submitted to Meta first and stored second. Only Meta can approve a template, and one kept only
    /// here - which is all this used to do - was never reviewed, never approved, and never came back
    /// from a sync. Stored once Meta accepts it, carrying Meta's id and the account it lives on.
    /// </remarks>
    public async Task<MessageTemplateResponse> CreateTemplateAsync(
        MessageTemplateDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        TemplateDraftRules.Validate(draft);

        var account = await RequireAccountAsync(cancellationToken);
        var definition = TemplateDraftRules.ToDefinition(draft);

        var existing = await _queries.FirstOrDefaultAsync(
            _templates.Query(asNoTracking: false).Where(template =>
                template.Name == definition.Name && template.Language == definition.Language),
            cancellationToken);

        // One row per name and language. A row this account already has is a genuine clash. One left
        // over from a previously connected account is invisible to the customer, so it is taken over
        // rather than allowed to block the name for good.
        if (existing is not null && TemplateAccount.IsOn(existing.WabaId, existing.MetaTemplateId, account.WabaId))
        {
            throw NameTaken(definition);
        }

        var submission = await SubmitAsync(
            () => _gateway.CreateTemplateAsync(account.WabaId, definition, account.AccessToken, cancellationToken),
            "create");

        var template = existing;

        if (template is null)
        {
            template = new MessageTemplate
            {
                TenantId = _tenantContext.RequireTenantId(),
                Name = definition.Name,
                BodyText = definition.BodyText,
            };

            _templates.Add(template);
        }

        Apply(template, draft, definition);

        template.MetaTemplateId = submission.Id;
        template.WabaId = account.WabaId;
        template.Status = ParseStatus(submission.Status);
        template.RejectionReason = null;

        // Meta may file a template under a different category than the one asked for - a "utility"
        // message that reads as promotional becomes marketing. Its decision is what gets priced.
        if (Enum.TryParse<TemplateCategory>(submission.Category, ignoreCase: true, out var category))
        {
            template.Category = category;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(template);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A template already on the connected account is edited at Meta, which resubmits it for review.
    /// One that never reached Meta, or reached a previously connected account, is submitted to this
    /// account as new.
    /// </remarks>
    public async Task<MessageTemplateResponse> UpdateTemplateAsync(
        string templateId,
        MessageTemplateDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        TemplateDraftRules.Validate(draft);

        var id = PublicId.Parse(PublicId.Template, templateId, "template");

        var template = await _templates.GetForUpdateAsync(id, cancellationToken)
                       ?? throw new NotFoundException("Template", templateId);

        var account = await RequireAccountAsync(cancellationToken);
        var definition = TemplateDraftRules.ToDefinition(draft);

        if (template.MetaTemplateId is { Length: > 0 } metaTemplateId
            && string.Equals(template.WabaId, account.WabaId, StringComparison.Ordinal))
        {
            // Approved wording is what Meta reviewed and what customers have received; changing it in
            // place would let a template drift away from the thing that was approved. Rejected is the
            // one state where an edit is a fix rather than a rewrite of history.
            if (template.Status != TemplateStatus.Rejected)
            {
                throw new BusinessRuleException(
                    "template_not_editable",
                    $"Meta only allows a rejected template to be edited. \"{template.Name}\" is "
                    + $"{template.Status.ToString().ToLowerInvariant()}, so create a new template instead.");
            }

            // Meta identifies a template by name and language, and neither can change once submitted.
            if (!string.Equals(template.Name, definition.Name, StringComparison.Ordinal)
                || !string.Equals(template.Language, definition.Language, StringComparison.Ordinal))
            {
                throw new ValidationException(
                    "name",
                    "Meta does not allow a submitted template to be renamed or moved to another language. "
                    + "Create a new template instead.");
            }

            // The category is sent only when it changed. Meta refuses a category on some edits, and
            // repeating the current one would turn a routine wording fix into an error.
            await SubmitAsync(
                () => _gateway.UpdateTemplateAsync(
                    metaTemplateId,
                    definition,
                    includeCategory: template.Category != draft.Category,
                    account.AccessToken,
                    cancellationToken),
                "update");
        }
        else
        {
            var clashes = await _queries.CountAsync(
                _templates.Query().Where(other =>
                    other.Id != id && other.Name == definition.Name && other.Language == definition.Language),
                cancellationToken);

            if (clashes > 0)
            {
                throw NameTaken(definition);
            }

            var submission = await SubmitAsync(
                () => _gateway.CreateTemplateAsync(account.WabaId, definition, account.AccessToken, cancellationToken),
                "create");

            template.MetaTemplateId = submission.Id;
            template.WabaId = account.WabaId;
        }

        Apply(template, draft, definition);

        // Whatever Meta approved before no longer describes this content. The webhook, or the next
        // sync, brings back the verdict on what was just sent.
        template.Status = TemplateStatus.Pending;
        template.RejectionReason = null;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(template);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Removed at Meta too when it lives on the connected account - otherwise the next sync would
    /// bring it straight back. A template from a previously connected account can only be removed
    /// here, because this platform no longer holds a credential for that account.
    /// </remarks>
    public async Task DeleteTemplateAsync(string templateId, CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Template, templateId, "template");

        var template = await _templates.GetForUpdateAsync(id, cancellationToken)
                       ?? throw new NotFoundException("Template", templateId);

        var inUse = await _queries.CountAsync(
            _campaigns.Query().Where(campaign =>
                campaign.MessageTemplateId == id
                && (campaign.Status == CampaignStatus.Scheduled || campaign.Status == CampaignStatus.Sending)),
            cancellationToken);

        if (inUse > 0)
        {
            throw new BusinessRuleException(
                "template_in_use",
                "That template is used by a scheduled or running campaign. Cancel the campaign first.");
        }

        if (template.MetaTemplateId is { Length: > 0 } metaTemplateId
            && await FindAccountAsync(cancellationToken) is { } account
            && string.Equals(template.WabaId, account.WabaId, StringComparison.Ordinal))
        {
            try
            {
                await _gateway.DeleteTemplateAsync(
                    account.WabaId,
                    template.Name,
                    metaTemplateId,
                    account.AccessToken,
                    cancellationToken);
            }
            catch (ExternalServiceException exception) when (!exception.IsTransient && exception.ProviderErrorCode == 100)
            {
                // Meta has nothing by that id - deleted in WhatsApp Manager, most often - so there is
                // nothing left to remove there. Should Meta hold it after all, the next sync restores
                // the local copy, so nothing is lost by carrying on.
            }
            catch (ExternalServiceException exception) when (!exception.IsTransient)
            {
                throw MetaRefused(exception, "delete");
            }
        }

        _templates.Remove(template);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Copies a validated draft's content onto the stored template.</summary>
    private static void Apply(MessageTemplate template, MessageTemplateDraft draft, MetaTemplateDefinition definition)
    {
        template.Name = definition.Name;
        template.Language = definition.Language;
        template.Category = draft.Category;
        template.HeaderText = definition.HeaderText;
        template.HeaderKind = TemplateDraftRules.HeaderKindFor(draft);
        template.BodyText = definition.BodyText;
        template.FooterText = definition.FooterText;

        // Worked out from the body rather than taken from the client, which does not send them. The
        // campaign sender reads this list to refuse templates whose placeholders it cannot fill.
        template.Variables = [.. TemplateDraftRules.VariablesOf(definition.BodyText)];

        // Labels only, as the column has always held. Where a button leads is kept by Meta.
        template.Buttons = [.. definition.Buttons.Select(button => button.Text)];
    }

    private static TemplateStatus ParseStatus(string? status) =>
        Enum.TryParse<TemplateStatus>(status, ignoreCase: true, out var parsed) ? parsed : TemplateStatus.Pending;

    private static BusinessRuleException NameTaken(MetaTemplateDefinition definition) =>
        new(
            "template_name_taken",
            $"A template called \"{definition.Name}\" already exists in {definition.Language}. "
            + "Choose another name, or edit that template.");

    /// <summary>The connected WhatsApp account and its credential, or null when none is usable.</summary>
    private async Task<ConnectedAccount?> FindAccountAsync(CancellationToken cancellationToken)
    {
        var connection = await _connections.FindForTenantAsync(_tenantContext.RequireTenantId(), cancellationToken);

        return connection is
        {
            Status: not ConnectionStatus.Disconnected,
            WabaId: { Length: > 0 } wabaId,
            EncryptedAccessToken: { Length: > 0 } encrypted,
        }
            ? new ConnectedAccount(wabaId, _protector.Unprotect(encrypted))
            : null;
    }

    private async Task<ConnectedAccount> RequireAccountAsync(CancellationToken cancellationToken) =>
        await FindAccountAsync(cancellationToken)
        ?? throw new BusinessRuleException(
            "whatsapp_not_connected",
            "Connect a WhatsApp account before creating or editing templates. Meta reviews each template "
            + "for the account that will send it.");

    private static async Task<T> SubmitAsync<T>(Func<Task<T>> call, string action)
    {
        try
        {
            return await call();
        }
        catch (ExternalServiceException exception) when (!exception.IsTransient)
        {
            throw MetaRefused(exception, action);
        }
    }

    private static async Task SubmitAsync(Func<Task> call, string action)
    {
        try
        {
            await call();
        }
        catch (ExternalServiceException exception) when (!exception.IsTransient)
        {
            throw MetaRefused(exception, action);
        }
    }

    /// <summary>
    /// Meta's refusal, in the words Meta wrote for the customer where it gave any.
    /// </summary>
    /// <remarks>
    /// A transient failure is never turned into this: an outage is not the template's fault, and the
    /// 503 it already produces tells the client to try again.
    /// </remarks>
    private static BusinessRuleException MetaRefused(ExternalServiceException exception, string action)
    {
        if (exception.ProviderErrorCode == 190)
        {
            return new BusinessRuleException(
                "whatsapp_token_rejected",
                "Meta rejected the WhatsApp connection's access token. Reconnect WhatsApp, then try again.");
        }

        var code = exception.ProviderErrorCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";

        return new BusinessRuleException(
            "template_rejected_by_meta",
            exception.ProviderUserMessage is { Length: > 0 } reason
                ? reason
                : $"Meta refused to {action} this template (error {code}). Check the name, wording and buttons, then try again.");
    }

    /// <summary>A WhatsApp Business Account and the decrypted token that can act on it.</summary>
    private sealed record ConnectedAccount(string WabaId, string AccessToken);

    private static MessageTemplateResponse Map(MessageTemplate template) =>
        new(
            PublicId.From(PublicId.Template, template.Id),
            template.Name,
            template.Category,
            template.Status,
            template.Language,
            template.HeaderText,
            template.BodyText,
            template.FooterText,
            template.Variables,
            template.Buttons,
            template.QualityScore,
            template.TimesUsed,
            template.ModifiedOn ?? template.CreatedOn,
            template.RejectionReason,
            template.HeaderKind);
}
