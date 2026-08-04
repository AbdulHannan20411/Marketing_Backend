using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
    private readonly JwtSecurityTokenHandler _tokenHandler = new();

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
            new(JwtRegisteredClaimNames.Sub, descriptor.UserId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Email, descriptor.Email),
            new(ApplicationClaimTypes.DisplayName, descriptor.DisplayName),
            new(ApplicationClaimTypes.SessionId, descriptor.SessionId.ToString()),
            new(
                JwtRegisteredClaimNames.Iat,
                issuedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
        };

        // The tenant claim is the whole isolation boundary. It is written here from a value read
        // out of the user's own row, and read back only through ITenantContext.
        if (descriptor.TenantId is { } tenantId)
        {
            claims.Add(new Claim(ApplicationClaimTypes.TenantId, tenantId.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(descriptor.TenantSlug))
        {
            claims.Add(new Claim(ApplicationClaimTypes.TenantSlug, descriptor.TenantSlug));
        }

        claims.AddRange(descriptor.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(descriptor.Permissions.Select(
            permission => new Claim(ApplicationClaimTypes.Permission, permission)));

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
    public string HashRefreshToken(string refreshToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        // A plain SHA-256, not a password hash. The input is 512 bits of entropy from a CSPRNG, so
        // it is not brute-forceable and a deliberately slow KDF would only add latency to every
        // refresh for no security gain.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));

        return Convert.ToHexStringLower(digest);
    }

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
