using System.Security.Claims;
using Marketing.Shared.Models;

namespace Marketing.Shared.Abstractions;

/// <summary>Issues and validates the platform's access and refresh tokens.</summary>
public interface ITokenService
{
    /// <summary>Issues a signed JWT access token for a principal.</summary>
    /// <param name="descriptor">Identity, tenant and roles to embed.</param>
    /// <returns>The token and its absolute expiry.</returns>
    public AccessToken CreateAccessToken(TokenSubject descriptor);

    /// <summary>
    /// Generates a cryptographically random refresh token.
    /// <para>
    /// The plaintext is returned to the caller once and never stored; only the SHA-256 hash is
    /// persisted, so a database disclosure does not yield usable tokens.
    /// </para>
    /// </summary>
    public RefreshTokenMaterial CreateRefreshToken();

    /// <summary>Hashes a refresh token so it can be matched against the stored hash.</summary>
    /// <param name="refreshToken">Plaintext refresh token supplied by the client.</param>
    public string HashRefreshToken(string refreshToken);

    /// <summary>
    /// Generates a single-use token for an emailed link - an invitation or a password reset.
    /// <para>
    /// The plaintext is returned once, to be put in the link; only the hash is persisted, so a
    /// database disclosure does not hand out working reset links for every account.
    /// </para>
    /// </summary>
    /// <param name="lifetime">How long the token stays valid.</param>
    public SecureToken CreateSecureToken(TimeSpan lifetime);

    /// <summary>Hashes an emailed token so it can be matched against the stored hash.</summary>
    /// <param name="token">Plaintext token taken from the link.</param>
    public string HashSecureToken(string token);

    /// <summary>
    /// Validates an expired access token's signature and returns its claims, ignoring lifetime.
    /// Used by the refresh flow to bind a refresh token to the access token it was paired with.
    /// </summary>
    /// <param name="accessToken">The expired access token.</param>
    /// <returns>The principal, or <see langword="null"/> when the token is not valid.</returns>
    public ClaimsPrincipal? GetPrincipalFromExpiredToken(string accessToken);
}
