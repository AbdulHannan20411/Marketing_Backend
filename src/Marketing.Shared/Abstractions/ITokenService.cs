using System.Security.Claims;
using Marketing.Shared.Models;

namespace Marketing.Shared.Abstractions;

/// <summary>Issues and validates the platform's access and refresh tokens.</summary>
public interface ITokenService
{
    /// <summary>Issues a signed JWT access token for a principal.</summary>
    /// <param name="descriptor">Identity, tenant and roles to embed.</param>
    /// <returns>The token and its absolute expiry.</returns>
    AccessToken CreateAccessToken(TokenSubject descriptor);

    /// <summary>
    /// Generates a cryptographically random refresh token.
    /// <para>
    /// The plaintext is returned to the caller once and never stored; only the SHA-256 hash is
    /// persisted, so a database disclosure does not yield usable tokens.
    /// </para>
    /// </summary>
    RefreshTokenMaterial CreateRefreshToken();

    /// <summary>Hashes a refresh token so it can be matched against the stored hash.</summary>
    /// <param name="refreshToken">Plaintext refresh token supplied by the client.</param>
    string HashRefreshToken(string refreshToken);

    /// <summary>
    /// Validates an expired access token's signature and returns its claims, ignoring lifetime.
    /// Used by the refresh flow to bind a refresh token to the access token it was paired with.
    /// </summary>
    /// <param name="accessToken">The expired access token.</param>
    /// <returns>The principal, or <see langword="null"/> when the token is not valid.</returns>
    ClaimsPrincipal? GetPrincipalFromExpiredToken(string accessToken);
}
