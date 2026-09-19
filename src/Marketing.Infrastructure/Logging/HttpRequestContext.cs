using Marketing.Common.Constants;
using Marketing.Shared.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Marketing.Infrastructure.Logging;

/// <summary>
/// Exposes correlation metadata for the operation in flight.
/// <para>
/// Outside a request - Quartz jobs, the seeder - the trace identifier is unavailable, so a fresh
/// correlation id is generated per instance. Since the type is scoped, that is one id per job
/// execution, which is exactly the grouping wanted in the logs.
/// </para>
/// </summary>
public sealed class HttpRequestContext : IRequestContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly Lazy<string> _fallbackCorrelationId = new(() => Guid.NewGuid().ToString("N"));

    /// <summary>Initialises a new instance.</summary>
    /// <param name="httpContextAccessor">Accessor for the ambient request.</param>
    public HttpRequestContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public string CorrelationId
    {
        get
        {
            var httpContext = _httpContextAccessor.HttpContext;

            if (httpContext is null)
            {
                return _fallbackCorrelationId.Value;
            }

            if (httpContext.Items.TryGetValue(AppConstants.Headers.CorrelationId, out var stored) &&
                stored is string correlationId &&
                !string.IsNullOrWhiteSpace(correlationId))
            {
                return correlationId;
            }

            return httpContext.TraceIdentifier;
        }
    }

    /// <inheritdoc />
    public string? IpAddress => _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    /// <inheritdoc />
    public string? UserAgent =>
        _httpContextAccessor.HttpContext?.Request.Headers[HeaderNames.UserAgent].ToString() is { Length: > 0 } agent
            ? agent
            : null;

    /// <inheritdoc />
    /// <remarks>
    /// Capped and restricted to identifier characters. It is caller-supplied, and it is stored and
    /// shown to administrators, so it must not be a place to put anything else.
    /// </remarks>
    public string? DeviceId =>
        Header(AppConstants.Headers.DeviceId) is { Length: > 0 and <= 64 } id
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? id
            : null;

    /// <inheritdoc />
    /// <remarks>
    /// Read from the geolocation headers edge networks add - Cloudflare's, or a load balancer set up
    /// to send <c>X-Geo-City</c> and <c>X-Geo-Country</c>. Nothing here looks an address up: that
    /// needs a GeoIP database, and a wrong city is worse than none in a report meant as evidence.
    /// </remarks>
    public string? Location
    {
        get
        {
            var city = Header("CF-IPCity") ?? Header("X-Geo-City");
            var country = Header("CF-IPCountry") ?? Header("X-Geo-Country");

            // Cloudflare reports "XX" and "T1" for unknown and Tor; neither is a place.
            if (country is "XX" or "T1")
            {
                country = null;
            }

            return (city, country) switch
            {
                ({ Length: > 0 }, { Length: > 0 }) => $"{city}, {country}",
                ({ Length: > 0 }, _) => city,
                (_, { Length: > 0 }) => country,
                _ => null,
            };
        }
    }

    private string? Header(string name) =>
        _httpContextAccessor.HttpContext?.Request.Headers[name].ToString() is { Length: > 0 } value
            ? value.Trim()
            : null;
}
