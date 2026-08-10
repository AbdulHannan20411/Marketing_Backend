using System.Net;
using Asp.Versioning;
using Marketing.Application.DTOs.Auth;
using Marketing.Application.Interfaces;
using Marketing.Common.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>
/// Stands in for the Angular pages an emailed link points at, so an invitation or a password reset
/// can be completed by actually clicking the link.
/// <para>
/// The activation link is built from <c>Email:ClientBaseUrl</c>. Point that at
/// <c>{host}/api/v1/dev</c> and the link resolves here instead of at a front end that is not
/// running yet; point it back at the Angular origin and nothing else has to change.
/// </para>
/// <para>
/// <b>Development only</b> — every action returns <c>404</c> elsewhere. It renders plain HTML with
/// no script and posts a normal form, so it needs nothing from the client and works in any browser.
/// </para>
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/dev/auth")]
[AllowAnonymous]
public sealed class DevAuthController : ApiControllerBase
{
    private readonly IAuthenticationService _authentication;
    private readonly IHostEnvironment _environment;

    /// <summary>Initialises a new instance.</summary>
    public DevAuthController(IAuthenticationService authentication, IHostEnvironment environment)
    {
        _authentication = authentication;
        _environment = environment;
    }

    /// <summary>Renders the "choose a password" form an invitation link lands on.</summary>
    /// <param name="token">Single-use token from the emailed link.</param>
    /// <response code="200">The form.</response>
    /// <response code="404">Not running in development.</response>
    [HttpGet("accept-invitation")]
    [Produces("text/html")]
    public IActionResult AcceptInvitationForm([FromQuery] string? token) =>
        _environment.IsDevelopment()
            ? Page(Form("accept-invitation", "Accept your invitation", "Set your password", token))
            : NotFound();

    /// <summary>Renders the "choose a new password" form a reset link lands on.</summary>
    /// <param name="token">Single-use token from the emailed link.</param>
    /// <response code="200">The form.</response>
    /// <response code="404">Not running in development.</response>
    [HttpGet("reset-password")]
    [Produces("text/html")]
    public IActionResult ResetPasswordForm([FromQuery] string? token) =>
        _environment.IsDevelopment()
            ? Page(Form("reset-password", "Reset your password", "Set a new password", token))
            : NotFound();

    /// <summary>Completes an invitation from the form above.</summary>
    /// <param name="token">Single-use token.</param>
    /// <param name="password">Chosen password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The outcome, as a page.</response>
    /// <response code="404">Not running in development.</response>
    [HttpPost("accept-invitation")]
    [Consumes("application/x-www-form-urlencoded")]
    [Produces("text/html")]
    public Task<IActionResult> AcceptInvitationAsync(
        [FromForm] string token,
        [FromForm] string password,
        CancellationToken cancellationToken) =>
        SubmitAsync(
            async () => await _authentication.AcceptInvitationAsync(
                new AcceptInvitationRequest(token, password), cancellationToken),
            "Your account is active. You can sign in now.");

    /// <summary>Completes a password reset from the form above.</summary>
    /// <param name="token">Single-use token.</param>
    /// <param name="password">Chosen password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The outcome, as a page.</response>
    /// <response code="404">Not running in development.</response>
    [HttpPost("reset-password")]
    [Consumes("application/x-www-form-urlencoded")]
    [Produces("text/html")]
    public Task<IActionResult> ResetPasswordAsync(
        [FromForm] string token,
        [FromForm] string password,
        CancellationToken cancellationToken) =>
        SubmitAsync(
            async () =>
            {
                // Reset returns no tokens on purpose: it revokes every session, so the user has to
                // sign in again with the new password.
                await _authentication.ResetPasswordAsync(
                    new ResetPasswordRequest(token, password), cancellationToken);

                return null;
            },
            "Your password has been changed. Sign in with it.");

    /// <summary>Runs one of the token redemptions and renders the outcome.</summary>
    private async Task<IActionResult> SubmitAsync(Func<Task<AuthTokens?>> redeem, string success)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        try
        {
            var tokens = await redeem();

            // The token pair is shown when there is one, because the whole point of this page is
            // to finish the flow and carry straight on to an authenticated call.
            return Page(
                $"<h1>Done</h1><p>{Encode(success)}</p>"
                + (tokens is null
                    ? string.Empty
                    : "<h2>Access token</h2>"
                      + $"<pre>{Encode(tokens.AccessToken)}</pre>"
                      + "<h2>Refresh token</h2>"
                      + $"<pre>{Encode(tokens.RefreshToken)}</pre>"
                      + $"<p>Expires at {Encode(tokens.ExpiresAtUtc.ToString("O"))}.</p>"));
        }
        catch (AppException exception)
        {
            // The uniform "invalid or expired" message the real endpoints return. A used, unknown
            // or lapsed token is deliberately indistinguishable, and this page must not become the
            // one place that tells them apart.
            return Page(
                $"<h1>That did not work</h1><p>{Encode(exception.Message)}</p>"
                + "<p>Request a new link and try again.</p>");
        }
    }

    /// <summary>Builds the password form.</summary>
    private static string Form(string action, string heading, string label, string? token) =>
        $"<h1>{Encode(heading)}</h1>"
        + (string.IsNullOrWhiteSpace(token)
            ? "<p>This link is missing its token. Open the link from the email exactly as sent.</p>"
            : $"<form method=\"post\" action=\"{Encode(action)}\">"
              + $"<input type=\"hidden\" name=\"token\" value=\"{Encode(token)}\">"
              + $"<p><label for=\"password\">{Encode(label)}</label></p>"
              + "<p><input id=\"password\" name=\"password\" type=\"password\" required "
              + "minlength=\"12\" autocomplete=\"new-password\" size=\"40\"></p>"
              + "<p>At least 12 characters, with a letter and a digit.</p>"
              + "<p><button type=\"submit\">Continue</button></p>"
              + "</form>");

    /// <summary>Wraps a fragment in a minimal document.</summary>
    /// <remarks>
    /// No stylesheet and no script, so the page renders under a strict content security policy and
    /// needs no exception beyond allowing the form to post back to this origin.
    /// </remarks>
    private ContentResult Page(string body) =>
        Content(
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            + "<title>Marketing platform</title></head><body>"
            + body
            + "</body></html>",
            "text/html; charset=utf-8");

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
