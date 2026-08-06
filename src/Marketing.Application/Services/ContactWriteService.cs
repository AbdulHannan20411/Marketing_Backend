using System.Runtime.CompilerServices;
using Marketing.Application.DTOs.Contacts;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <inheritdoc cref="IContactWriteService" />
public sealed class ContactWriteService : IContactWriteService
{
    private readonly IRepository<Contact> _contacts;
    private readonly IRepository<ContactTagAssignment> _tagAssignments;
    private readonly IRepository<ContactGroupMember> _groupMembers;
    private readonly IRepository<ContactTag> _tags;
    private readonly IRepository<ContactGroup> _groups;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;

    /// <summary>Initialises a new instance.</summary>
    public ContactWriteService(
        IRepository<Contact> contacts,
        IRepository<ContactTagAssignment> tagAssignments,
        IRepository<ContactGroupMember> groupMembers,
        IRepository<ContactTag> tags,
        IRepository<ContactGroup> groups,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext)
    {
        _contacts = contacts;
        _tagAssignments = tagAssignments;
        _groupMembers = groupMembers;
        _tags = tags;
        _groups = groups;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task<ContactResponse> CreateAsync(
        CreateContactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalized = PhoneNumbers.Normalise(request.PhoneNumber);

        if (!PhoneNumbers.IsPlausible(request.PhoneNumber))
        {
            throw new ValidationException(nameof(request.PhoneNumber), "Enter a valid phone number.");
        }

        if (await _queries.CountAsync(
                _contacts.Query().Where(contact => contact.NormalizedPhoneNumber == normalized),
                cancellationToken) > 0)
        {
            throw new BusinessRuleException(
                "contact_exists",
                "A contact with that phone number already exists.");
        }

        var tenantId = _tenantContext.RequireTenantId();

        var contact = new Contact
        {
            Id = SequentialGuid.Create(),
            TenantId = tenantId,
            FullName = request.FullName.Trim(),
            PhoneNumber = PhoneNumbers.ToDisplayForm(request.PhoneNumber),
            NormalizedPhoneNumber = normalized,
            Email = request.Email?.Trim(),
            Country = request.Country?.Trim().ToUpperInvariant() ?? string.Empty,
            Status = request.Status,

            // Consent time is recorded only when the contact actually arrives subscribed. Stamping
            // it regardless would fabricate an opt-in record, which is the one field a data
            // protection audit will ask to see evidence for.
            OptedInAt = request.Status == ContactStatus.Subscribed ? DateTimeOffset.UtcNow : null,
        };

        _contacts.Add(contact);

        await ApplyTagsAsync(contact.Id, tenantId, request.TagIds, cancellationToken);
        await ApplyGroupsAsync(contact.Id, tenantId, request.GroupIds, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await LoadOneAsync(contact.Id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ContactResponse> UpdateAsync(
        string contactId,
        UpdateContactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = PublicId.Parse(PublicId.Contact, contactId, "contact");

        var contact = await _contacts.GetForUpdateAsync(id, cancellationToken)
                      ?? throw new NotFoundException("Contact", contactId);

        if (request.PhoneNumber is { } phoneNumber)
        {
            if (!PhoneNumbers.IsPlausible(phoneNumber))
            {
                throw new ValidationException(nameof(request.PhoneNumber), "Enter a valid phone number.");
            }

            var normalized = PhoneNumbers.Normalise(phoneNumber);

            if (normalized != contact.NormalizedPhoneNumber
                && await _queries.CountAsync(
                    _contacts.Query().Where(other => other.NormalizedPhoneNumber == normalized),
                    cancellationToken) > 0)
            {
                throw new BusinessRuleException(
                    "contact_exists",
                    "Another contact already uses that phone number.");
            }

            contact.PhoneNumber = PhoneNumbers.ToDisplayForm(phoneNumber);
            contact.NormalizedPhoneNumber = normalized;
        }

        contact.FullName = request.FullName?.Trim() ?? contact.FullName;
        contact.Email = request.Email?.Trim() ?? contact.Email;
        contact.Country = request.Country?.Trim().ToUpperInvariant() ?? contact.Country;
        contact.Lifecycle = request.Lifecycle ?? contact.Lifecycle;

        if (request.Status is { } status && status != contact.Status)
        {
            contact.Status = status;

            // Opting back in starts a fresh consent record; the previous one lapsed when they
            // unsubscribed and cannot be resurrected.
            contact.OptedInAt = status == ContactStatus.Subscribed ? DateTimeOffset.UtcNow : contact.OptedInAt;
        }

        // Collections are replaced only when supplied, so saving a name change from the editor
        // does not require sending the full tag and group membership back.
        if (request.TagIds is not null)
        {
            await ReplaceTagsAsync(contact.Id, contact.TenantId, request.TagIds, cancellationToken);
        }

        if (request.GroupIds is not null)
        {
            await ReplaceGroupsAsync(contact.Id, contact.TenantId, request.GroupIds, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await LoadOneAsync(id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string contactId, CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Contact, contactId, "contact");

        var contact = await _contacts.GetForUpdateAsync(id, cancellationToken)
                      ?? throw new NotFoundException("Contact", contactId);

        _contacts.Remove(contact);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BulkOperationResult> BulkDeleteAsync(
        BulkContactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ids = ParseIds(PublicId.Contact, request.Ids);

        var contacts = await _queries.ToListAsync(
            _contacts.Query(asNoTracking: false).Where(contact => ids.Contains(contact.Id)),
            cancellationToken);

        foreach (var contact in contacts)
        {
            _contacts.Remove(contact);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Skipped counts identifiers that matched nothing - out of tenant, already deleted, or
        // simply wrong. Reporting it lets the client say "42 of 50 removed" rather than implying
        // everything worked.
        return new BulkOperationResult(contacts.Count, request.Ids.Count - contacts.Count);
    }

    /// <inheritdoc />
    public async Task<BulkOperationResult> BulkTagAsync(
        BulkTagRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var contactIds = await ExistingContactIdsAsync(request.Ids, cancellationToken);
        var tagIds = await ExistingTagIdsAsync(request.TagIds, cancellationToken);
        var tenantId = _tenantContext.RequireTenantId();

        var existing = await _queries.ToListAsync(
            _tagAssignments.Query()
                .Where(assignment => contactIds.Contains(assignment.ContactId)
                                     && tagIds.Contains(assignment.ContactTagId))
                .Select(assignment => new { assignment.ContactId, assignment.ContactTagId }),
            cancellationToken);

        var added = 0;

        foreach (var contactId in contactIds)
        {
            foreach (var tagId in tagIds)
            {
                // Additive, and idempotent. Applying a tag someone already has must not fail the
                // whole batch on a unique-index violation.
                if (existing.Any(entry => entry.ContactId == contactId && entry.ContactTagId == tagId))
                {
                    continue;
                }

                _tagAssignments.Add(new ContactTagAssignment
                {
                    Id = SequentialGuid.Create(),
                    TenantId = tenantId,
                    ContactId = contactId,
                    ContactTagId = tagId,
                });

                added++;
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new BulkOperationResult(added, (contactIds.Count * tagIds.Count) - added);
    }

    /// <inheritdoc />
    public async Task<BulkOperationResult> BulkGroupAsync(
        BulkGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var contactIds = await ExistingContactIdsAsync(request.Ids, cancellationToken);
        var groupIds = await ExistingGroupIdsAsync(request.GroupIds, cancellationToken);
        var tenantId = _tenantContext.RequireTenantId();

        var existing = await _queries.ToListAsync(
            _groupMembers.Query()
                .Where(member => contactIds.Contains(member.ContactId)
                                 && groupIds.Contains(member.ContactGroupId))
                .Select(member => new { member.ContactId, member.ContactGroupId }),
            cancellationToken);

        var added = 0;

        foreach (var contactId in contactIds)
        {
            foreach (var groupId in groupIds)
            {
                if (existing.Any(entry => entry.ContactId == contactId && entry.ContactGroupId == groupId))
                {
                    continue;
                }

                _groupMembers.Add(new ContactGroupMember
                {
                    Id = SequentialGuid.Create(),
                    TenantId = tenantId,
                    ContactId = contactId,
                    ContactGroupId = groupId,
                });

                added++;
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new BulkOperationResult(added, (contactIds.Count * groupIds.Count) - added);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ExportAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return "Name,Phone,Email,Country,Status,OptedInAt,CreatedAt\n";

        var rows = await _queries.ToListAsync(
            _contacts.Query()
                .OrderBy(contact => contact.FullName)
                .Select(contact => new
                {
                    contact.FullName,
                    contact.PhoneNumber,
                    contact.Email,
                    contact.Country,
                    contact.Status,
                    contact.OptedInAt,
                    contact.CreatedOn,
                }),
            cancellationToken);

        foreach (var row in rows)
        {
            yield return string.Join(',',
                Escape(row.FullName),
                Escape(row.PhoneNumber),
                Escape(row.Email),
                Escape(row.Country),
                Escape(row.Status.ToString()),
                Escape(row.OptedInAt?.ToString("O")),
                Escape(row.CreatedOn.ToString("O"))) + "\n";
        }
    }

    /// <summary>
    /// Quotes a CSV cell when it contains a character that would otherwise break the row.
    /// <para>
    /// A name containing a comma is common enough that skipping this shifts every later column by
    /// one, silently, for that row only.
    /// </para>
    /// </summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!value.Contains(',', StringComparison.Ordinal)
            && !value.Contains('"', StringComparison.Ordinal)
            && !value.Contains('\n', StringComparison.Ordinal))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static List<Guid> ParseIds(string prefix, IReadOnlyList<string> ids)
    {
        var parsed = new List<Guid>(ids.Count);

        foreach (var id in ids)
        {
            // Malformed identifiers are dropped rather than failing the batch. A bulk operation
            // over fifty rows should not be refused wholesale because one id was stale.
            if (PublicId.TryParse(prefix, id, out var value))
            {
                parsed.Add(value);
            }
        }

        return parsed;
    }

    private async Task<List<Guid>> ExistingContactIdsAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var parsed = ParseIds(PublicId.Contact, ids);

        return [.. await _queries.ToListAsync(
            _contacts.Query().Where(contact => parsed.Contains(contact.Id)).Select(contact => contact.Id),
            cancellationToken)];
    }

    private async Task<List<Guid>> ExistingTagIdsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var parsed = ParseIds(PublicId.Tag, ids);

        return [.. await _queries.ToListAsync(
            _tags.Query().Where(tag => parsed.Contains(tag.Id)).Select(tag => tag.Id),
            cancellationToken)];
    }

    private async Task<List<Guid>> ExistingGroupIdsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var parsed = ParseIds(PublicId.Group, ids);

        return [.. await _queries.ToListAsync(
            _groups.Query().Where(group => parsed.Contains(group.Id)).Select(group => group.Id),
            cancellationToken)];
    }

    private async Task ApplyTagsAsync(
        Guid contactId,
        Guid? tenantId,
        IReadOnlyList<string>? tagIds,
        CancellationToken cancellationToken)
    {
        if (tagIds is not { Count: > 0 })
        {
            return;
        }

        foreach (var tagId in await ExistingTagIdsAsync(tagIds, cancellationToken))
        {
            _tagAssignments.Add(new ContactTagAssignment
            {
                Id = SequentialGuid.Create(),
                TenantId = tenantId,
                ContactId = contactId,
                ContactTagId = tagId,
            });
        }
    }

    private async Task ApplyGroupsAsync(
        Guid contactId,
        Guid? tenantId,
        IReadOnlyList<string>? groupIds,
        CancellationToken cancellationToken)
    {
        if (groupIds is not { Count: > 0 })
        {
            return;
        }

        foreach (var groupId in await ExistingGroupIdsAsync(groupIds, cancellationToken))
        {
            _groupMembers.Add(new ContactGroupMember
            {
                Id = SequentialGuid.Create(),
                TenantId = tenantId,
                ContactId = contactId,
                ContactGroupId = groupId,
            });
        }
    }

    private async Task ReplaceTagsAsync(
        Guid contactId,
        Guid? tenantId,
        IReadOnlyList<string> tagIds,
        CancellationToken cancellationToken)
    {
        var existing = await _queries.ToListAsync(
            _tagAssignments.Query(asNoTracking: false).Where(assignment => assignment.ContactId == contactId),
            cancellationToken);

        foreach (var assignment in existing)
        {
            _tagAssignments.Remove(assignment);
        }

        await ApplyTagsAsync(contactId, tenantId, tagIds, cancellationToken);
    }

    private async Task ReplaceGroupsAsync(
        Guid contactId,
        Guid? tenantId,
        IReadOnlyList<string> groupIds,
        CancellationToken cancellationToken)
    {
        var existing = await _queries.ToListAsync(
            _groupMembers.Query(asNoTracking: false).Where(member => member.ContactId == contactId),
            cancellationToken);

        foreach (var member in existing)
        {
            _groupMembers.Remove(member);
        }

        await ApplyGroupsAsync(contactId, tenantId, groupIds, cancellationToken);
    }

    /// <summary>Reads one contact back in the exact shape the list endpoint returns.</summary>
    private async Task<ContactResponse> LoadOneAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await _queries.FirstOrDefaultAsync(
            _contacts.Query()
                .Where(contact => contact.Id == id)
                .Select(contact => new
                {
                    contact.Id,
                    contact.FullName,
                    contact.PhoneNumber,
                    contact.Email,
                    contact.Country,
                    contact.Status,
                    TagIds = contact.TagAssignments.Where(assignment => !assignment.IsDeleted)
                        .Select(assignment => assignment.ContactTagId).ToList(),
                    GroupIds = contact.GroupMemberships.Where(membership => !membership.IsDeleted)
                        .Select(membership => membership.ContactGroupId).ToList(),
                    contact.OptedInAt,
                    contact.LastMessagedAt,
                    contact.CreatedOn,
                }),
            cancellationToken)
            ?? throw new NotFoundException("Contact", PublicId.From(PublicId.Contact, id));

        return new ContactResponse(
            PublicId.From(PublicId.Contact, row.Id),
            row.FullName,
            Initials.From(row.FullName),
            row.PhoneNumber,
            row.Email,
            row.Country,
            row.Status,
            [.. row.TagIds.Select(tagId => PublicId.From(PublicId.Tag, tagId))],
            [.. row.GroupIds.Select(groupId => PublicId.From(PublicId.Group, groupId))],
            row.OptedInAt,
            row.LastMessagedAt,
            row.CreatedOn);
    }
}
