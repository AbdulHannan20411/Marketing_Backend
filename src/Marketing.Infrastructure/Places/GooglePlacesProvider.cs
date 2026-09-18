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
/// more" a button that never does anything. Text Search pages, at the cost of expressing the category
/// as text - and of restricting only to a rectangle, never a circle. See <see cref="SearchArea"/>.
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

    /// <summary>Widest radius sent to the provider, matching Google's own 50 km ceiling.</summary>
    private const double MaximumRadiusKm = 50;
    private const string SearchNearbyUrl = "https://places.googleapis.com/v1/places:searchNearby";

    /// <summary>
    /// Fields a place lookup asks for. Narrower than a business search: a suggestion needs a name, a
    /// point and a country, and every extra field is charged for.
    /// </summary>
    private const string PlaceFieldMask =
        "places.id,places.displayName,places.formattedAddress,places.location,places.addressComponents";

    /// <summary>Suggestions offered for one search. A dropdown nobody scrolls past five is enough.</summary>
    private const int SuggestionCount = 5;

    /// <summary>
    /// How far a dropped pin may be from something with a name, in metres.
    /// </summary>
    /// <remarks>
    /// Wide enough to name a pin dropped in a street or a park, narrow enough that the answer is
    /// somewhere the person can see. Beyond this the honest answer is no name at all, which the
    /// client already renders as bare coordinates.
    /// </remarks>
    private const double ReverseLookupRadiusMetres = 400;

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
        EnsureConfigured();

        var request = new HttpRequestMessage(HttpMethod.Post, SearchTextUrl)
        {
            Content = JsonContent.Create(new PlaceLookupRequest(query, SuggestionCount), options: Json),
        };

        request.Headers.Add("X-Goog-Api-Key", _options.ApiKey);
        request.Headers.Add("X-Goog-FieldMask", PlaceFieldMask);

        var payload = await SendAsync<SearchTextResponse>(request, cancellationToken);

        return [.. (payload?.Places ?? []).Select(ToSuggestion)];
    }

    /// <inheritdoc />
    public async Task<PlaceSuggestion?> ReverseGeocodeAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var request = new HttpRequestMessage(HttpMethod.Post, SearchNearbyUrl)
        {
            Content = JsonContent.Create(
                new PlaceNearbyRequest(
                    new NearbyRestriction(new Circle(
                        new LatLng(latitude, longitude),
                        ReverseLookupRadiusMetres)),

                    // Nearest first and one result: naming a pin wants the closest thing to it, not
                    // the most prominent thing in the area.
                    "DISTANCE",
                    1),
                options: Json),
        };

        request.Headers.Add("X-Goog-Api-Key", _options.ApiKey);
        request.Headers.Add("X-Goog-FieldMask", PlaceFieldMask);

        var payload = await SendAsync<SearchTextResponse>(request, cancellationToken);
        var nearest = payload?.Places is { Count: > 0 } places ? places[0] : null;

        // Null is a normal answer for a pin in open country, not an error.
        return nearest is null ? null : ToSuggestion(nearest);
    }

    /// <summary>
    /// Refuses a lookup this deployment cannot make, before a request is built.
    /// </summary>
    /// <remarks>
    /// Matches the business search, so a deployment with no key answers "not available here" on every
    /// endpoint of the feature rather than an empty dropdown on one of them - which is exactly how a
    /// denied key spent a week looking like a typo.
    /// </remarks>
    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new BusinessRuleException(
                "provider_not_configured",
                "Location search is not available on this workspace yet.");
        }
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
        var radius = Math.Min(radiusKm, MaximumRadiusKm);
        var bounds = SearchArea.Bounds(latitude, longitude, radius);

        var request = new HttpRequestMessage(HttpMethod.Post, SearchTextUrl)
        {
            Content = JsonContent.Create(
                new SearchTextRequest(
                    providerCategory.Replace('_', ' '),

                    // Restriction rather than bias: a bias is a hint Google may ignore, and a user who
                    // asked for businesses within 5 km does not want one from the next city. It has to
                    // be a rectangle - Text Search rejects a circle here as an unknown field, which is
                    // what made every search fail with a 400 - so the corners are trimmed off below.
                    new LocationRestriction(new Rectangle(
                        new LatLng(bounds.LowLatitude, bounds.LowLongitude),
                        new LatLng(bounds.HighLatitude, bounds.HighLongitude))),
                    Math.Clamp(pageSize, 1, 20),
                    pageToken),
                options: Json),
        };

        request.Headers.Add("X-Goog-Api-Key", _options.ApiKey);
        request.Headers.Add("X-Goog-FieldMask", SearchFieldMask);

        var payload = await SendAsync<SearchTextResponse>(request, cancellationToken);

        // Trimmed back to the circle the user asked for. The rectangle reaches past it at the corners,
        // and a missing location reads as (0, 0), which is outside any real search too - a business
        // that cannot be placed cannot be shown to be within range.
        var businesses = (payload?.Places ?? [])
            .Select(ToBusiness)
            .Where(business => SearchArea.Contains(latitude, longitude, radius, business.Latitude, business.Longitude))
            .ToList();

        // Google reports no total. The count in hand is the only honest number, and inflating it
        // would have the client promise results that do not exist. Paging continues from Google's
        // token, not from this count, so a page trimmed short still offers the next one.
        return new ProviderSearchResult(businesses, businesses.Count, payload?.NextPageToken);
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

        // Logged for whoever reads the logs, and never returned to the caller. Without Google's own
        // reason, a rejected request is indistinguishable from an outage, which is how a malformed
        // search survived until the first real key was configured.
        LogProviderFailed(
            (int)status,
            request.RequestUri?.AbsolutePath ?? string.Empty,
            await ReadProviderErrorAsync(response, cancellationToken));

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

    private static PlaceSuggestion ToSuggestion(Place place)
    {
        // Read once, so the name and the code are always taken from the same component.
        var country = place.AddressComponents
            ?.FirstOrDefault(component => component.Types?.Contains("country") == true);

        return new PlaceSuggestion(
            place.Id,

            // The full address, not the display name: two towns share a name often enough that a
            // dropdown of bare names is a guess, and "Hyderabad, Pakistan" is not "Hyderabad, India".
            place.FormattedAddress ?? place.DisplayName?.Text ?? "Unknown location",
            place.Location?.Latitude ?? 0,
            place.Location?.Longitude ?? 0,
            country?.LongText,

            // Google's short name for a country component is its ISO 3166-1 alpha-2 code, which the
            // CSV export and the phone-number expansion both read.
            country?.ShortText);
    }

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

    /// <summary>Google's own explanation of a failed call, or empty when it gave none.</summary>
    private static async Task<string> ReadProviderErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ProviderErrorEnvelope>(Json, cancellationToken);
            var message = body?.Error?.Message;

            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            return message.Length <= 300 ? message : message[..300];
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or HttpRequestException)
        {
            // Diagnostic only. A body that cannot be read must not turn a provider failure into a
            // different, more confusing one.
            return string.Empty;
        }
    }

    [LoggerMessage(
        EventId = 3601,
        Level = LogLevel.Warning,
        Message = "Places provider returned {StatusCode} for {Path}: {Detail}")]
    private partial void LogProviderFailed(int statusCode, string path, string detail);

    private sealed record SearchTextRequest(
        [property: JsonPropertyName("textQuery")] string TextQuery,
        [property: JsonPropertyName("locationRestriction")] LocationRestriction LocationRestriction,
        [property: JsonPropertyName("pageSize")] int PageSize,

        // Omitted on the first page rather than sent as null.
        [property: JsonPropertyName("pageToken"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? PageToken);

    private sealed record LocationRestriction(
        [property: JsonPropertyName("rectangle")] Rectangle Rectangle);

    private sealed record Rectangle(
        [property: JsonPropertyName("low")] LatLng Low,
        [property: JsonPropertyName("high")] LatLng High);

    private sealed record ProviderErrorEnvelope(ProviderError? Error);

    private sealed record ProviderError(string? Message);

    private sealed record LatLng(
        [property: JsonPropertyName("latitude")] double Latitude,
        [property: JsonPropertyName("longitude")] double Longitude);

    private sealed record SearchTextResponse(List<Place>? Places, string? NextPageToken);

    private sealed record Place(
        string Id,
        LocalizedText? DisplayName,
        string? FormattedAddress,
        PlaceLocation? Location,
        List<AddressComponent>? AddressComponents,
        string? NationalPhoneNumber,
        string? InternationalPhoneNumber,
        string? WebsiteUri,
        double? Rating,
        LocalizedText? PrimaryTypeDisplayName,
        OpeningHours? RegularOpeningHours);

    private sealed record LocalizedText(string? Text);

    private sealed record PlaceLocation(double Latitude, double Longitude);

    private sealed record OpeningHours(List<string>? WeekdayDescriptions);

    /// <summary>One component of a place's address, as the Places API returns it.</summary>
    private sealed record AddressComponent(
        [property: JsonPropertyName("longText")] string? LongText,
        [property: JsonPropertyName("shortText")] string? ShortText,
        List<string>? Types);

    private sealed record PlaceLookupRequest(
        [property: JsonPropertyName("textQuery")] string TextQuery,
        [property: JsonPropertyName("pageSize")] int PageSize);

    private sealed record PlaceNearbyRequest(
        [property: JsonPropertyName("locationRestriction")] NearbyRestriction LocationRestriction,
        [property: JsonPropertyName("rankPreference")] string RankPreference,
        [property: JsonPropertyName("maxResultCount")] int MaxResultCount);

    private sealed record NearbyRestriction(
        [property: JsonPropertyName("circle")] Circle Circle);

    private sealed record Circle(
        [property: JsonPropertyName("center")] LatLng Center,

        // Metres, unlike every other distance in this file. Google's units, not the platform's.
        [property: JsonPropertyName("radius")] double Radius);
}
