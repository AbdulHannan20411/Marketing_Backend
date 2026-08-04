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

            if (httpContext.Items.TryGetValue(ApplicationHeaderNames.CorrelationId, out var stored) &&
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
}
