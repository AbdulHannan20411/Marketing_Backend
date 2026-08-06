namespace Marketing.Application.DTOs.Auth;

/// <summary>Request to exchange a refresh token for a new pair.</summary>
/// <param name="RefreshToken">The refresh token issued by a previous sign-in or refresh.</param>
public sealed record RefreshTokenRequest(string RefreshToken);

/// <summary>
/// Request to begin a password reset.
/// <para>
/// The endpoint always reports success regardless of whether the address is registered, so this
/// cannot be used to enumerate accounts.
/// </para>
/// </summary>
/// <param name="Email">Address to send the reset link to.</param>
public sealed record ForgotPasswordRequest(string Email);
