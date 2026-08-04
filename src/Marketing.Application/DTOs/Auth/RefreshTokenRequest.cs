namespace Marketing.Application.DTOs.Auth;

/// <summary>Request to exchange a refresh token for a new token pair.</summary>
/// <param name="RefreshToken">The refresh token issued by a previous sign-in or refresh.</param>
public sealed record RefreshTokenRequest(string RefreshToken);

/// <summary>Request to revoke a single refresh-token session.</summary>
/// <param name="RefreshToken">Token identifying the session to end.</param>
public sealed record RevokeTokenRequest(string RefreshToken);
