namespace Marketing.Shared.Models;

/// <summary>
/// Everything that goes into an access token.
/// <para>
/// Built exclusively from persisted state on the server. In particular <see cref="TenantId"/> is
/// read from the user's row, never from anything the client sent.
/// </para>
/// </summary>
/// <param name="UserId">Identifier of the user.</param>
/// <param name="Email">Normalised email address.</param>
/// <param name="DisplayName">Full name.</param>
/// <param name="TenantId">Owning tenant, or null for platform administrators.</param>
/// <param name="TenantSlug">Owning tenant slug, for log context.</param>
/// <param name="SessionId">Identifier tying the access token to its refresh-token session.</param>
/// <param name="Roles">Role names to embed.</param>
/// <param name="Permissions">Fine-grained permissions to embed.</param>
public sealed record TokenSubject(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid? TenantId,
    string? TenantSlug,
    Guid SessionId,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions);

/// <summary>A signed access token and the instant it stops being valid.</summary>
/// <param name="Value">Compact-serialised JWT.</param>
/// <param name="ExpiresAtUtc">Absolute expiry in UTC.</param>
public sealed record AccessToken(string Value, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// A newly minted refresh token. <paramref name="Value"/> is returned to the client exactly once;
/// only <paramref name="Hash"/> is persisted.
/// </summary>
/// <param name="Value">Plaintext refresh token.</param>
/// <param name="Hash">SHA-256 hash of the plaintext, hex encoded.</param>
/// <param name="ExpiresAtUtc">Absolute expiry in UTC.</param>
public sealed record RefreshTokenMaterial(string Value, string Hash, DateTimeOffset ExpiresAtUtc);
