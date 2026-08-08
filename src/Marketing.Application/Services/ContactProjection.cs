using Marketing.Application.DTOs.Contacts;
using Marketing.Business.Extensions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <summary>
/// The one place a <see cref="Contact"/> becomes a <see cref="ContactResponse"/>, and the one place
/// the list filters are composed.
/// <para>
/// Shared deliberately. The same contact shape is returned by the list, the single read, the group
/// member list, the tag member list, the duplicate report and the merge result; the same filter set
/// is used by the list and the export. Six copies of a projection is how a field ends up populated
/// on one screen and blank on another.
/// </para>
/// </summary>
internal static class ContactProjection
{
    /// <summary>
    /// Applies search and every list filter.
    /// <para>
    /// Each filter treats the literal <c>all</c> - which is what the client sends when a filter is
    /// cleared - as "no filter".
    /// </para>
    /// </summary>
    /// <param name="source">Contacts to filter.</param>
    /// <param name="query">Search and filter values.</param>
    public static IQueryable<Contact> ApplyFilters(IQueryable<Contact> source, ContactQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        source = source.WhereMatchesSearch(query.Search);
        source = ApplyStatusFilter(source, query.Status);
        source = ApplyGroupFilter(source, query.GroupId);
        source = ApplyTagFilter(source, query.TagId);

        return source;
    }

    /// <summary>Projects to the database-shaped row the response is built from.</summary>
    /// <param name="source">Contacts to project.</param>
    public static IQueryable<ContactRow> Project(IQueryable<Contact> source) =>
        source.Select(contact => new ContactRow(
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
            contact.CreatedOn,
            contact.NormalizedPhoneNumber));

    /// <summary>Formats a materialised row for the wire.</summary>
    /// <param name="row">Row read from the database.</param>
    public static ContactResponse ToResponse(ContactRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ContactResponse(
            PublicId.From(PublicId.Contact, row.Id),
            row.FullName,
            Initials.From(row.FullName),
            row.PhoneNumber,
            row.Email,
            // Stored as an ISO code, returned as the name the client renders verbatim.
            Countries.ToDisplayName(row.Country),
            row.Status,
            [.. row.TagIds.Select(id => PublicId.From(PublicId.Tag, id))],
            [.. row.GroupIds.Select(id => PublicId.From(PublicId.Group, id))],
            row.OptedInAt,
            row.LastMessagedAt,
            row.CreatedOn);
    }

    private static IQueryable<Contact> ApplyStatusFilter(IQueryable<Contact> source, string? status)
    {
        if (IsUnfiltered(status) || !Enum.TryParse<ContactStatus>(status, ignoreCase: true, out var parsed))
        {
            return source;
        }

        return source.Where(contact => contact.Status == parsed);
    }

    private static IQueryable<Contact> ApplyGroupFilter(IQueryable<Contact> source, string? groupId)
    {
        if (IsUnfiltered(groupId))
        {
            return source;
        }

        // An unparseable group cannot match anything, so return an empty set rather than silently
        // ignoring the filter and showing the caller every contact they have.
        if (!PublicId.TryParse(PublicId.Group, groupId!, out var parsed))
        {
            return source.Where(_ => false);
        }

        return source.Where(contact =>
            contact.GroupMemberships.Any(membership =>
                !membership.IsDeleted && membership.ContactGroupId == parsed));
    }

    private static IQueryable<Contact> ApplyTagFilter(IQueryable<Contact> source, string? tagId)
    {
        if (IsUnfiltered(tagId))
        {
            return source;
        }

        if (!PublicId.TryParse(PublicId.Tag, tagId!, out var parsed))
        {
            return source.Where(_ => false);
        }

        return source.Where(contact =>
            contact.TagAssignments.Any(assignment =>
                !assignment.IsDeleted && assignment.ContactTagId == parsed));
    }

    private static bool IsUnfiltered(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value, ContactQuery.All, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Database-shaped contact projection, before identifiers are formatted for the wire.</summary>
/// <param name="Id">Primary key.</param>
/// <param name="FullName">Full name.</param>
/// <param name="PhoneNumber">Display-form number.</param>
/// <param name="Email">Email address.</param>
/// <param name="Country">Stored country code.</param>
/// <param name="Status">Consent state.</param>
/// <param name="TagIds">Tag keys.</param>
/// <param name="GroupIds">Group keys.</param>
/// <param name="OptedInAt">Consent instant.</param>
/// <param name="LastMessagedAt">Last outbound message.</param>
/// <param name="CreatedOn">Creation instant.</param>
/// <param name="NormalizedPhone">
/// Digits-only number. Carried for duplicate grouping, never returned - the response exposes the
/// display form only.
/// </param>
internal sealed record ContactRow(
    long Id,
    string FullName,
    string PhoneNumber,
    string? Email,
    string Country,
    ContactStatus Status,
    List<long> TagIds,
    List<long> GroupIds,
    DateTimeOffset? OptedInAt,
    DateTimeOffset? LastMessagedAt,
    DateTimeOffset CreatedOn,
    string NormalizedPhone);
