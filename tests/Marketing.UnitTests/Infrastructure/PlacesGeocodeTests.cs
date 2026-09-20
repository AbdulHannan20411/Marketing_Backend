using System.Net;
using System.Text;
using AwesomeAssertions;
using Marketing.Common.Exceptions;
using Marketing.Infrastructure.Places;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Marketing.UnitTests.Infrastructure;

/// <summary>
/// Turning typed text and dropped pins into places, through the Places API rather than the legacy
/// Geocoding API.
/// </summary>
/// <remarks>
/// Written after location search returned an empty dropdown for every query. The key was fine and
/// business search worked; the legacy Geocoding API was refused for want of billing, and it reports
/// that refusal as HTTP 200 with a status in the body - so "denied" and "no such place" arrived
/// looking identical. Both lookups now use the API the key already works against.
/// </remarks>
public sealed class PlacesGeocodeTests
{
    private const string LahoreSearchResponse =
        """
        {"places":[{"id":"ChIJ2QeB5YMEGTkRYiR-zGy-OsI","displayName":{"text":"Lahore"},
        "formattedAddress":"Lahore, Punjab, Pakistan",
        "location":{"latitude":31.5203696,"longitude":74.358749},
        "addressComponents":[{"longText":"Lahore","shortText":"Lahore","types":["locality"]},
        {"longText":"Pakistan","shortText":"PK","types":["country","political"]}]}]}
        """;

    private sealed class CannedResponse(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (GooglePlacesProvider Provider, CannedResponse Handler) Create(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = "{}",
        string apiKey = "test-key")
    {
        var handler = new CannedResponse(status, body);

        var provider = new GooglePlacesProvider(
            new HttpClient(handler),
            Options.Create(new PlacesOptions { ApiKey = apiKey }),
            NullLogger<GooglePlacesProvider>.Instance,

            // A cache of its own per provider, so one test never answers another test's call.
            new MemoryCache(new MemoryCacheOptions()));

        return (provider, handler);
    }

    [Fact]
    public async Task A_search_goes_to_the_places_api_not_the_legacy_geocoder()
    {
        var (provider, handler) = Create(body: LahoreSearchResponse);

        await provider.GeocodeAsync("lahore", TestContext.Current.CancellationToken);

        var request = handler.Requests.Should().ContainSingle().Which;

        // The whole point of the change: this key is refused on maps.googleapis.com and accepted here.
        request.RequestUri!.Host.Should().Be("places.googleapis.com");
        request.Headers.GetValues("X-Goog-Api-Key").Should().ContainSingle("test-key");
        request.Headers.GetValues("X-Goog-FieldMask").Should().ContainSingle(mask =>
            mask.Contains("places.location") && mask.Contains("places.addressComponents"));
    }

    [Fact]
    public async Task A_place_becomes_the_suggestion_the_client_expects()
    {
        var (provider, _) = Create(body: LahoreSearchResponse);

        var suggestion = (await provider.GeocodeAsync("lahore", TestContext.Current.CancellationToken))
            .Should().ContainSingle().Which;

        suggestion.Id.Should().Be("ChIJ2QeB5YMEGTkRYiR-zGy-OsI");

        // The full address, not the bare name: two towns share a name often enough that a dropdown of
        // names alone is a guess.
        suggestion.Label.Should().Be("Lahore, Punjab, Pakistan");
        suggestion.Latitude.Should().BeApproximately(31.5203696, 0.000001);
        suggestion.Longitude.Should().BeApproximately(74.358749, 0.000001);
        suggestion.Country.Should().Be("Pakistan");

        // ISO 3166-1 alpha-2, which the CSV export and the phone-number expansion both read.
        suggestion.CountryCode.Should().Be("PK");
    }

    [Fact]
    public async Task A_refused_key_is_an_error_the_user_can_act_on_rather_than_an_empty_list()
    {
        // The failure this whole change exists for. Google refuses with a status, and an empty list
        // told the person their spelling was wrong.
        var (provider, _) = Create(
            HttpStatusCode.Forbidden,
            """{"error":{"code":403,"message":"Requests to this API are blocked.","status":"PERMISSION_DENIED"}}""");

        var search = () => provider.GeocodeAsync("lahore", TestContext.Current.CancellationToken);

        (await search.Should().ThrowAsync<BusinessRuleException>()).Which.ErrorCode
            .Should().Be("provider_not_configured");
    }

    [Fact]
    public async Task An_exhausted_quota_is_reported_as_one()
    {
        var (provider, _) = Create(
            HttpStatusCode.TooManyRequests,
            """{"error":{"code":429,"message":"Quota exceeded.","status":"RESOURCE_EXHAUSTED"}}""");

        var search = () => provider.GeocodeAsync("lahore", TestContext.Current.CancellationToken);

        (await search.Should().ThrowAsync<ProviderQuotaException>()).Which.ErrorCode
            .Should().Be("provider_quota_exceeded");
    }

    [Fact]
    public async Task A_provider_fault_is_transient_rather_than_a_configuration_problem()
    {
        var (provider, _) = Create(HttpStatusCode.ServiceUnavailable, "{}");

        var search = () => provider.GeocodeAsync("lahore", TestContext.Current.CancellationToken);

        await search.Should().ThrowAsync<ExternalServiceException>();
    }

    [Fact]
    public async Task A_deployment_without_a_key_never_calls_google_at_all()
    {
        var (provider, handler) = Create(apiKey: "");

        var search = () => provider.GeocodeAsync("lahore", TestContext.Current.CancellationToken);

        (await search.Should().ThrowAsync<BusinessRuleException>()).Which.ErrorCode
            .Should().Be("provider_not_configured");

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_matching_is_still_an_empty_list()
    {
        // A real "no such place" has to stay distinguishable from a refusal, which is now an error.
        var (provider, _) = Create(body: """{"places":[]}""");

        (await provider.GeocodeAsync("qqqqqqq", TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_dropped_pin_is_named_by_the_nearest_place()
    {
        var (provider, handler) = Create(body: LahoreSearchResponse);

        var named = await provider.ReverseGeocodeAsync(31.52, 74.35, TestContext.Current.CancellationToken);

        named.Should().NotBeNull();
        named!.CountryCode.Should().Be("PK");

        handler.Requests.Should().ContainSingle().Which.RequestUri!.AbsolutePath
            .Should().EndWith("places:searchNearby");
    }

    [Fact]
    public async Task A_pin_in_open_country_has_no_name_and_that_is_not_a_failure()
    {
        var (provider, _) = Create(body: """{"places":[]}""");

        var named = await provider.ReverseGeocodeAsync(31.52, 74.35, TestContext.Current.CancellationToken);

        // The client shows the pin unnamed and searches by coordinates, which is the part that matters.
        named.Should().BeNull();
    }
}
