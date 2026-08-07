using Asp.Versioning;
using Marketing.Application.DTOs.Auth;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Marketing.API.Controllers;

/// <summary>Sign-in, token rotation and session management.</summary>
[ApiVersion("1.0")]
[EnableRateLimiting(AppConstants.RateLimits.Authentication)]
public sealed class AuthController : ApiControllerBase
{
    private readonly IAuthenticationService _authenticationService;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="authenticationService">Authentication service.</param>
    public AuthController(IAuthenticationService authenticationService)
    {
        _authenticationService = authenticationService;
    }

    /// <summary>Authenticates a user and opens a session.</summary>
    /// <remarks>
    /// Returns the same generic error for wrong credentials, a disabled account, a locked account,
    /// a suspended tenant and a portal mismatch. That is intentional: distinguishing them would
    /// let an unauthenticated caller confirm which addresses are registered on the platform.
    /// </remarks>
    /// <param name="request">Credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Authentication succeeded; a token pair is returned.</response>
    /// <response code="401">Authentication failed.</response>
    /// <response code="422">The request is malformed; per-field errors are returned.</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthTokens>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> LoginAsync(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var tokens = await _authenticationService.LoginAsync(request, cancellationToken);

        return Success(tokens);
    }

    /// <summary>Exchanges a refresh token for a new token pair.</summary>
    /// <remarks>
    /// Refresh tokens are single use. Presenting one that has already been rotated revokes every
    /// session for that user, on the assumption that the token was stolen.
    /// </remarks>
    /// <param name="request">The refresh token to exchange.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A new token pair was issued.</response>
    /// <response code="401">The refresh token is unknown, expired, revoked or already used.</response>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthTokens>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RefreshAsync(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var tokens = await _authenticationService.RefreshAsync(request, cancellationToken);

        return Success(tokens);
    }

    /// <summary>Ends the caller's current session.</summary>
    /// <remarks>
    /// The session is taken from the access token's <c>sid</c> claim, not from the request body,
    /// so a caller can only ever end their own. Idempotent, and never reveals whether the session
    /// was active.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The session is no longer active.</response>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> LogoutAsync(CancellationToken cancellationToken)
    {
        await _authenticationService.LogoutAsync(cancellationToken);

        return SuccessEmpty("Signed out.");
    }

    /// <summary>Ends every session for the signed-in user.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">All sessions were revoked.</response>
    [HttpPost("logout-everywhere")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> LogoutEverywhereAsync(CancellationToken cancellationToken)
    {
        await _authenticationService.LogoutEverywhereAsync(cancellationToken);

        return SuccessEmpty("Signed out of all sessions.");
    }

    /// <summary>Begins a password reset.</summary>
    /// <remarks>
    /// Always returns 200, whether or not the address is registered, so the endpoint cannot be
    /// used to enumerate accounts.
    /// </remarks>
    /// <param name="request">The address to send a reset link to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The request was accepted.</response>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ForgotPasswordAsync(
        [FromBody] ForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        await _authenticationService.ForgotPasswordAsync(request, cancellationToken);

        return SuccessEmpty("If that address is registered, a reset link is on its way.");
    }

    /// <summary>Activates an invited account and signs the user in.</summary>
    /// <remarks>
    /// Exchanges the single-use token from the invitation email for an active account with a
    /// password the user chooses, and returns a token pair so they land signed in. The token
    /// cannot be reused.
    /// </remarks>
    /// <param name="request">Token and chosen password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The account is active and a token pair is returned.</response>
    /// <response code="401">The token is unknown, expired or already used.</response>
    /// <response code="422">The password does not meet policy.</response>
    [HttpPost("accept-invitation")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthTokens>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AcceptInvitationAsync(
        [FromBody] AcceptInvitationRequest request,
        CancellationToken cancellationToken)
    {
        var tokens = await _authenticationService.AcceptInvitationAsync(request, cancellationToken);

        return Success(tokens, "Your account is ready.");
    }

    /// <summary>Sets a new password from a reset link.</summary>
    /// <remarks>
    /// Ends every existing session, including any an attacker may hold. The user signs in again
    /// with the new password.
    /// </remarks>
    /// <param name="request">Token and new password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The password was changed.</response>
    /// <response code="401">The token is unknown, expired or already used.</response>
    /// <response code="422">The password does not meet policy.</response>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ResetPasswordAsync(
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        await _authenticationService.ResetPasswordAsync(request, cancellationToken);

        return SuccessEmpty("Your password has been changed. Sign in with your new password.");
    }

    /// <summary>Changes the signed-in user's own password.</summary>
    /// <remarks>
    /// Requires the current password. Ends the user's other sessions but keeps the one making the
    /// request, so they are not signed out of the tab they are using.
    /// </remarks>
    /// <param name="request">Current and new password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The password was changed.</response>
    /// <response code="422">The current password is wrong, or the new one does not meet policy.</response>
    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ChangePasswordAsync(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        await _authenticationService.ChangePasswordAsync(request, cancellationToken);

        return SuccessEmpty("Your password has been changed. Other devices have been signed out.");
    }

    /// <summary>Returns the signed-in user's profile.</summary>
    /// <remarks>
    /// Supplementary to the token, which is the client's primary source for identity, role and
    /// permissions. Carries the organisation's <em>name</em> and never its identifier.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The profile of the authenticated user.</response>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<CurrentUserResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        var profile = await _authenticationService.GetCurrentUserAsync(cancellationToken);

        return Success(profile);
    }
}
