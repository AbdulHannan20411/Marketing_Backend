namespace Marketing.Infrastructure.Places;

/// <summary>Places provider settings, bound from the <c>Places</c> section.</summary>
/// <remarks>
/// <para>
/// <b>The key belongs in a secret store, never in <c>appsettings.json</c>.</b> Use
/// <c>dotnet user-secrets</c> locally and the platform's secret manager in every deployed
/// environment; the value is billable and a leaked one is spent by whoever finds it.
/// </para>
/// <para>
/// No <c>[Required]</c> on the key. An unset key is a supported state meaning "this deployment does
/// not offer business discovery", which the endpoints report cleanly - making it required would
/// stop the whole application from starting over a feature most deployments will not use.
/// </para>
/// </remarks>
public sealed class PlacesOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Places";

    /// <summary>Provider API key. Empty disables business discovery.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Tile URL template for the map, handed to the client so no key reaches the bundle.
    /// </summary>
    /// <remarks>
    /// OpenStreetMap's public tiles are the default and are fine for development. Their usage
    /// policy does not permit heavy commercial use, so a licensed provider belongs here before
    /// launch - configured, not compiled in, so the key stays server-side.
    /// </remarks>
    public string TileUrl { get; init; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    /// <summary>Attribution the map must display, as required by the tile provider's terms.</summary>
    public string TileAttribution { get; init; } = "© OpenStreetMap contributors";
}
