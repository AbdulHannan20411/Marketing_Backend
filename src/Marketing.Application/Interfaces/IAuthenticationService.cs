using Marketing.Application.DTOs.Auth;

namespace Marketing.Application.Interfaces;

/// <summary>Sign-in, token rotation and session revocation.</summary>
public interface IAuthenticationService
{
    /// <summary>
    /// Authenticates a user and opens a new session.
    /// </summary>
    /// <param name="request">Credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Common.Exceptions.AuthenticationException">
    /// Credentials are wrong, the account is locked or disabled, or the tenant is not active. The
    /// same generic message is returned in every case so the response cannot be used to enumerate
    /// accounts or probe account state.
    /// </exception>
    Task<AuthenticationResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges a refresh token for a new pair, rotating the old one.
    /// <para>
    /// Presenting a token that has already been rotated is treated as theft: the entire session
    /// chain for that user is revoked rather than the request merely being refused.
    /// </para>
    /// </summary>
    /// <param name="request">The refresh token to exchange.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AuthenticationResponse> RefreshAsync(
        RefreshTokenRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Ends the session identified by a refresh token.</summary>
    /// <param name="request">The token identifying the session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task LogoutAsync(RevokeTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends every session for the signed-in user and rotates their security stamp, so access tokens
    /// already in flight stop being accepted at the next refresh.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task LogoutEverywhereAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the profile of the signed-in user.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<CurrentUserResponse> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}
