using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Marketing.Application.Interfaces;
using Marketing.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marketing.Infrastructure.Places;

/// <summary>
/// Business listings and geocoding from the Google Places API.
/// </summary>
/// <remarks>
/// <para>
/// Uses <c>places:searchText</c> rather than <c>places:searchNearby</c>. Nearby Search returns at
/// most 20 results and offers no continuation at all, which would make the review screen's "load
/// more" a button that never does anything. Text Search takes the same circular restriction and
/// pages, at the cost of expressing the category as text.
/// </para>
/// <para>
/// <b>Google still caps a text search at 60 results across three pages.</b> That ceiling is the
/// provider's and cannot be raised by asking differently, so a dense city centre will report fewer
/// businesses than are really there.
/// </para>
/// <para>
/// The field mask is not decoration: Google bills by which fields are requested, and asking for
/// everything on every call costs several times what asking for these does.
/// </para>
/// </remarks>
public sealed partial class GooglePlacesProvider : IPlaceProvider
{
    private const string SearchTextUrl = "https://places.googleapis.com/v1/places:searchText";
    private const string GeocodeUrl = "https://maps.googleapis.com/maps/api/geocode/json";

    /// <summary>Fields requested from a text search. Every addition here has a price.</summary>
    private const string SearchFieldMask =
        "places.id,places.displayName,places.formattedAddress,places.location,"
        + "places.nationalPhoneNumber,places.internationalPhoneNumber,places.websiteUri,"
        + "places.rating,places.primaryTypeDisplayName,places.regularOpeningHours.weekdayDescriptions,"
        + "nextPageToken";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly PlacesOptions _options;
    private readonly ILogger<GooglePlacesProvider> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="client">HTTP client.</param>
    /// <param name="options">Provider settings, including the key.</param>
    /// <param name="logger">Logger.</param>
    public GooglePlacesProvider(
        HttpClient client,
        IOptions<PlacesOptions> options,
        ILogger<GooglePlacesProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlaceSuggestion>> GeocodeAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var url = $"{GeocodeUrl}?address={Uri.EscapeDataString(query)}&key={_options.ApiKey}";

        var payload = await SendAsync<GeocodeResponse>(
            new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken);

        return [.. (payload?.Results ?? []).Select(ToSuggestion)];
    }

    /// <inheritdoc />
    public async Task<PlaceSuggestion?> ReverseGeocodeAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        var point = string.Create(CultureInfo.InvariantCulture, $"{latitude:F6},{longitude:F6}");
        var url = $"{GeocodeUrl}?latlng={point}&key={_options.ApiKey}";

        var payload = await SendAsync<GeocodeResponse>(
            new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken);

        var first = payload?.Results?.FirstOrDefault();

        // Null is a normal answer for a pin in open country, not an error.
        return first is null ? null : ToSuggestion(first);
    }

    /// <inheritdoc />
    public async Task<ProviderSearchResult> SearchAsync(
        double latitude,
        double longitude,
        double radiusKm,
        string providerCategory,
        int pageSize,
        string? pageToken,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, SearchTextUrl)
        {
            Content = JsonContent.Create(
                new SearchTextRequest(
                    providerCategory.Replace('_', ' '),
                    // Restriction rather than bias: a bias is a hint Google may ignore, and a user
                    // who asked for businesses within 5 km does not want one from the next city.
                    new LocationRestriction(new Circle(
                        new LatLng(latitude, longitude),
                        Math.Min(radiusKm * 1000, 50_000))),
                    Math.Clamp(pageSize, 1, 20),
                    pageToken),
                options: Json),
        };

        request.Headers.Add("X-Goog-Api-Key", _options.ApiKey);
        request.Headers.Add("X-Goog-FieldMask", SearchFieldMask);

        var payload = await SendAsync<SearchTextResponse>(request, cancellationToken);
        var places = payload?.Places ?? [];

        // Google reports no total. The count in hand is the only honest number, and inflating it
        // would have the client promise results that do not exist.
        return new ProviderSearchResult(
            [.. places.Select(ToBusiness)],
            places.Count,
            payload?.NextPageToken);
    }

    /// <summary>Sends a request, translating provider failures into platform ones.</summary>
    /// <remarks>
    /// The provider's own error text never escapes this method. It reaches an end user, and which
    /// supplier the platform buys from - and how much of its quota is left - is not their business.
    /// </remarks>
    private async Task<TResponse?> SendAsync<TResponse>(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        using var response = await _client.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<TResponse>(Json, cancellationToken);
        }

        var status = response.StatusCode;

        LogProviderFailed((int)status, request.RequestUri?.AbsolutePath ?? string.Empty);

        // 429 from Google is the platform's own quota, not this user's - they have their own
        // allowance and it is counted separately. Reported as a billing state so the distinction
        // survives into metrics and the user is told to contact support rather than to wait.
        if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired)
        {
            throw new ProviderQuotaException(
                "provider_quota_exceeded",
                "Business search is temporarily unavailable on this workspace. Please contact support.");
        }

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new BusinessRuleException(
                "provider_not_configured",
                "Business search is not available on this workspace yet.");
        }

        throw new ExternalServiceException(
            "Places",
            "The business directory could not be reached. Please try again.");
    }

    private static PlaceSuggestion ToSuggestion(GeocodeResult result) =>
        new(
            result.PlaceId ?? result.FormattedAddress ?? Guid.NewGuid().ToString("N"),
            result.FormattedAddress ?? "Unknown location",
            result.Geometry?.Location?.Lat ?? 0,
            result.Geometry?.Location?.Lng ?? 0,
            result.AddressComponents
                ?.FirstOrDefault(component => component.Types?.Contains("country") == true)
                ?.LongName);

    private static ProviderBusiness ToBusiness(Place place) =>
        new(
            place.Id,
            place.DisplayName?.Text ?? "Unnamed business",
            place.Location?.Latitude ?? 0,
            place.Location?.Longitude ?? 0,

            // International first: it is already close to E.164, where the national form needs a
            // country to interpret it and would be normalised into the wrong number without one.
            place.InternationalPhoneNumber ?? place.NationalPhoneNumber,
            place.FormattedAddress,
            place.PrimaryTypeDisplayName?.Text,
            place.WebsiteUri,
            place.Rating,
            place.RegularOpeningHours?.WeekdayDescriptions is { Count: > 0 } hours
                ? string.Join("; ", hours)
                : null);

    [LoggerMessage(
        EventId = 3601,
        Level = LogLevel.Warning,
        Message = "Places provider returned {StatusCode} for {Path}.")]
    private partial void LogProviderFailed(int statusCode, string path);

    private sealed record SearchTextRequest(
        [property: JsonPropertyName("textQuery")] string TextQuery,
        [property: JsonPropertyName("locationRestriction")] LocationRestriction LocationRestriction,
        [property: JsonPropertyName("pageSize")] int PageSize,
        [property: JsonPropertyName("pageToken")] string? PageToken);

    private sealed record LocationRestriction(
        [property: JsonPropertyName("circle")] Circle Circle);

    private sealed record Circle(
        [property: JsonPropertyName("center")] LatLng Center,
        [property: JsonPropertyName("radius")] double Radius);

    private sealed record LatLng(
        [property: JsonPropertyName("latitude")] double Latitude,
        [property: JsonPropertyName("longitude")] double Longitude);

    private sealed record SearchTextResponse(List<Place>? Places, string? NextPageToken);

    private sealed record Place(
        string Id,
        LocalizedText? DisplayName,
        string? FormattedAddress,
        PlaceLocation? Location,
        string? NationalPhoneNumber,
        string? InternationalPhoneNumber,
        string? WebsiteUri,
        double? Rating,
        LocalizedText? PrimaryTypeDisplayName,
        OpeningHours? RegularOpeningHours);

    private sealed record LocalizedText(string? Text);

    private sealed record PlaceLocation(double Latitude, double Longitude);

    private sealed record OpeningHours(List<string>? WeekdayDescriptions);

    private sealed record GeocodeResponse(List<GeocodeResult>? Results);

    private sealed record GeocodeResult(
        [property: JsonPropertyName("place_id")] string? PlaceId,
        [property: JsonPropertyName("formatted_address")] string? FormattedAddress,
        Geometry? Geometry,
        [property: JsonPropertyName("address_components")] List<AddressComponent>? AddressComponents);

    private sealed record Geometry(GeoLocation? Location);

    private sealed record GeoLocation(double Lat, double Lng);

    private sealed record AddressComponent(
        [property: JsonPropertyName("long_name")] string? LongName,
        List<string>? Types);
}
