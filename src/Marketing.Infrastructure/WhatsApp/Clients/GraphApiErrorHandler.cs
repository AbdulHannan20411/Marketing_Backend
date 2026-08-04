using System.Net;
using System.Text.Json;
using Marketing.Common.Exceptions;
using Marketing.Infrastructure.WhatsApp.Models;
using Microsoft.Extensions.Logging;

namespace Marketing.Infrastructure.WhatsApp.Clients;

/// <summary>
/// Converts Graph API error responses into <see cref="ExternalServiceException"/>.
/// <para>
/// Placed innermost in the handler chain so the resilience pipeline sees a typed exception whose
/// <c>IsTransient</c> flag already reflects Meta's own error semantics - a rate-limit code is worth
/// retrying, an invalid-parameter code never is.
/// </para>
/// </summary>
public sealed class GraphApiErrorHandler : DelegatingHandler
{
    private const string ServiceName = "MetaWhatsAppCloudApi";

    /// <summary>Meta error codes that indicate throttling rather than a malformed request.</summary>
    private static readonly HashSet<int> ThrottlingCodes = [4, 80007, 130429, 131048, 131056];

    private readonly ILogger<GraphApiErrorHandler> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="logger">Logger.</param>
    public GraphApiErrorHandler(ILogger<GraphApiErrorHandler> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var error = TryParse(body);

        var isTransient =
            response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
            || (int)response.StatusCode >= 500
            || (error?.Code is { } code && ThrottlingCodes.Contains(code));

        _logger.LogError(
            "Meta Graph API call failed. Status: {StatusCode}. Code: {ErrorCode}/{ErrorSubCode}. Trace: {TraceId}.",
            (int)response.StatusCode,
            error?.Code,
            error?.SubCode,
            error?.TraceId);

        // The message goes to the logs, not to the client: Graph error bodies routinely echo back
        // request parameters, which for this platform means recipient phone numbers.
        throw new ExternalServiceException(
            ServiceName,
            $"Graph API returned {(int)response.StatusCode}. Code {error?.Code}, trace {error?.TraceId}.",
            innerException: null,
            isTransient: isTransient);
    }

    private static GraphError? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GraphErrorResponse>(body)?.Error;
        }
        catch (JsonException)
        {
            // Meta occasionally returns an HTML error page from an edge proxy rather than JSON.
            return null;
        }
    }
}
