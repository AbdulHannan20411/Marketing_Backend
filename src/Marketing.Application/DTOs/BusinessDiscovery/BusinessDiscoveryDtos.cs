namespace Marketing.Application.DTOs.BusinessDiscovery;

/// <summary>A business category the picker offers.</summary>
/// <param name="Id">Provider-neutral slug, sent back on search.</param>
/// <param name="Label">Shown to the user.</param>
/// <param name="Group">Optional grouping for the picker.</param>
public sealed record BusinessCategoryResponse(string Id, string Label, string? Group);

/// <summary>A candidate point from geocoding.</summary>
/// <param name="Id">Provider identifier.</param>
/// <param name="Label">Human-readable description.</param>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
/// <param name="Country">Display name of the country, or null.</param>
public sealed record PlaceSuggestionResponse(
    string Id,
    string Label,
    double Latitude,
    double Longitude,
    string? Country);

/// <summary>Request for one page of businesses near a point.</summary>
/// <param name="Latitude">-90 to 90.</param>
/// <param name="Longitude">-180 to 180.</param>
/// <param name="RadiusKm">1 to 50. The ceiling is enforced here, not by the client's dropdown.</param>
/// <param name="Category">A known category id.</param>
/// <param name="Page">One-based.</param>
/// <param name="PageSize">1 to 50.</param>
public sealed record BusinessSearchRequest(
    double Latitude,
    double Longitude,
    double RadiusKm,
    string Category,
    int Page = 1,
    int PageSize = 25);

/// <summary>One discovered business.</summary>
/// <remarks>
/// Every field but the identifier, the name and the coordinates may be null. Null is returned rather
/// than a placeholder: the client renders only what is present, and an invented phone number would
/// be messaged by a campaign.
/// </remarks>
/// <param name="Id">Provider identifier, stable across pages of one search.</param>
/// <param name="Name">Business name.</param>
/// <param name="Phone">Contact number, E.164 where available.</param>
/// <param name="Address">Formatted address.</param>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
/// <param name="Category">What the business is.</param>
/// <param name="Website">Public website.</param>
/// <param name="Rating">Provider rating.</param>
/// <param name="OpeningHours">Human-readable opening hours.</param>
/// <param name="ExistsInContacts">
/// Whether this business is already a contact in the caller's workspace, or null when not checked.
/// </param>
public sealed record DiscoveredBusinessResponse(
    string Id,
    string Name,
    string? Phone,
    string? Address,
    double Latitude,
    double Longitude,
    string? Category,
    string? Website,
    double? Rating,
    string? OpeningHours,
    bool? ExistsInContacts);

/// <summary>One page of discovered businesses.</summary>
/// <param name="Items">The businesses.</param>
/// <param name="Page">One-based page number.</param>
/// <param name="PageSize">Results per page.</param>
/// <param name="Total">Total matches, not the length of this page.</param>
/// <param name="HasNextPage">Whether asking for another page is worthwhile.</param>
/// <param name="SearchId">Identifier echoed back on import so the results can be resolved.</param>
public sealed record BusinessSearchResponse(
    IReadOnlyList<DiscoveredBusinessResponse> Items,
    int Page,
    int PageSize,
    int Total,
    bool HasNextPage,
    string SearchId);

/// <summary>Request to import discovered businesses as contacts.</summary>
/// <remarks>
/// Identifiers, never records. Accepting client-supplied business details would turn this into an
/// unvalidated contact-creation endpoint that bypasses the import pipeline entirely; the ids are
/// resolved against the tenant's own cached search instead.
/// </remarks>
/// <param name="BusinessIds">Provider identifiers from the search.</param>
/// <param name="SearchId">The search they came from.</param>
/// <param name="GroupName">Group to add the contacts to, created if absent. May be null.</param>
public sealed record BusinessImportRequest(
    IReadOnlyList<string> BusinessIds,
    string SearchId,
    string? GroupName);

/// <summary>Why one business could not be imported.</summary>
/// <param name="BusinessId">Provider identifier.</param>
/// <param name="Name">Business name, so the user can recognise it.</param>
/// <param name="Reason">Shown to the user verbatim. Written for an operator, not for a log.</param>
public sealed record BusinessImportFailure(string BusinessId, string Name, string Reason);

/// <summary>Outcome of a business import.</summary>
/// <remarks>
/// <paramref name="Imported"/> + <paramref name="Skipped"/> + <paramref name="Failed"/> always
/// equals the number of identifiers sent. The client shows all three, and a total that does not
/// reconcile reads as lost data.
/// </remarks>
/// <param name="Imported">Contacts created.</param>
/// <param name="Skipped">Already present in the workspace.</param>
/// <param name="Failed">Could not be imported.</param>
/// <param name="Failures">One entry per failure.</param>
public sealed record BusinessImportResponse(
    int Imported,
    int Skipped,
    int Failed,
    IReadOnlyList<BusinessImportFailure> Failures);
