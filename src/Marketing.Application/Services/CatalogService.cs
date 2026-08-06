using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.DTOs.Contacts;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
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

    /// <summary>Deletes a tag.</summary>
    public Task DeleteTagAsync(string tagId, CancellationToken cancellationToken = default);

    /// <summary>Creates a message template in the pending state.</summary>
    public Task<MessageTemplateResponse> CreateTemplateAsync(MessageTemplateDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Replaces a message template.</summary>
    public Task<MessageTemplateResponse> UpdateTemplateAsync(string templateId, MessageTemplateDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Deletes a message template.</summary>
    public Task DeleteTemplateAsync(string templateId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICatalogService" />
public sealed class CatalogService : ICatalogService
{
    private readonly IRepository<ContactGroup> _groups;
    private readonly IRepository<ContactTag> _tags;
    private readonly IRepository<MessageTemplate> _templates;
    private readonly IRepository<Campaign> _campaigns;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;

    /// <summary>Initialises a new instance.</summary>
    public CatalogService(
        IRepository<ContactGroup> groups,
        IRepository<ContactTag> tags,
        IRepository<MessageTemplate> templates,
        IRepository<Campaign> campaigns,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext)
    {
        _groups = groups;
        _tags = tags;
        _templates = templates;
        _campaigns = campaigns;
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

        var group = new ContactGroup
        {
            Id = SequentialGuid.Create(),
            TenantId = _tenantContext.RequireTenantId(),
            Name = draft.Name.Trim(),
            Description = draft.Description,
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

        group.Name = draft.Name.Trim();
        group.Description = draft.Description;

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

        var tag = new ContactTag
        {
            Id = SequentialGuid.Create(),
            TenantId = _tenantContext.RequireTenantId(),
            Name = draft.Name.Trim(),
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

        tag.Name = draft.Name.Trim();
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
    public async Task DeleteTagAsync(string tagId, CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Tag, tagId, "tag");

        var tag = await _tags.GetForUpdateAsync(id, cancellationToken)
                  ?? throw new NotFoundException("Tag", tagId);

        _tags.Remove(tag);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MessageTemplateResponse> CreateTemplateAsync(
        MessageTemplateDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var template = new MessageTemplate
        {
            Id = SequentialGuid.Create(),
            TenantId = _tenantContext.RequireTenantId(),
            Name = draft.Name.Trim(),
            Category = draft.Category,
            Language = draft.Language,
            HeaderText = draft.HeaderText,
            BodyText = draft.BodyText,
            FooterText = draft.FooterText,
            Variables = [.. draft.Variables ?? []],
            Buttons = [.. draft.Buttons ?? []],

            // Pending, always. Only Meta can approve a template, so a locally created one is not
            // usable until a sync brings back its real status.
            Status = TemplateStatus.Pending,
        };

        _templates.Add(template);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(template);
    }

    /// <inheritdoc />
    public async Task<MessageTemplateResponse> UpdateTemplateAsync(
        string templateId,
        MessageTemplateDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var id = PublicId.Parse(PublicId.Template, templateId, "template");

        var template = await _templates.GetForUpdateAsync(id, cancellationToken)
                       ?? throw new NotFoundException("Template", templateId);

        template.Name = draft.Name.Trim();
        template.Category = draft.Category;
        template.Language = draft.Language;
        template.HeaderText = draft.HeaderText;
        template.BodyText = draft.BodyText;
        template.FooterText = draft.FooterText;
        template.Variables = [.. draft.Variables ?? []];
        template.Buttons = [.. draft.Buttons ?? []];

        // Editing the content invalidates whatever Meta previously approved, so the template goes
        // back to pending rather than quietly keeping an approval it no longer has.
        template.Status = TemplateStatus.Pending;
        template.RejectionReason = null;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Map(template);
    }

    /// <inheritdoc />
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

        _templates.Remove(template);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

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
            template.RejectionReason);
}
