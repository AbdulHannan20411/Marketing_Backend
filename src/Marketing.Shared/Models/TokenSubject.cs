namespace Marketing.Shared.Models;

/// <summary>
/// Everything that goes into an access token.
/// <para>
/// Built exclusively from persisted server state. In particular <see cref="TenantId"/> is read
/// from the user's own row, never from anything the client sent.
/// </para>
/// </summary>
/// <param name="UserId">Identifier of the user, emitted as <c>sub</c>.</param>
/// <param name="Email">Normalised email address.</param>
/// <param name="Name">Full name, emitted as <c>name</c>.</param>
/// <param name="Role">The single role string the client reads.</param>
/// <param name="Permissions">Effective permissions, emitted as a JSON array.</param>
/// <param name="WorkspaceName">Display label for the organisation. Never a tenant identifier.</param>
/// <param name="AvatarUrl">Avatar URL, or null.</param>
/// <param name="TenantId">Owning tenant, or null for platform staff. Server-side use only.</param>
/// <param name="TenantSlug">Owning tenant slug, for log context.</param>
/// <param name="SessionId">Ties the access token to its refresh-token session.</param>
public sealed record TokenSubject(
    Guid UserId,
    string Email,
    string Name,
    string Role,
    IReadOnlyCollection<string> Permissions,
    string? WorkspaceName,
    string? AvatarUrl,
    Guid? TenantId,
    string? TenantSlug,
    Guid SessionId);

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
