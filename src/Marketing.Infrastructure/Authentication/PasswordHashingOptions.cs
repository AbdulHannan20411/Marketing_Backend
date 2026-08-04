using System.ComponentModel.DataAnnotations;

namespace Marketing.Infrastructure.Authentication;

/// <summary>Password-hashing work factor, bound from <c>Authentication:PasswordHashing</c>.</summary>
public sealed class PasswordHashingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Authentication:PasswordHashing";

    /// <summary>
    /// PBKDF2 iteration count.
    /// <para>
    /// The default follows the OWASP guidance for PBKDF2-HMAC-SHA256. It is a genuine trade-off:
    /// every increase multiplies both an attacker's cost and the latency of every sign-in, and the
    /// sign-in path holds a request thread for that whole time. Raise it deliberately, and measure
    /// the login endpoint afterwards.
    /// </para>
    /// </summary>
    [Range(100_000, 2_000_000)]
    public int Iterations { get; init; } = 210_000;
}
