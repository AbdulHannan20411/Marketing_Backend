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
public sealed partial class GraphApiErrorHandler : DelegatingHandler
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

        LogCallFailed((int)response.StatusCode, error?.Code, error?.SubCode, error?.TraceId);

        // Meta's own words, logged where they will be seen. Graph's message is the one thing that
        // says *why* a call was refused - held back to Debug, a bare "code 100" or "code 131058" sent
        // people into a debugger to learn a template was test-only. The body echoes request
        // parameters, so phone numbers and tokens are masked before it is written.
        var detail = GraphErrorText.Mask(error?.Message);

        if (detail.Length > 0)
        {
            LogErrorDetail(request.RequestUri?.AbsolutePath ?? string.Empty, detail);
        }

        // The message goes to the logs, not to the client: Graph error bodies routinely echo back
        // request parameters, which for this platform means recipient phone numbers.
        throw new ExternalServiceException(
            ServiceName,
            $"Graph API returned {(int)response.StatusCode}. Code {error?.Code}, trace {error?.TraceId}.",
            innerException: null,
            isTransient: isTransient)
        {
            // Carried as a number so a caller can explain the failure without parsing the message.
            ProviderErrorCode = error?.Code,

            // Meta's wording for the person who made the request - "content in this language already
            // exists" - masked like the log line. Null when Meta gave none.
            ProviderUserMessage = GraphErrorText.Mask(error?.UserMessage) is { Length: > 0 } userMessage
                ? userMessage
                : null,
        };
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

    [LoggerMessage(
        EventId = 2420,
        Level = LogLevel.Error,
        Message = "Meta Graph API call failed. Status: {StatusCode}. Code: {ErrorCode}/{ErrorSubCode}. Trace: {TraceId}.")]
    private partial void LogCallFailed(int statusCode, int? errorCode, int? errorSubCode, string? traceId);

    [LoggerMessage(
        EventId = 2421,
        Level = LogLevel.Warning,
        Message = "Meta Graph API error detail for {Path}: {Detail}")]
    private partial void LogErrorDetail(string path, string detail);
}
