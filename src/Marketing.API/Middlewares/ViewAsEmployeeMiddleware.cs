using System.Net;
using Marketing.Application.Services.Security;
using Marketing.Common.Exceptions;

namespace Marketing.API.Middlewares;

/// <summary>
/// Applies <c>?viewAsEmployeeId=</c> to a request, so an administrator sees the workspace as one
/// of their team sees it.
/// </summary>
/// <remarks>
/// Middleware rather than a parameter on each action, unlike <c>?adminId=</c>. That one is threaded
/// through the handful of routes platform staff use; this one applies to every tenant-scoped read
/// in the product, and adding an argument to two hundred actions would guarantee the one that got
/// missed was the interesting one.
/// <para>
/// Runs after authentication, so the caller is known, and before the endpoint, so the preview is
/// in force for the whole of it. Route authorisation still runs against the real principal: the
/// caller must pass the endpoint's own gate as themselves, and only then does the preview narrow
/// what the endpoint can see. Preview subtracts; it never adds.
/// </para>
/// </remarks>
public sealed class ViewAsEmployeeMiddleware
{
    /// <summary>Query parameter carrying the teammate to preview.</summary>
    public const string Parameter = "viewAsEmployeeId";

    /// <summary>
    /// Route prefixes the parameter is ignored on.
    /// </summary>
    /// <remarks>
    /// Platform administration and identity. Narrowing the plan catalogue or the sign-in routes to
    /// an employee's view is not a smaller answer, it is a meaningless one - and <c>/auth/</c>
    /// especially must keep answering for the real caller, or the client would render the banner
    /// using the previewed teammate's own profile and lose the way back out.
    /// </remarks>
    private static readonly string[] NotScopedToAnEmployee =
        ["/api/v1/superadmin", "/api/v1/admin", "/api/v1/plans", "/api/v1/auth"];

    private readonly RequestDelegate _next;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="next">Next middleware.</param>
    public ViewAsEmployeeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>Enters the preview for the request, when one is asked for.</summary>
    /// <param name="context">Request context.</param>
    /// <param name="resolver">Resolves and authorises the teammate.</param>
    public async Task InvokeAsync(HttpContext context, IViewAsResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolver);

        var asked = context.Request.Query[Parameter];

        if (asked.Count == 0 || string.IsNullOrWhiteSpace(asked[0]))
        {
            await _next(context);
            return;
        }

        if (IsExcluded(context.Request.Path))
        {
            // Ignored rather than refused, matching how ?adminId= treats a caller it does not
            // apply to. The client does not send it here; a stale one should not break the route.
            await _next(context);
            return;
        }

        if (!IsRead(context.Request.Method))
        {
            // The line this feature is built on. An administrator who wants to act acts as
            // themselves - a mode where a write silently lands under a colleague's name is a
            // different feature with different consequences, and it is not this one. Refused
            // loudly rather than ignored, because ignoring it would do the write as the
            // administrator while the client believed otherwise.
            throw new RequestRejectedException(
                HttpStatusCode.BadRequest,
                "view_as_is_read_only",
                "Previewing a teammate is read-only. Leave the preview to make a change.",
                Parameter);
        }

        // Disposed at the end of the request, so nothing after this point is still previewing.
        using var scope = await resolver.EnterAsync(asked[0], context.RequestAborted);

        await _next(context);
    }

    private static bool IsRead(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

    private static bool IsExcluded(PathString path) =>
        NotScopedToAnEmployee.Any(prefix =>
            path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}
