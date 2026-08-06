using Marketing.Common.Constants;
using Marketing.Shared.Abstractions;
using Serilog.Context;

namespace Marketing.API.Middlewares;

/// <summary>
/// Establishes a correlation id for the request and pushes it onto the log context.
/// <para>
/// An inbound id is honoured so a trace started by the browser or an upstream gateway stays intact
/// across the hop; otherwise one is generated. Either way every log entry, audit row and error
/// response for this request carries the same value, which is what makes "the customer says it
/// failed at 14:32" a solvable problem.
/// </para>
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private const int MaxAcceptedLength = 64;

    private readonly RequestDelegate _next;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="next">Next middleware in the pipeline.</param>
    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>Runs the middleware.</summary>
    /// <param name="context">Request context.</param>
    /// <param name="currentUser">Principal, promoted onto the log context.</param>
    /// <param name="tenantContext">Tenant, promoted onto the log context.</param>
    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser, ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = ResolveCorrelationId(context);

        context.Items[AppConstants.Headers.CorrelationId] = correlationId;
        context.TraceIdentifier = correlationId;

        // Written before the response body starts, since headers cannot be set afterwards.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[AppConstants.Headers.CorrelationId] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty(AppConstants.Headers.CorrelationId, correlationId))
        using (LogContext.PushProperty("TenantSlug", tenantContext.TenantSlug))
        using (LogContext.PushProperty("UserId", currentUser.UserId))
        {
            await _next(context);
        }
    }

    /// <summary>
    /// Reads and sanitises the inbound header.
    /// <para>
    /// A client-supplied value is echoed into log entries and response headers, so it is length
    /// capped and restricted to safe characters. Anything else is a header-injection and
    /// log-forging vector.
    /// </para>
    /// </summary>
    private static string ResolveCorrelationId(HttpContext context)
    {
        var inbound = context.Request.Headers[AppConstants.Headers.CorrelationId].ToString();

        if (string.IsNullOrWhiteSpace(inbound) || inbound.Length > MaxAcceptedLength || !IsSafe(inbound))
        {
            return Guid.NewGuid().ToString("N");
        }

        return inbound;
    }

    private static bool IsSafe(string value)
    {
        foreach (var character in value)
        {
            var isAllowed = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.';

            if (!isAllowed)
            {
                return false;
            }
        }

        return true;
    }
}
