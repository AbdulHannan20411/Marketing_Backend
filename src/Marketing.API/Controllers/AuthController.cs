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
    /// Returns the same generic error for wrong credentials, a disabled account, a locked account
    /// and a suspended tenant. That is intentional: distinguishing them would let an unauthenticated
    /// caller confirm which addresses are registered on the platform.
    /// </remarks>
    /// <param name="request">Credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Authentication succeeded; a token pair is returned.</response>
    /// <response code="400">The request is malformed.</response>
    /// <response code="401">Authentication failed.</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthenticationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LoginAsync(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authenticationService.LoginAsync(request, cancellationToken);

        return Success(result, "Signed in successfully.");
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
    [ProducesResponseType(typeof(ApiResponse<AuthenticationResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RefreshAsync(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authenticationService.RefreshAsync(request, cancellationToken);

        return Success(result);
    }

    /// <summary>Ends the session identified by a refresh token.</summary>
    /// <remarks>Idempotent, and never reveals whether the token existed.</remarks>
    /// <param name="request">The token identifying the session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="204">The session is no longer active.</response>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> LogoutAsync(
        [FromBody] RevokeTokenRequest request,
        CancellationToken cancellationToken)
    {
        await _authenticationService.LogoutAsync(request, cancellationToken);

        return NoContent();
    }

    /// <summary>Ends every session for the signed-in user.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="204">All sessions were revoked.</response>
    [HttpPost("logout-everywhere")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> LogoutEverywhereAsync(CancellationToken cancellationToken)
    {
        await _authenticationService.LogoutEverywhereAsync(cancellationToken);

        return NoContent();
    }

    /// <summary>Returns the signed-in user's profile.</summary>
    /// <remarks>
    /// The response carries the organisation's <em>name</em> and never its identifier. Tenancy is
    /// resolved server-side from the bearer token on every request.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The profile of the authenticated user.</response>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<CurrentUserResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        var result = await _authenticationService.GetCurrentUserAsync(cancellationToken);

        return Success(result);
    }
}
