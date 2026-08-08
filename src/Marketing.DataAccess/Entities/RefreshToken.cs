namespace Marketing.DataAccess.Entities;

/// <summary>
/// One refresh-token session.
/// <para>
/// Only the SHA-256 hash of the token is stored, so a database disclosure yields nothing usable.
/// Rotation is recorded through <see cref="ReplacedByTokenHash"/>, which is what allows reuse of an
/// already-rotated token to be detected and the whole session chain revoked - the standard
/// mitigation for a stolen refresh token.
/// </para>
/// </summary>
public sealed class RefreshToken : BaseEntity, ITenantScoped
{
    /// <summary>Owning user.</summary>
    public long UserId { get; set; }

    /// <summary>
    /// Session this token belongs to. Carried in the access token's <c>sid</c> claim so an access
    /// token can be tied back to the session that issued it.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>Hex-encoded SHA-256 hash of the token value.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Security stamp captured at issuance; a mismatch invalidates the session.</summary>
    public Guid SecurityStamp { get; set; }

    /// <summary>Absolute expiry, in UTC.</summary>
    public DateTimeOffset ExpiresOn { get; set; }

    /// <summary>Instant the token was used and rotated, in UTC.</summary>
    public DateTimeOffset? ConsumedOn { get; set; }

    /// <summary>Instant the token was revoked, in UTC.</summary>
    public DateTimeOffset? RevokedOn { get; set; }

    /// <summary>Why the token was revoked. Recorded for incident review.</summary>
    public string? RevokedReason { get; set; }

    /// <summary>Hash of the token that superseded this one during rotation.</summary>
    public string? ReplacedByTokenHash { get; set; }

    /// <summary>Client address at issuance, retained for anomaly detection.</summary>
    public string? CreatedByIp { get; set; }

    /// <summary>Client user agent at issuance, truncated on write.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Owning user navigation.</summary>
    public User User { get; set; } = null!;

    /// <summary>Whether the token is currently usable.</summary>
    /// <param name="utcNow">Current instant, supplied so the check stays testable.</param>
    public bool IsActive(DateTimeOffset utcNow) =>
        RevokedOn is null && ConsumedOn is null && ExpiresOn > utcNow;
}
