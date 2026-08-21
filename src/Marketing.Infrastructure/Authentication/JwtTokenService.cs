using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marketing.Common.Constants;
using Marketing.Shared.Abstractions;
using Marketing.Shared.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Marketing.Infrastructure.Authentication;

/// <summary>JWT implementation of <see cref="ITokenService"/>.</summary>
public sealed class JwtTokenService : ITokenService
{
    private const int RefreshTokenByteLength = 64;

    private readonly JwtOptions _options;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly SigningCredentials _signingCredentials;
    private readonly TokenValidationParameters _refreshValidationParameters;
    /// <summary>
    /// Claim mapping is disabled to match the JwtBearer handler in the API host.
    /// <para>
    /// Left at its default, this handler rewrites <c>sub</c> to the long
    /// <c>ClaimTypes.NameIdentifier</c> URI on the way back in, so a principal recovered during
    /// refresh would carry different claim names from one produced by an ordinary authenticated
    /// request - and any code reading <c>sub</c> would silently find nothing.
    /// </para>
    /// </summary>
    private readonly JwtSecurityTokenHandler _tokenHandler = new() { MapInboundClaims = false };

    /// <summary>Initialises a new instance.</summary>
    /// <param name="options">Token settings.</param>
    /// <param name="dateTimeProvider">Clock.</param>
    public JwtTokenService(IOptions<JwtOptions> options, IDateTimeProvider dateTimeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _dateTimeProvider = dateTimeProvider;

        var keyBytes = Encoding.UTF8.GetBytes(_options.SigningKey);

        if (keyBytes.Length < JwtOptions.MinimumSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"The JWT signing key must be at least {JwtOptions.MinimumSigningKeyBytes} bytes for HMAC-SHA256.");
        }

        var securityKey = new SymmetricSecurityKey(keyBytes);
        _signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        _refreshValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = securityKey,
            // Deliberately false: this parameter set exists to read the claims out of a token that
            // has already expired, during refresh. Everything else about it is still verified, and
            // the signature check is what makes that safe.
            ValidateLifetime = false,
            ClockSkew = TimeSpan.Zero,
        };
    }

    /// <inheritdoc />
    public AccessToken CreateAccessToken(TokenSubject descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var issuedAt = _dateTimeProvider.UtcNow;
        var expiresAt = issuedAt.AddMinutes(_options.AccessTokenLifetimeMinutes);

        var claims = new List<Claim>(12)
        {
            // Invariant throughout. Claim values are compared as strings on every request, and
            // a server running under a culture with non-ASCII digits would mint tokens whose
            // subject and tenant no longer match anything the rest of the system looks up.
            new(JwtRegisteredClaimNames.Sub, descriptor.UserId.ToString(CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Email, descriptor.Email),
            new(AppConstants.Claims.Name, descriptor.Name),

            // Single-valued, matching the client contract. Also emitted under the framework's role
            // claim type so [Authorize(Roles = ...)] and RequireRole keep working server-side.
            new(AppConstants.Claims.Role, descriptor.Role),
            new(ClaimTypes.Role, descriptor.Role),

            new(AppConstants.Claims.SessionId, descriptor.SessionId.ToString()),
            new(
                JwtRegisteredClaimNames.Iat,
                issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
        };

        // Serialised as one JSON array claim rather than repeated string claims. Repeating them
        // would collapse to an array for two or more values but stay a bare string for exactly
        // one, and the client always indexes it as an array.
        claims.Add(new Claim(
            AppConstants.Claims.Permissions,
            JsonSerializer.Serialize(descriptor.Permissions),
            JsonClaimValueTypes.JsonArray));

        // A display label, never an identifier. Present even when null so the client can read the
        // key unconditionally.
        claims.Add(new Claim(AppConstants.Claims.WorkspaceName, descriptor.WorkspaceName ?? string.Empty));

        if (!string.IsNullOrWhiteSpace(descriptor.AvatarUrl))
        {
            claims.Add(new Claim(AppConstants.Claims.AvatarUrl, descriptor.AvatarUrl));
        }

        // The tenant claim is the whole isolation boundary. Written from the user's own row and
        // read back only through ITenantContext; no endpoint ever accepts one from a request.
        if (descriptor.TenantId is { } tenantId)
        {
            claims.Add(new Claim(
                AppConstants.Claims.TenantId,
                tenantId.ToString(CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(descriptor.TenantSlug))
        {
            claims.Add(new Claim(AppConstants.Claims.TenantSlug, descriptor.TenantSlug));
        }

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: issuedAt.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: _signingCredentials);

        return new AccessToken(_tokenHandler.WriteToken(token), expiresAt);
    }

    /// <inheritdoc />
    public RefreshTokenMaterial CreateRefreshToken()
    {
        // 512 bits from the OS CSPRNG. Base64url so the value survives headers, query strings and
        // JSON without escaping.
        var bytes = RandomNumberGenerator.GetBytes(RefreshTokenByteLength);
        var value = Base64UrlEncoder.Encode(bytes);

        return new RefreshTokenMaterial(
            value,
            HashRefreshToken(value),
            _dateTimeProvider.UtcNow.AddDays(_options.RefreshTokenLifetimeDays));
    }

    /// <inheritdoc />
    public string HashRefreshToken(string refreshToken) => HashOpaqueToken(refreshToken);

    /// <summary>
    /// Hashes an opaque token.
    /// <para>
    /// A plain SHA-256, not a password hash. The input is 512 bits of entropy from a CSPRNG, so it
    /// is not brute-forceable and a deliberately slow KDF would only add latency to every refresh
    /// for no security gain.
    /// </para>
    /// </summary>
    private static string HashOpaqueToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        return Convert.ToHexStringLower(digest);
    }

    /// <inheritdoc />
    public SecureToken CreateSecureToken(TimeSpan lifetime)
    {
        // Same 512-bit CSPRNG material as a refresh token. An invitation or reset link is a
        // bearer credential in an inbox and deserves the same entropy.
        var bytes = RandomNumberGenerator.GetBytes(RefreshTokenByteLength);
        var value = Base64UrlEncoder.Encode(bytes);

        return new SecureToken(value, HashSecureToken(value), _dateTimeProvider.UtcNow.Add(lifetime));
    }

    /// <inheritdoc />
    public string HashSecureToken(string token) => HashOpaqueToken(token);

    /// <inheritdoc />
    public ClaimsPrincipal? GetPrincipalFromExpiredToken(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        try
        {
            var principal = _tokenHandler.ValidateToken(
                accessToken,
                _refreshValidationParameters,
                out var validatedToken);

            // Guards against an algorithm-confusion attack: a token presented as "none" or signed
            // with an asymmetric algorithm must not be accepted just because it parses.
            if (validatedToken is not JwtSecurityToken jwt ||
                !jwt.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return principal;
        }
        catch (SecurityTokenException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
