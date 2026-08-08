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
    /// <summary>
    /// Denies everything. Correct for every API response, because none of them are meant to be
    /// rendered by a browser.
    /// </summary>
    private const string ApiPolicy =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>
    /// The narrowest policy under which Swagger UI actually renders.
    /// <para>
    /// It needs its own stylesheets and scripts from this origin, and it writes inline style
    /// attributes and an inline initialiser, hence <c>'unsafe-inline'</c> on both. That is why this
    /// applies to the documentation path only and only when the documentation is served at all -
    /// widening the API's own policy to make a development tool work would be the wrong trade.
    /// </para>
    /// </summary>
    private const string DocumentationPolicy =
        "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data:; font-src 'self'; connect-src 'self'; "
        + "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    /// <summary>Path prefix the documentation is served under.</summary>
    private const string DocumentationPath = "/swagger";

    private readonly RequestDelegate _next;
    private readonly bool _servesDocumentation;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="next">Next middleware in the pipeline.</param>
    /// <param name="environment">
    /// Host environment. The relaxed documentation policy is only ever offered where the
    /// documentation itself is, so it cannot be reached in production even by path.
    /// </param>
    public SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _next = next;
        _servesDocumentation = environment.IsDevelopment();
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
        // load anything. Swagger UI is the one exception, and only where it is served.
        headers["Content-Security-Policy"] = _servesDocumentation && IsDocumentation(context.Request.Path)
            ? DocumentationPolicy
            : ApiPolicy;

        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

        // Leaks the framework and version to anyone fingerprinting the host.
        headers.Remove("X-Powered-By");
        headers.Remove("Server");

        return _next(context);
    }

    /// <summary>Whether a path belongs to the API documentation.</summary>
    /// <param name="path">Request path.</param>
    private static bool IsDocumentation(PathString path) =>
        path.StartsWithSegments(DocumentationPath, StringComparison.OrdinalIgnoreCase);
}
