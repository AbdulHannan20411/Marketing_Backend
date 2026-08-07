using static Marketing.Common.Constants.AppConstants;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// A single-use, expiring token emailed to a user - an invitation to activate an account, or a
/// password reset.
/// <para>
/// Only the SHA-256 hash is stored, exactly as for refresh tokens: a database disclosure must not
/// hand an attacker a working reset link for every account on the platform.
/// </para>
/// <para>
/// Separate from <see cref="RefreshToken"/> because the two have different lifetimes, different
/// revocation rules, and different consequences. Conflating them would mean a password reset and a
/// session share a table and, eventually, a bug.
/// </para>
/// </summary>
public sealed class UserToken : BaseEntity, ITenantScoped
{
    /// <summary>User the token belongs to.</summary>
    public Guid UserId { get; set; }

    /// <summary>What the token authorises.</summary>
    public UserTokenPurpose Purpose { get; set; }

    /// <summary>Hex-encoded SHA-256 hash of the token value.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Absolute expiry, in UTC.</summary>
    public DateTimeOffset ExpiresOn { get; set; }

    /// <summary>
    /// Instant the token was used. Set on the first successful use, which is what makes it
    /// single-use - a reset link forwarded or left in an inbox cannot be replayed.
    /// </summary>
    public DateTimeOffset? ConsumedOn { get; set; }

    /// <summary>Client address that requested it, retained for incident review.</summary>
    public string? RequestedByIp { get; set; }

    /// <summary>User navigation.</summary>
    public User User { get; set; } = null!;

    /// <summary>Whether the token can still be exchanged.</summary>
    /// <param name="utcNow">Current instant.</param>
    public bool IsUsable(DateTimeOffset utcNow) => ConsumedOn is null && ExpiresOn > utcNow;
}
