namespace Marketing.Application.DTOs.Auth;

/// <summary>A freshly issued token pair and the profile of its owner.</summary>
/// <param name="AccessToken">Signed JWT to send as a bearer token.</param>
/// <param name="AccessTokenExpiresAtUtc">Absolute expiry of the access token.</param>
/// <param name="RefreshToken">
/// Opaque refresh token. Returned exactly once - only its hash is stored server-side, so it cannot
/// be recovered if the client loses it.
/// </param>
/// <param name="RefreshTokenExpiresAtUtc">Absolute expiry of the refresh token.</param>
/// <param name="User">Profile of the authenticated user.</param>
public sealed record AuthenticationResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc,
    CurrentUserResponse User);
