namespace Marketing.Application.Interfaces;

/// <summary>A point on the map, named.</summary>
/// <param name="Id">Provider identifier for the place.</param>
/// <param name="Label">Human-readable description, specific enough to tell two same-named places apart.</param>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
/// <param name="Country">Display name of the country, or null if the provider did not say.</param>
public sealed record PlaceSuggestion(
    string Id,
    string Label,
    double Latitude,
    double Longitude,
    string? Country);

/// <summary>A business returned by the provider.</summary>
/// <remarks>
/// Everything but the identifier, the name and the coordinates is nullable, and a provider that
/// does not know a value must leave it null rather than substituting a placeholder. A fabricated
/// phone number here becomes a real message sent to a real stranger further down the pipeline.
/// </remarks>
/// <param name="Id">Provider identifier. Must be stable across pages of one search.</param>
/// <param name="Name">Business name.</param>
/// <param name="Latitude">Degrees north.</param>
/// <param name="Longitude">Degrees east.</param>
/// <param name="Phone">Contact number, E.164 where the provider offers it.</param>
/// <param name="Address">Formatted address.</param>
/// <param name="Category">Provider's own description of what the business is.</param>
/// <param name="Website">Public website.</param>
/// <param name="Rating">Provider rating, on the provider's own scale.</param>
/// <param name="OpeningHours">Human-readable opening hours.</param>
public sealed record ProviderBusiness(
    string Id,
    string Name,
    double Latitude,
    double Longitude,
    string? Phone,
    string? Address,
    string? Category,
    string? Website,
    double? Rating,
    string? OpeningHours);

/// <summary>One page of provider results.</summary>
/// <remarks>
/// Continuation is a token rather than a page number because that is what places providers actually
/// offer: Google, Foursquare and HERE all hand back an opaque cursor, and none of them can be asked
/// for "page 3" without having walked pages 1 and 2. Translating page numbers to cursors is the
/// caller's job, and it needs the cache it already keeps.
/// </remarks>
/// <param name="Items">The businesses.</param>
/// <param name="Total">Total matches the provider reports, not the length of this page.</param>
/// <param name="NextPageToken">Cursor for the following page, or null when there is none.</param>
public sealed record ProviderSearchResult(
    IReadOnlyList<ProviderBusiness> Items,
    int Total,
    string? NextPageToken);

/// <summary>
/// A source of business listings and geocoding.
/// </summary>
/// <remarks>
/// A seam, not an abstraction for its own sake. Provider calls are metered and billed per request,
/// the choice of provider is a commercial decision that can change, and their category taxonomies
/// differ - so everything above this interface deals in the platform's own vocabulary and nothing
/// above it knows which provider is answering.
/// <para>
/// <b>Nothing here is reachable from the browser.</b> The key lives in server configuration; a key
/// shipped to the client is a key published to every customer, and every call spends money.
/// </para>
/// </remarks>
public interface IPlaceProvider
{
    /// <summary>Whether the provider is configured and usable.</summary>
    /// <remarks>
    /// Checked before a request rather than discovered as a failure mid-flight, so an unconfigured
    /// deployment answers "not available here" instead of a provider authentication error the user
    /// cannot act on.
    /// </remarks>
    public bool IsConfigured { get; }

    /// <summary>Turns typed text into candidate points.</summary>
    /// <param name="query">What the user typed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<PlaceSuggestion>> GeocodeAsync(
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>Names a point, or returns null when nothing sensible is nearby.</summary>
    /// <param name="latitude">Degrees north.</param>
    /// <param name="longitude">Degrees east.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PlaceSuggestion?> ReverseGeocodeAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default);

    /// <summary>Finds businesses of one category near a point.</summary>
    /// <param name="latitude">Degrees north.</param>
    /// <param name="longitude">Degrees east.</param>
    /// <param name="radiusKm">Search radius in kilometres.</param>
    /// <param name="providerCategory">The provider's own category token.</param>
    /// <param name="pageSize">Results per page.</param>
    /// <param name="pageToken">Cursor from the previous page, or null for the first.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ProviderSearchResult> SearchAsync(
        double latitude,
        double longitude,
        double radiusKm,
        string providerCategory,
        int pageSize,
        string? pageToken,
        CancellationToken cancellationToken = default);
}
