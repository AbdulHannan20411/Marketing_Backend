using Marketing.Application.DTOs.Auth;

namespace Marketing.Application.Interfaces;

/// <summary>Sign-in, token rotation and session revocation.</summary>
public interface IAuthenticationService
{
    /// <summary>Authenticates a user and opens a new session.</summary>
    /// <param name="request">Credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Common.Exceptions.AuthenticationException">
    /// Credentials are wrong, the account is locked or disabled, the tenant is not active, or the
    /// account does not belong at the sign-in portal used. The same generic message is returned in
    /// every case so the response cannot be used to enumerate accounts or probe account state.
    /// </exception>
    public Task<AuthTokens> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges a refresh token for a new pair, rotating the old one.
    /// <para>
    /// Presenting a token that has already been rotated is treated as theft: the entire session
    /// chain for that user is revoked rather than the request merely being refused.
    /// </para>
    /// </summary>
    /// <param name="request">The refresh token to exchange.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<AuthTokens> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the session the caller's access token belongs to, identified by its <c>sid</c> claim.
    /// Idempotent, and never reveals whether the session existed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends every session for the signed-in user and rotates their security stamp, so access
    /// tokens already in flight stop being accepted at the next refresh.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task LogoutEverywhereAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins a password reset.
    /// <para>
    /// Always completes successfully, whether or not the address is registered - reporting
    /// otherwise would turn this into an account-enumeration oracle.
    /// </para>
    /// </summary>
    /// <param name="request">The address to send a reset link to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates an invited account: sets its first password and signs the user in.
    /// <para>
    /// Redeeming the token proves the user controls the address it was sent to, so the account is
    /// marked email-confirmed here and no separate verification step is needed.
    /// </para>
    /// </summary>
    /// <param name="request">Token and chosen password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<AuthTokens> AcceptInvitationAsync(
        AcceptInvitationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a new password from a reset link and ends every existing session.
    /// <para>
    /// Ending sessions is the point of a reset: if the account was compromised, leaving the
    /// attacker signed in would make the reset cosmetic.
    /// </para>
    /// </summary>
    /// <param name="request">Token and new password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the signed-in user's password, keeping their current session and ending the rest.
    /// </summary>
    /// <param name="request">Current and new password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the signed-in user's own name, address or password, in one transaction.
    /// </summary>
    /// <remarks>
    /// The user is taken from the token and never from the request. Accepting an identifier here
    /// would let any authenticated caller edit any other account by guessing one.
    /// </remarks>
    /// <param name="request">The fields to change. Absent means leave alone.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<CurrentUserResponse> UpdateProfileAsync(
        UpdateProfileRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the signed-in user's progress through the product tour.</summary>
    /// <remarks>
    /// A user nobody has stored anything for is <c>not_started</c> at step zero, never a 404. "No
    /// state" and "not started" are the same thing, and a 404 would make every first sign-in look
    /// like an error in the logs.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<OnboardingStateResponse> GetOnboardingStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Records the signed-in user's progress through the product tour.</summary>
    /// <param name="request">The state to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<OnboardingStateResponse> UpdateOnboardingStateAsync(
        UpdateOnboardingStateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the profile of the signed-in user.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<CurrentUserResponse> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}
