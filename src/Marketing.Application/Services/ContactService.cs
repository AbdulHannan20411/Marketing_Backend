using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using Marketing.Application.DTOs.Contacts;
using Marketing.Application.Interfaces;
using Marketing.Business.Extensions;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.Common.Requests;
using Marketing.Common.Responses;
using Marketing.DataAccess.Entities;

namespace Marketing.Application.Services;

/// <inheritdoc cref="IContactService" />
public sealed class ContactService : IContactService
{
    /// <summary>
    /// Sortable columns, matched against the client's <c>sortBy</c>.
    /// <para>
    /// An allow-list, so a client-supplied sort field is matched against known keys and never
    /// reaches the provider as text.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, Expression<Func<Contact, object?>>> SortableColumns =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["fullName"] = contact => contact.FullName,
            ["status"] = contact => contact.Status,
            ["country"] = contact => contact.Country,
            ["createdAt"] = contact => contact.CreatedOn,
            ["lastMessagedAt"] = contact => contact.LastMessagedAt,
        };

    /// <summary>Duplicate groups examined per request, bounding a pathological data set.</summary>
    private const int MaxDuplicateScan = 5000;

    private readonly IRepository<Contact> _contacts;
    private readonly IRepository<ContactGroup> _groups;
    private readonly IRepository<ContactTag> _tags;
    private readonly IQueryExecutor _queries;

    /// <summary>Initialises a new instance.</summary>
    public ContactService(
        IRepository<Contact> contacts,
        IRepository<ContactGroup> groups,
        IRepository<ContactTag> tags,
        IQueryExecutor queries)
    {
        _contacts = contacts;
        _groups = groups;
        _tags = tags;
        _queries = queries;
    }

    /// <inheritdoc />
    public async Task<PagedResult<ContactResponse>> GetContactsAsync(
        ContactQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var source = ContactProjection.ApplyFilters(_contacts.Query(), query);

        // Projected in the database, so only the columns the list needs cross the wire and no
        // entity can escape this layer. Identifiers stay as keys here and are formatted after
        // materialisation, because PublicId is C# the provider cannot translate.
        var projected = ContactProjection.Project(
            source.ApplySort(query, SortableColumns, contact => contact.CreatedOn));

        var page = await _queries.ToPagedAsync(projected, query.Page, query.PageSize, cancellationToken);

        return page.Map(ContactProjection.ToResponse);
    }

    /// <inheritdoc />
    public async Task<ContactResponse> GetContactAsync(
        string contactId,
        CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Contact, contactId, "contact");

        var row = await _queries.FirstOrDefaultAsync(
            ContactProjection.Project(_contacts.Query().Where(contact => contact.Id == id)),
            cancellationToken);

        // The tenant filter has already excluded another tenant's row, so "not found" and "not
        // yours" arrive here identically - which is the point. Distinguishing them would confirm
        // that an identifier exists somewhere else on the platform.
        return row is null
            ? throw new NotFoundException("Contact", contactId)
            : ContactProjection.ToResponse(row);
    }

    /// <inheritdoc />
    public async Task<PagedResult<ContactResponse>> GetGroupMembersAsync(
        string groupId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = PublicId.Parse(PublicId.Group, groupId, "group");

        await EnsureGroupExistsAsync(id, groupId, cancellationToken);

        var source = _contacts.Query()
            .Where(contact => contact.GroupMemberships.Any(membership =>
                !membership.IsDeleted && membership.ContactGroupId == id))
            .WhereMatchesSearch(request.Search);

        return await PageAsync(source, request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PagedResult<ContactResponse>> GetTagMembersAsync(
        string tagId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = PublicId.Parse(PublicId.Tag, tagId, "tag");

        if (await _queries.CountAsync(_tags.Query().Where(tag => tag.Id == id), cancellationToken) == 0)
        {
            throw new NotFoundException("Tag", tagId);
        }

        var source = _contacts.Query()
            .Where(contact => contact.TagAssignments.Any(assignment =>
                !assignment.IsDeleted && assignment.ContactTagId == id))
            .WhereMatchesSearch(request.Search);

        return await PageAsync(source, request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PagedResult<DuplicateGroupResponse>> GetDuplicatesAsync(
        DuplicateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Grouped in the database and materialised as keys only, so a tenant with a large contact
        // book does not pull every row into memory to find the handful that collide.
        var collisions = await CollidingKeysAsync(query.Strategy, cancellationToken);

        var total = collisions.Count;
        var pageKeys = collisions
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        if (pageKeys.Count == 0)
        {
            return PagedResults.Empty<DuplicateGroupResponse>(query.Page, query.PageSize);
        }

        var members = await LoadDuplicateMembersAsync(query.Strategy, pageKeys, cancellationToken);

        var items = pageKeys
            .Select(key => new DuplicateGroupResponse(
                key,
                query.Strategy,
                [.. members.Where(entry => entry.Key == key).Select(entry => entry.Contact)]))
            .ToList();

        return new PagedResult<DuplicateGroupResponse>(items, total, query.Page, query.PageSize);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContactGroupResponse>> GetGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        // Counted in the same query rather than in a second pass, so the number a group reports
        // and the number the contacts list returns for the same filter cannot disagree.
        var projected = _groups.Query()
            .OrderBy(group => group.Name)
            .Select(group => new
            {
                group.Id,
                group.Name,
                group.Description,
                ContactCount = group.Members.Count(member => !member.IsDeleted && !member.Contact.IsDeleted),
                group.CreatedOn,
                group.ModifiedOn,
            });

        var rows = await _queries.ToListAsync(projected, cancellationToken);

        return [.. rows.Select(row => new ContactGroupResponse(
            PublicId.From(PublicId.Group, row.Id),
            row.Name,
            row.Description,
            row.ContactCount,
            row.CreatedOn,
            row.ModifiedOn ?? row.CreatedOn))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContactTagResponse>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        var projected = _tags.Query()
            .OrderBy(tag => tag.Name)
            .Select(tag => new
            {
                tag.Id,
                tag.Name,
                tag.Color,
                ContactCount = tag.Assignments.Count(assignment =>
                    !assignment.IsDeleted && !assignment.Contact.IsDeleted),
                tag.CreatedOn,
            });

        var rows = await _queries.ToListAsync(projected, cancellationToken);

        return [.. rows.Select(row => new ContactTagResponse(
            PublicId.From(PublicId.Tag, row.Id),
            row.Name,
            row.Color,
            row.ContactCount,
            row.CreatedOn))];
    }

    private async Task<PagedResult<ContactResponse>> PageAsync(
        IQueryable<Contact> source,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        var projected = ContactProjection.Project(
            source.ApplySort(request, SortableColumns, contact => contact.CreatedOn));

        var page = await _queries.ToPagedAsync(projected, request.Page, request.PageSize, cancellationToken);

        return page.Map(ContactProjection.ToResponse);
    }

    private async Task EnsureGroupExistsAsync(long id, string groupId, CancellationToken cancellationToken)
    {
        if (await _queries.CountAsync(_groups.Query().Where(group => group.Id == id), cancellationToken) == 0)
        {
            throw new NotFoundException("Group", groupId);
        }
    }

    /// <summary>Returns the values shared by two or more contacts, in a stable order.</summary>
    private async Task<List<string>> CollidingKeysAsync(
        DuplicateStrategy strategy,
        CancellationToken cancellationToken)
    {
        var source = _contacts.Query();

        var grouped = strategy switch
        {
            DuplicateStrategy.Email => source
                .Where(contact => contact.Email != null && contact.Email != string.Empty)
                .GroupBy(contact => contact.Email!),

            DuplicateStrategy.Name => source.GroupBy(contact => contact.FullName),

            // Phone is the default and the only strategy that can be trusted absolutely, because
            // the normalised form is what uniqueness is enforced on.
            _ => source.GroupBy(contact => contact.NormalizedPhoneNumber),
        };

        return [.. await _queries.ToListAsync(
            grouped
                .Where(group => group.Count() > 1)
                .OrderBy(group => group.Key)
                .Select(group => group.Key)
                .Take(MaxDuplicateScan),
            cancellationToken)];
    }

    /// <summary>Loads the contacts behind one page of duplicate keys.</summary>
    private async Task<List<(string Key, ContactResponse Contact)>> LoadDuplicateMembersAsync(
        DuplicateStrategy strategy,
        List<string> keys,
        CancellationToken cancellationToken)
    {
        var source = strategy switch
        {
            DuplicateStrategy.Email => _contacts.Query()
                .Where(contact => contact.Email != null && keys.Contains(contact.Email)),
            DuplicateStrategy.Name => _contacts.Query().Where(contact => keys.Contains(contact.FullName)),
            _ => _contacts.Query().Where(contact => keys.Contains(contact.NormalizedPhoneNumber)),
        };

        var rows = await _queries.ToListAsync(
            ContactProjection.Project(source.OrderBy(contact => contact.CreatedOn)),
            cancellationToken);

        // Regrouped here rather than in the query. The key is already on the row, and asking the
        // provider to emit it a second time as a computed column buys nothing.
        return [.. rows.Select(row => (KeyOf(strategy, row), ContactProjection.ToResponse(row)))];
    }

    private static string KeyOf(DuplicateStrategy strategy, ContactRow row) => strategy switch
    {
        DuplicateStrategy.Email => row.Email ?? string.Empty,
        DuplicateStrategy.Name => row.FullName,
        _ => row.NormalizedPhone,
    };
}
