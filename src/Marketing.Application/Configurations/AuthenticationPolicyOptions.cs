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
}
