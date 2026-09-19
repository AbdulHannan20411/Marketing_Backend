using System.ComponentModel.DataAnnotations;

namespace Marketing.Application.Configurations;

/// <summary>
/// Sign-in policy. Bound from the <c>Authentication:Policy</c> configuration section.
/// </summary>
public sealed class AuthenticationPolicyOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Authentication:Policy";

    /// <summary>Consecutive failures before the account is temporarily locked.</summary>
    [Range(3, 20)]
    public int MaxFailedLoginAttempts { get; init; } = 5;

    /// <summary>How long a lockout lasts.</summary>
    [Range(1, 1440)]
    public int LockoutDurationMinutes { get; init; } = 15;

    /// <summary>Minimum accepted password length.</summary>
    [Range(8, 128)]
    public int MinimumPasswordLength { get; init; } = 12;

    /// <summary>
    /// Maximum concurrent refresh-token sessions per user. Older sessions are revoked once the
    /// limit is exceeded, which bounds the blast radius of a stolen token.
    /// </summary>
    [Range(1, 50)]
    public int MaxConcurrentSessions { get; init; } = 5;

    /// <summary>
    /// Distinct devices one account may use within <see cref="DeviceWindowDays"/> before its
    /// administrators are told.
    /// </summary>
    /// <remarks>
    /// A person has a laptop, a phone and perhaps a work desktop. Past that, a login is usually being
    /// passed around, which is the abuse a per-seat price invites.
    /// </remarks>
    [Range(1, 50)]
    public int MaxDevicesPerUser { get; init; } = 3;

    /// <summary>How far back devices are counted, in days.</summary>
    [Range(1, 90)]
    public int DeviceWindowDays { get; init; } = 30;

    /// <summary>
    /// How recently a session must have shown activity to count as active, in minutes.
    /// </summary>
    /// <remarks>
    /// A little over twice the client's heartbeat interval, so one missed beat - a sleeping laptop, a
    /// flaky connection - does not mark a live session idle.
    /// </remarks>
    [Range(1, 60)]
    public int ActiveWindowMinutes { get; init; } = 5;
}
