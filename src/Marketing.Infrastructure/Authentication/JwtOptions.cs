using System.ComponentModel.DataAnnotations;

namespace Marketing.Infrastructure.Authentication;

/// <summary>Token issuance and validation settings, bound from the <c>Authentication:Jwt</c> section.</summary>
public sealed class JwtOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Authentication:Jwt";

    /// <summary>Minimum signing-key length in bytes, imposed by HMAC-SHA256.</summary>
    public const int MinimumSigningKeyBytes = 32;

    /// <summary>Token issuer.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; init; } = string.Empty;

    /// <summary>Intended audience.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Audience { get; init; } = string.Empty;

    /// <summary>
    /// Symmetric signing key.
    /// <para>
    /// Supplied by user secrets locally and by the platform secret store elsewhere. It is never
    /// committed and never has a default: a signing key with a fallback value is a signing key an
    /// attacker already has.
    /// </para>
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(MinimumSigningKeyBytes)]
    public string SigningKey { get; init; } = string.Empty;

    /// <summary>
    /// Access-token lifetime. Kept short because an access token cannot be revoked once issued;
    /// revocation takes effect at the next refresh.
    /// </summary>
    [Range(1, 120)]
    public int AccessTokenLifetimeMinutes { get; init; } = 15;

    /// <summary>Refresh-token lifetime.</summary>
    [Range(1, 90)]
    public int RefreshTokenLifetimeDays { get; init; } = 14;

    /// <summary>
    /// Tolerance applied when validating expiry. Defaults to zero rather than the framework's
    /// five minutes, so a 15-minute token is not silently a 20-minute token.
    /// </summary>
    [Range(0, 300)]
    public int ClockSkewSeconds { get; init; }
}
