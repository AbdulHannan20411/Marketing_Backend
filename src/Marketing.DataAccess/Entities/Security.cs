using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// One signed-in session: which device it is on, where it came from, and whether it is still alive.
/// </summary>
/// <remarks>
/// Keyed by the same <see cref="SessionId"/> the refresh tokens carry. Refresh tokens rotate - a new
/// row every few minutes - so they cannot answer "how many devices does this person use" or "when did
/// this device last do anything" without grouping and guessing. This row can: one per sign-in, touched
/// by the heartbeat, revoked once.
/// </remarks>
public sealed class UserSession : BaseEntity, ITenantScoped
{
    /// <summary>Whose session it is.</summary>
    public long UserId { get; set; }

    /// <summary>Identifier shared with the refresh tokens and the access token's <c>sid</c> claim.</summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// The browser's own identifier, sent by the client, or a fingerprint of its user agent.
    /// </summary>
    /// <remarks>
    /// The client generates it once and keeps it, so two sign-ins from the same browser are the same
    /// device and a new browser is a new one. Without the header, the user agent stands in - coarser,
    /// since every Chrome on Windows looks alike, but never wrong in the direction that matters: it
    /// cannot make two real devices look like one.
    /// </remarks>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>What the device is, as a person would say it: "Chrome / Windows".</summary>
    public string DeviceLabel { get; set; } = string.Empty;

    /// <summary>Browser family.</summary>
    public string Browser { get; set; } = string.Empty;

    /// <summary>Operating system family.</summary>
    public string OperatingSystem { get; set; } = string.Empty;

    /// <summary>Desktop, mobile or tablet.</summary>
    public string DeviceType { get; set; } = string.Empty;

    /// <summary>Address the session signed in from.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Address it was most recently seen at, which differs when a device changes network.</summary>
    public string? LastIpAddress { get; set; }

    /// <summary>City and country, when the edge in front of the API reports them.</summary>
    public string? Location { get; set; }

    /// <summary>Raw user agent, kept for an investigation rather than for display.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Last time the session proved it was alive - a request, a refresh or a heartbeat.</summary>
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>When it was ended, or null while it lives.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Why it was ended, as a stable code the client can explain.</summary>
    public string? RevokedReason { get; set; }

    /// <summary>Who ended it, when a person did rather than the system.</summary>
    public long? RevokedByUserId { get; set; }
}

/// <summary>Something about an account's sign-ins worth remembering: a new device, a burst of failures.</summary>
/// <remarks>
/// The raw material for the risk score and the alerts, and a record a Super Admin can read when a
/// customer disputes being flagged. Written once, never edited.
/// </remarks>
public sealed class SecurityEvent : BaseEntity, ITenantScoped
{
    /// <summary>Whose account it concerns.</summary>
    public long UserId { get; set; }

    /// <summary>Session involved, when there is one.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>What happened.</summary>
    public SecurityEventKind Kind { get; set; }

    /// <summary>Address involved.</summary>
    public string? IpAddress { get; set; }

    /// <summary>City and country, when known.</summary>
    public string? Location { get; set; }

    /// <summary>Device involved, as a person would say it.</summary>
    public string? DeviceLabel { get; set; }

    /// <summary>One line of detail for whoever investigates.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>When it happened.</summary>
    public DateTimeOffset OccurredAt { get; set; }
}
