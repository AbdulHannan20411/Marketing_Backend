namespace Marketing.API.Middlewares;

/// <summary>
/// Adds the response security headers.
/// <para>
/// This host serves JSON to a separate Angular origin, so the headers are tuned for an API rather
/// than a document server: the content security policy denies everything because nothing here is
/// meant to be rendered, and referrer information is suppressed entirely.
/// </para>
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="next">Next middleware in the pipeline.</param>
    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>Runs the middleware.</summary>
    /// <param name="context">Request context.</param>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var headers = context.Response.Headers;

        // Stops a browser from second-guessing the declared content type, which is how a JSON
        // response containing attacker-controlled text gets executed as HTML.
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Resource-Policy"] = "same-site";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";

        // No scripts, styles, frames or form targets: an API response has no legitimate reason to
        // load anything, and Swagger UI is served with its own relaxed policy in development.
        headers["Content-Security-Policy"] =
            "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

        // Leaks the framework and version to anyone fingerprinting the host.
        headers.Remove("X-Powered-By");
        headers.Remove("Server");

        return _next(context);
    }
}
