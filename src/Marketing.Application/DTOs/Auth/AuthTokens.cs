namespace Marketing.Application.DTOs.Auth;

/// <summary>
/// A freshly issued token pair.
/// <para>
/// Deliberately carries no profile. The client decodes the access token for identity, role and
/// permissions, so duplicating them in the body would create a second source of truth that can
/// disagree with the token after a permission change.
/// </para>
/// </summary>
/// <param name="AccessToken">Signed JWT to send as a bearer token.</param>
/// <param name="RefreshToken">
/// Opaque refresh token, returned exactly once - only its hash is stored server-side.
/// </param>
/// <param name="ExpiresAtUtc">Absolute expiry of the access token.</param>
public sealed record AuthTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc);
