using Marketing.Application.Services.Billing;

namespace Marketing.API.Middlewares;

/// <summary>
/// Refuses writes from a workspace with no active plan.
/// </summary>
/// <remarks>
/// The client hides the product behind a lock screen, and that is a UI. <c>POST /contacts</c> is
/// reachable with a token and a <c>curl</c> command whatever the screen shows, so the rule has to
/// exist here as well or it is a suggestion.
/// <para>
/// Reads are never refused. A workspace that lapses must still be able to see what it has:
/// hiding the data is a worse answer than pausing the product, and "nothing has been deleted" is
/// what the lock screen promises.
/// </para>
/// </remarks>
public sealed class SubscriptionGateMiddleware
{
    /// <summary>
    /// Route prefixes a lapsed workspace may still write to.
    /// </summary>
    /// <remarks>
    /// Three groups, and each is here for its own reason:
    /// <list type="bullet">
    /// <item>the way out - <c>subscription</c>, <c>billing</c> and <c>plans</c> carry the plan
    /// change and the payment proof, and refusing those would lock the customer out of paying;</item>
    /// <item>identity and housekeeping - signing in, ending a device, marking a notification read,
    /// correcting the workspace's own details. None of these are using the product, and all of
    /// them are things somebody sorting out a lapsed account needs;</item>
    /// <item>not the customer at all - the platform's own routes, and the Meta webhook, which
    /// carries no token and must never be refused: Meta retries, and a customer's inbound
    /// messages are not theirs to lose over an unpaid invoice.</item>
    /// </list>
    /// </remarks>
    private static readonly string[] StillWritable =
    [
        "/api/v1/subscription",
        "/api/v1/billing",
        "/api/v1/plans",
        "/api/v1/auth",
        "/api/v1/security",
        "/api/v1/notifications",
        "/api/v1/workspace",
        "/api/v1/superadmin",
        "/api/v1/admin",
        "/api/v1/dev",
        "/api/v1/whatsapp/webhook",
    ];

    private readonly RequestDelegate _next;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="next">Next middleware.</param>
    public SubscriptionGateMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>Refuses the request when it writes and the workspace has no plan.</summary>
    /// <param name="context">Request context.</param>
    /// <param name="gate">Reads the workspace's subscription state.</param>
    public async Task InvokeAsync(HttpContext context, ISubscriptionGate gate)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gate);

        if (IsWrite(context.Request.Method) && !IsExempt(context.Request.Path))
        {
            // Throws a ForbiddenException carrying subscription_required, which the exception
            // handler renders as the 403 the client keys its copy off.
            await gate.DemandActiveAsync(context.RequestAborted);
        }

        await _next(context);
    }

    /// <summary>
    /// Whether the method changes anything.
    /// </summary>
    /// <remarks>
    /// An allow-list of read methods rather than a list of write ones, so a method nobody thought
    /// about is gated rather than waved through. <c>OPTIONS</c> is a preflight and never reaches
    /// a handler.
    /// </remarks>
    /// <param name="method">HTTP method.</param>
    private static bool IsWrite(string method) =>
        !HttpMethods.IsGet(method)
        && !HttpMethods.IsHead(method)
        && !HttpMethods.IsOptions(method);

    private static bool IsExempt(PathString path) =>
        StillWritable.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}
