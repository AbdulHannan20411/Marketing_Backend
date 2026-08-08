using Marketing.Common.Requests;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Contacts;

/// <summary>A contact, as the client renders it.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>cnt_</c>.</param>
/// <param name="FullName">Full name.</param>
/// <param name="Initials">Initials, computed server-side.</param>
/// <param name="PhoneNumber">E.164 number, possibly with display spacing.</param>
/// <param name="Email">Email address, when known.</param>
/// <param name="Country">ISO 3166-1 alpha-2 country code.</param>
/// <param name="Status">Marketing consent state.</param>
/// <param name="TagIds">Tag identifiers, prefixed <c>tag_</c>.</param>
/// <param name="GroupIds">Group identifiers, prefixed <c>grp_</c>.</param>
/// <param name="OptedInAt">Instant consent was given.</param>
/// <param name="LastMessagedAt">Instant of the last outbound message.</param>
/// <param name="CreatedAt">Instant the contact was created.</param>
public sealed record ContactResponse(
    string Id,
    string FullName,
    string Initials,
    string PhoneNumber,
    string? Email,
    string Country,
    ContactStatus Status,
    IReadOnlyList<string> TagIds,
    IReadOnlyList<string> GroupIds,
    DateTimeOffset? OptedInAt,
    DateTimeOffset? LastMessagedAt,
    DateTimeOffset CreatedAt);

/// <summary>A named collection of contacts.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>grp_</c>.</param>
/// <param name="Name">Group name.</param>
/// <param name="Description">Description.</param>
/// <param name="ContactCount">Live members, counted server-side.</param>
/// <param name="CreatedAt">Instant the group was created.</param>
/// <param name="UpdatedAt">Instant it was last changed.</param>
public sealed record ContactGroupResponse(
    string Id,
    string Name,
    string Description,
    int ContactCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A label applied to contacts.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>tag_</c>.</param>
/// <param name="Name">Tag name.</param>
/// <param name="Color">Badge colour.</param>
/// <param name="ContactCount">Live contacts carrying the tag.</param>
/// <param name="CreatedAt">Instant the tag was created.</param>
public sealed record ContactTagResponse(
    string Id,
    string Name,
    TagColor Color,
    int ContactCount,
    DateTimeOffset CreatedAt);

/// <summary>
/// Query parameters for the contacts list.
/// <para>
/// <see cref="Status"/> and <see cref="GroupId"/> both accept the literal <c>all</c>, which is what
/// the client sends when a filter is cleared, and both default to it when omitted.
/// </para>
/// </summary>
public class ContactQuery : PageRequest
{
    /// <summary>Sentinel meaning "do not filter".</summary>
    public const string All = "all";

    /// <summary>Rows the contacts table renders per page.</summary>
    public const int TablePageSize = 12;

    /// <summary>Initialises a new instance with the contacts table's page size.</summary>
    public ContactQuery() => SetDefaultPageSize(TablePageSize);

    /// <summary>Consent state to filter by, or <c>all</c>.</summary>
    public string Status { get; init; } = All;

    /// <summary>Group to filter by, or <c>all</c>.</summary>
    public string GroupId { get; init; } = All;

    /// <summary>Tag to filter by, or <c>all</c>.</summary>
    public string TagId { get; init; } = All;
}
