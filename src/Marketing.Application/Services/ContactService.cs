using System.Linq.Expressions;
using Marketing.Application.DTOs.Contacts;
using Marketing.Application.Interfaces;
using Marketing.Business.Extensions;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Helpers;
using Marketing.Common.Responses;
using Marketing.DataAccess.Entities;
using static Marketing.Common.Constants.ContractEnums;

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

        var source = _contacts.Query();

        source = source.WhereMatchesSearch(query.Search);
        source = ApplyStatusFilter(source, query.Status);
        source = ApplyGroupFilter(source, query.GroupId);

        // Projected in the database, so only the columns the list needs cross the wire and no
        // entity can escape this layer. Identifiers stay as keys here and are formatted after
        // materialisation, because PublicId is C# the provider cannot translate.
        var projected = source
            .ApplySort(query, SortableColumns, contact => contact.CreatedOn)
            .Select(contact => new ContactRow(
                contact.Id,
                contact.FullName,
                contact.PhoneNumber,
                contact.Email,
                contact.Country,
                contact.Status,
                contact.TagAssignments.Where(assignment => !assignment.IsDeleted)
                    .Select(assignment => assignment.ContactTagId).ToList(),
                contact.GroupMemberships.Where(membership => !membership.IsDeleted)
                    .Select(membership => membership.ContactGroupId).ToList(),
                contact.OptedInAt,
                contact.LastMessagedAt,
                contact.CreatedOn));

        var page = await _queries.ToPagedAsync(projected, query.PageNumber, query.PageSize, cancellationToken);

        return page.Map(row => new ContactResponse(
            PublicId.From(PublicId.Contact, row.Id),
            row.FullName,
            Initials.From(row.FullName),
            row.PhoneNumber,
            row.Email,
            row.Country,
            row.Status,
            [.. row.TagIds.Select(id => PublicId.From(PublicId.Tag, id))],
            [.. row.GroupIds.Select(id => PublicId.From(PublicId.Group, id))],
            row.OptedInAt,
            row.LastMessagedAt,
            row.CreatedOn));
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

    /// <summary>Applies the status filter, treating <c>all</c> and anything unrecognised as no filter.</summary>
    private static IQueryable<Contact> ApplyStatusFilter(IQueryable<Contact> source, string? status)
    {
        if (string.IsNullOrWhiteSpace(status)
            || string.Equals(status, ContactQuery.All, StringComparison.OrdinalIgnoreCase)
            || !Enum.TryParse<ContactStatus>(status, ignoreCase: true, out var parsed))
        {
            return source;
        }

        return source.Where(contact => contact.Status == parsed);
    }

    /// <summary>Applies the group filter, treating <c>all</c> as no filter.</summary>
    private static IQueryable<Contact> ApplyGroupFilter(IQueryable<Contact> source, string? groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)
            || string.Equals(groupId, ContactQuery.All, StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        // An unparseable group cannot match anything, so return an empty set rather than silently
        // ignoring the filter and showing the caller every contact they have.
        if (!PublicId.TryParse(PublicId.Group, groupId, out var parsed))
        {
            return source.Where(_ => false);
        }

        return source.Where(contact =>
            contact.GroupMemberships.Any(membership =>
                !membership.IsDeleted && membership.ContactGroupId == parsed));
    }

    /// <summary>Database-shaped projection, before identifiers are formatted for the wire.</summary>
    private sealed record ContactRow(
        Guid Id,
        string FullName,
        string PhoneNumber,
        string? Email,
        string Country,
        ContactStatus Status,
        List<Guid> TagIds,
        List<Guid> GroupIds,
        DateTimeOffset? OptedInAt,
        DateTimeOffset? LastMessagedAt,
        DateTimeOffset CreatedOn);
}
