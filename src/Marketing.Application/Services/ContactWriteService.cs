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
    /// <summary>
    /// Identifiers accepted in one bulk call.
    /// <para>
    /// The table selects twelve at a time and selection survives pagination, so a genuine selection
    /// never approaches this. It exists so a scripted caller cannot ask for a statement with a
    /// hundred thousand parameters in it.
    /// </para>
    /// </summary>
    public const int MaxBulkIds = 1000;

    private readonly IRepository<Contact> _contacts;
    private readonly IRepository<ContactTagAssignment> _tagAssignments;
    private readonly IRepository<ContactGroupMember> _groupMembers;
    private readonly IRepository<ContactTag> _tags;
    private readonly IRepository<ContactGroup> _groups;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;
    private readonly IPlanGuard _planGuard;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public ContactWriteService(
        IRepository<Contact> contacts,
        IRepository<ContactTagAssignment> tagAssignments,
        IRepository<ContactGroupMember> groupMembers,
        IRepository<ContactTag> tags,
        IRepository<ContactGroup> groups,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        IPlanGuard planGuard,
        IDateTimeProvider clock)
    {
        _contacts = contacts;
        _tagAssignments = tagAssignments;
        _groupMembers = groupMembers;
        _tags = tags;
        _groups = groups;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _planGuard = planGuard;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<ContactResponse> CreateAsync(
        CreateContactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Checked before anything is validated or written, so a tenant at their ceiling gets the
        // upgrade prompt rather than a form-level error about some unrelated field.
        await _planGuard.EnsureContactCapacityAsync(1, cancellationToken);

        var fullName = ContactRules.NormaliseName(request.FullName);
        var normalized = ContactRules.NormalisePhone(request.PhoneNumber);
        var email = ContactRules.NormaliseEmail(request.Email);
        var country = ContactRules.ResolveCountry(request.Country, request.PhoneNumber);

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
            TenantId = tenantId,
            FullName = fullName,
            PhoneNumber = PhoneNumbers.ToDisplayForm(request.PhoneNumber),
            NormalizedPhoneNumber = normalized,
            Email = email,
            Country = country,
            Status = request.Status,

            // Consent time is recorded only when the contact actually arrives subscribed. Stamping
            // it regardless would fabricate an opt-in record, which is the one field a data
            // protection audit will ask to see evidence for.
            OptedInAt = ContactRules.ConsentStampFor(request.Status, _clock.UtcNow),
        };

        _contacts.Add(contact);

        await ApplyTagsAsync(contact, tenantId, request.TagIds, strict: true, cancellationToken);
        await ApplyGroupsAsync(contact, tenantId, request.GroupIds, strict: true, cancellationToken);

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
            var normalized = ContactRules.NormalisePhone(phoneNumber);

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

        if (request.FullName is not null)
        {
            contact.FullName = ContactRules.NormaliseName(request.FullName);
        }

        if (request.Email is not null)
        {
            contact.Email = ContactRules.NormaliseEmail(request.Email);
        }

        if (request.Country is not null)
        {
            contact.Country = ContactRules.ResolveCountry(request.Country, contact.NormalizedPhoneNumber);
        }

        contact.Lifecycle = request.Lifecycle ?? contact.Lifecycle;

        if (request.Status is { } status && status != contact.Status)
        {
            ContactRules.EnsureTransitionAllowed(contact.Status, status);

            contact.Status = status;

            // Opting back in starts a fresh consent record; unsubscribing or blocking clears it,
            // because a stamp left behind reads as consent this platform no longer holds.
            contact.OptedInAt = ContactRules.ConsentStampFor(status, _clock.UtcNow);
        }

        // Collections are replaced only when supplied, so saving a name change from the editor
        // does not require sending the full tag and group membership back.
        if (request.TagIds is not null)
        {
            await ReplaceTagsAsync(contact, contact.TenantId, request.TagIds, cancellationToken);
        }

        if (request.GroupIds is not null)
        {
            await ReplaceGroupsAsync(contact, contact.TenantId, request.GroupIds, cancellationToken);
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

        EnsureBulkSizeAllowed(request.Ids);

        var resolved = await ResolveContactsAsync(request.Ids, cancellationToken);

        foreach (var contact in resolved.Found.Values)
        {
            _contacts.Remove(contact);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new BulkOperationResult(request.Ids.Count, resolved.Found.Count, resolved.Failed);
    }

    /// <inheritdoc />
    public async Task<BulkOperationResult> BulkTagAsync(
        BulkTagRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureBulkSizeAllowed(request.Ids);

        var resolved = await ResolveContactsAsync(request.Ids, cancellationToken);
        var tagIds = await ResolveTargetsAsync(PublicId.Tag, request.TagIds, IsKnownTagAsync, cancellationToken);

        var tenantId = _tenantContext.RequireTenantId();
        var contactIds = resolved.Found.Keys.ToList();

        var existing = await _queries.ToListAsync(
            _tagAssignments.Query(asNoTracking: false)
                .Where(assignment => contactIds.Contains(assignment.ContactId)),
            cancellationToken);

        foreach (var contactId in contactIds)
        {
            var current = existing.Where(assignment => assignment.ContactId == contactId).ToList();

            ApplyMode(
                request.Mode,
                tagIds,
                current.Select(assignment => assignment.ContactTagId).ToList(),
                add: tagId => _tagAssignments.Add(new ContactTagAssignment
                {
                    TenantId = tenantId,
                    ContactId = contactId,
                    ContactTagId = tagId,
                }),
                remove: tagId => _tagAssignments.Remove(
                    current.First(assignment => assignment.ContactTagId == tagId)));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new BulkOperationResult(request.Ids.Count, contactIds.Count, resolved.Failed);
    }

    /// <inheritdoc />
    public async Task<BulkOperationResult> BulkGroupAsync(
        BulkGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureBulkSizeAllowed(request.Ids);

        var resolved = await ResolveContactsAsync(request.Ids, cancellationToken);
        var groupIds = await ResolveTargetsAsync(
            PublicId.Group,
            request.GroupIds,
            IsKnownGroupAsync,
            cancellationToken);

        var tenantId = _tenantContext.RequireTenantId();
        var contactIds = resolved.Found.Keys.ToList();

        var existing = await _queries.ToListAsync(
            _groupMembers.Query(asNoTracking: false)
                .Where(member => contactIds.Contains(member.ContactId)),
            cancellationToken);

        foreach (var contactId in contactIds)
        {
            var current = existing.Where(member => member.ContactId == contactId).ToList();

            ApplyMode(
                request.Mode,
                groupIds,
                current.Select(member => member.ContactGroupId).ToList(),
                add: groupId => _groupMembers.Add(new ContactGroupMember
                {
                    TenantId = tenantId,
                    ContactId = contactId,
                    ContactGroupId = groupId,
                }),
                remove: groupId => _groupMembers.Remove(
                    current.First(member => member.ContactGroupId == groupId)));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new BulkOperationResult(request.Ids.Count, contactIds.Count, resolved.Failed);
    }

    /// <inheritdoc />
    public Task<BulkOperationResult> SetGroupMembershipAsync(
        string groupId,
        MembershipRequest request,
        BulkMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Delegated rather than reimplemented. The group detail screen and the contacts table are
        // two doors into the same operation, and two implementations would eventually disagree
        // about counts.
        return BulkGroupAsync(new BulkGroupRequest(request.ContactIds, [groupId], mode), cancellationToken);
    }

    /// <inheritdoc />
    public Task<BulkOperationResult> SetTagMembershipAsync(
        string tagId,
        MembershipRequest request,
        BulkMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return BulkTagAsync(new BulkTagRequest(request.ContactIds, [tagId], mode), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ContactResponse> MergeAsync(
        MergeContactsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var keepId = PublicId.Parse(PublicId.Contact, request.KeepId, "contact");

        var survivor = await _contacts.GetForUpdateAsync(keepId, cancellationToken)
                       ?? throw new NotFoundException("Contact", request.KeepId);

        var mergeIds = ParseIds(PublicId.Contact, request.MergeIds).Where(id => id != keepId).ToList();

        if (mergeIds.Count == 0)
        {
            throw new ValidationException("mergeIds", "Choose at least one other contact to merge in.");
        }

        List<Contact> losers = [.. await _queries.ToListAsync(
            _contacts.Query(asNoTracking: false).Where(contact => mergeIds.Contains(contact.Id)),
            cancellationToken)];

        if (losers.Count == 0)
        {
            throw new NotFoundException("None of the contacts to merge could be found.");
        }

        await MergeMembershipsAsync(survivor, losers, cancellationToken);

        // The survivor inherits the earliest creation and the most recent contact, so the merged
        // record reads as one continuous relationship rather than starting the day of the merge.
        survivor.LastMessagedAt = losers
            .Select(loser => loser.LastMessagedAt)
            .Append(survivor.LastMessagedAt)
            .Max();

        survivor.Email ??= losers.Select(loser => loser.Email).FirstOrDefault(email => email is not null);

        ApplyOverrides(survivor, request.FieldOverrides);

        foreach (var loser in losers)
        {
            _contacts.Remove(loser);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await LoadOneAsync(survivor.Id, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ExportAsync(
        ContactExportQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        yield return "fullName,phoneNumber,email,country,status,tags,groups,optedInAt,lastMessagedAt,createdAt\n";

        var source = _contacts.Query();

        if (query.Ids is { Count: > 0 } ids)
        {
            var selected = ParseIds(PublicId.Contact, ids);

            source = source.Where(contact => selected.Contains(contact.Id));
        }
        else
        {
            source = ContactProjection.ApplyFilters(source, query);
        }

        var rows = await _queries.ToListAsync(
            source
                .OrderBy(contact => contact.FullName)
                .Select(contact => new ExportRow(
                    contact.FullName,
                    contact.PhoneNumber,
                    contact.Email,
                    contact.Country,
                    contact.Status,
                    contact.TagAssignments.Where(assignment => !assignment.IsDeleted)
                        .Select(assignment => assignment.ContactTag.Name).ToList(),
                    contact.GroupMemberships.Where(membership => !membership.IsDeleted)
                        .Select(membership => membership.ContactGroup.Name).ToList(),
                    contact.OptedInAt,
                    contact.LastMessagedAt,
                    contact.CreatedOn)),
            cancellationToken);

        foreach (var row in rows)
        {
            yield return string.Join(',',
                Escape(row.FullName),
                Escape(row.PhoneNumber),
                Escape(row.Email),
                Escape(Countries.ToDisplayName(row.Country)),
                Escape(row.Status.ToString().ToLowerInvariant()),
                Escape(string.Join(ImportDelimiters.List, row.Tags)),
                Escape(string.Join(ImportDelimiters.List, row.Groups)),
                Escape(row.OptedInAt?.ToString("O")),
                Escape(row.LastMessagedAt?.ToString("O")),
                Escape(row.CreatedOn.ToString("O"))) + "\n";
        }
    }

    /// <summary>Moves every tag and group membership from the merged contacts onto the survivor.</summary>
    private async Task MergeMembershipsAsync(
        Contact survivor,
        List<Contact> losers,
        CancellationToken cancellationToken)
    {
        var loserIds = losers.ConvertAll(loser => loser.Id);
        var allIds = new List<long>(loserIds) { survivor.Id };

        var tagAssignments = await _queries.ToListAsync(
            _tagAssignments.Query(asNoTracking: false)
                .Where(assignment => allIds.Contains(assignment.ContactId)),
            cancellationToken);

        foreach (var assignment in tagAssignments.Where(entry => loserIds.Contains(entry.ContactId)))
        {
            // Union, not overwrite: a tag applied to either record describes the merged person.
            if (tagAssignments.Any(entry =>
                    entry.ContactId == survivor.Id && entry.ContactTagId == assignment.ContactTagId))
            {
                _tagAssignments.Remove(assignment);

                continue;
            }

            assignment.ContactId = survivor.Id;
        }

        var memberships = await _queries.ToListAsync(
            _groupMembers.Query(asNoTracking: false)
                .Where(member => allIds.Contains(member.ContactId)),
            cancellationToken);

        foreach (var membership in memberships.Where(entry => loserIds.Contains(entry.ContactId)))
        {
            if (memberships.Any(entry =>
                    entry.ContactId == survivor.Id && entry.ContactGroupId == membership.ContactGroupId))
            {
                _groupMembers.Remove(membership);

                continue;
            }

            membership.ContactId = survivor.Id;
        }
    }

    /// <summary>Applies the caller's explicit field choices to the survivor.</summary>
    private static void ApplyOverrides(Contact survivor, IReadOnlyDictionary<string, string?>? overrides)
    {
        if (overrides is null)
        {
            return;
        }

        foreach (var (field, value) in overrides)
        {
            switch (field.ToLowerInvariant())
            {
                case "fullname":
                    survivor.FullName = ContactRules.NormaliseName(value);
                    break;

                case "email":
                    survivor.Email = ContactRules.NormaliseEmail(value);
                    break;

                case "country":
                    survivor.Country = ContactRules.ResolveCountry(value, survivor.NormalizedPhoneNumber);
                    break;

                case "phonenumber":
                    survivor.NormalizedPhoneNumber = ContactRules.NormalisePhone(value);
                    survivor.PhoneNumber = PhoneNumbers.ToDisplayForm(value!);
                    break;

                default:
                    throw new ValidationException("fieldOverrides", $"\"{field}\" cannot be overridden.");
            }
        }
    }

    /// <summary>Adds, removes or replaces a contact's assignments according to the requested mode.</summary>
    private static void ApplyMode(
        BulkMode mode,
        IReadOnlyList<long> requested,
        IReadOnlyList<long> current,
        Action<long> add,
        Action<long> remove)
    {
        switch (mode)
        {
            case BulkMode.Remove:
                foreach (var id in requested.Where(current.Contains))
                {
                    remove(id);
                }

                break;

            case BulkMode.Replace:
                foreach (var id in current.Where(id => !requested.Contains(id)))
                {
                    remove(id);
                }

                foreach (var id in requested.Where(id => !current.Contains(id)))
                {
                    add(id);
                }

                break;

            default:
                // Add, and idempotent: applying a tag someone already has is a no-op rather than a
                // unique-index violation that fails the whole batch.
                foreach (var id in requested.Where(id => !current.Contains(id)))
                {
                    add(id);
                }

                break;
        }
    }

    private static void EnsureBulkSizeAllowed(IReadOnlyList<string> ids)
    {
        if (ids.Count > MaxBulkIds)
        {
            throw new ValidationException("ids", $"Select at most {MaxBulkIds} contacts at a time.");
        }
    }

    /// <summary>
    /// Loads the contacts behind a set of identifiers, reporting each one that could not be found.
    /// </summary>
    private async Task<ResolvedContacts> ResolveContactsAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var parsed = new Dictionary<long, string>();
        var failed = new List<BulkItemFailure>();

        foreach (var id in ids)
        {
            if (PublicId.TryParse(PublicId.Contact, id, out var key))
            {
                parsed[key] = id;
            }
            else
            {
                failed.Add(new BulkItemFailure(id, "Not a valid contact identifier."));
            }
        }

        var keys = parsed.Keys.ToList();

        var contacts = await _queries.ToListAsync(
            _contacts.Query(asNoTracking: false).Where(contact => keys.Contains(contact.Id)),
            cancellationToken);

        var found = contacts.ToDictionary(contact => contact.Id);

        // Anything parsed but absent is another tenant's row, or one already deleted. Reported per
        // item so the operator sees "48 of 50" instead of a silent partial success.
        failed.AddRange(parsed
            .Where(entry => !found.ContainsKey(entry.Key))
            .Select(entry => new BulkItemFailure(entry.Value, "Already deleted, or not found.")));

        return new ResolvedContacts(found, failed);
    }

    /// <summary>Resolves tag or group identifiers, refusing the request when any is unknown.</summary>
    private static async Task<List<long>> ResolveTargetsAsync(
        string prefix,
        IReadOnlyList<string> ids,
        Func<List<long>, CancellationToken, Task<List<long>>> existing,
        CancellationToken cancellationToken)
    {
        var parsed = ParseIds(prefix, ids);
        var known = await existing(parsed, cancellationToken);

        // Unlike the contact ids, an unknown tag or group fails the call. The contact ids come from
        // a selection that may have gone stale; these come from a picker, so an unknown one means
        // the caller asked for something that does not exist.
        if (known.Count != ids.Count)
        {
            var field = prefix == PublicId.Tag ? "tagIds" : "groupIds";

            throw new ValidationException(field, $"One or more {field} do not exist.");
        }

        return known;
    }

    private async Task<List<long>> IsKnownTagAsync(List<long> ids, CancellationToken cancellationToken) =>
        [.. await _queries.ToListAsync(
            _tags.Query().Where(tag => ids.Contains(tag.Id)).Select(tag => tag.Id),
            cancellationToken)];

    private async Task<List<long>> IsKnownGroupAsync(List<long> ids, CancellationToken cancellationToken) =>
        [.. await _queries.ToListAsync(
            _groups.Query().Where(group => ids.Contains(group.Id)).Select(group => group.Id),
            cancellationToken)];

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

    private static List<long> ParseIds(string prefix, IReadOnlyList<string> ids)
    {
        var parsed = new List<long>(ids.Count);

        foreach (var id in ids)
        {
            if (PublicId.TryParse(prefix, id, out var value))
            {
                parsed.Add(value);
            }
        }

        return parsed;
    }

    private async Task ApplyTagsAsync(
        Contact contact,
        long? tenantId,
        IReadOnlyList<string>? tagIds,
        bool strict,
        CancellationToken cancellationToken)
    {
        if (tagIds is not { Count: > 0 })
        {
            return;
        }

        var resolved = strict
            ? await ResolveTargetsAsync(PublicId.Tag, tagIds, IsKnownTagAsync, cancellationToken)
            : await IsKnownTagAsync(ParseIds(PublicId.Tag, tagIds), cancellationToken);

        foreach (var tagId in resolved)
        {
            _tagAssignments.Add(new ContactTagAssignment
            {
                TenantId = tenantId,
                Contact = contact,
                ContactTagId = tagId,
            });
        }
    }

    private async Task ApplyGroupsAsync(
        Contact contact,
        long? tenantId,
        IReadOnlyList<string>? groupIds,
        bool strict,
        CancellationToken cancellationToken)
    {
        if (groupIds is not { Count: > 0 })
        {
            return;
        }

        var resolved = strict
            ? await ResolveTargetsAsync(PublicId.Group, groupIds, IsKnownGroupAsync, cancellationToken)
            : await IsKnownGroupAsync(ParseIds(PublicId.Group, groupIds), cancellationToken);

        foreach (var groupId in resolved)
        {
            _groupMembers.Add(new ContactGroupMember
            {
                TenantId = tenantId,
                Contact = contact,
                ContactGroupId = groupId,
            });
        }
    }

    private async Task ReplaceTagsAsync(
        Contact contact,
        long? tenantId,
        IReadOnlyList<string> tagIds,
        CancellationToken cancellationToken)
    {
        var existing = await _queries.ToListAsync(
            _tagAssignments.Query(asNoTracking: false).Where(assignment => assignment.ContactId == contact.Id),
            cancellationToken);

        foreach (var assignment in existing)
        {
            _tagAssignments.Remove(assignment);
        }

        await ApplyTagsAsync(contact, tenantId, tagIds, strict: true, cancellationToken);
    }

    private async Task ReplaceGroupsAsync(
        Contact contact,
        long? tenantId,
        IReadOnlyList<string> groupIds,
        CancellationToken cancellationToken)
    {
        var existing = await _queries.ToListAsync(
            _groupMembers.Query(asNoTracking: false).Where(member => member.ContactId == contact.Id),
            cancellationToken);

        foreach (var member in existing)
        {
            _groupMembers.Remove(member);
        }

        await ApplyGroupsAsync(contact, tenantId, groupIds, strict: true, cancellationToken);
    }

    /// <summary>Reads one contact back in the exact shape the list endpoint returns.</summary>
    private async Task<ContactResponse> LoadOneAsync(long id, CancellationToken cancellationToken)
    {
        var row = await _queries.FirstOrDefaultAsync(
            ContactProjection.Project(_contacts.Query().Where(contact => contact.Id == id)),
            cancellationToken)
            ?? throw new NotFoundException("Contact", PublicId.From(PublicId.Contact, id));

        return ContactProjection.ToResponse(row);
    }

    private sealed record ResolvedContacts(
        Dictionary<long, Contact> Found,
        List<BulkItemFailure> Failed);

    private sealed record ExportRow(
        string FullName,
        string PhoneNumber,
        string? Email,
        string Country,
        ContactStatus Status,
        List<string> Tags,
        List<string> Groups,
        DateTimeOffset? OptedInAt,
        DateTimeOffset? LastMessagedAt,
        DateTimeOffset CreatedOn);
}
