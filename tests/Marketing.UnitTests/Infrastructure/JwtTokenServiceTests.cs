using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AwesomeAssertions;
using Marketing.Common.Constants;
using Marketing.Infrastructure.Authentication;
using Marketing.Shared.Models;
using Microsoft.Extensions.Options;

namespace Marketing.UnitTests.Infrastructure;

public sealed class JwtTokenServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    private static readonly JwtOptions DefaultOptions = new()
    {
        Issuer = "https://api.test",
        Audience = "test-audience",
        SigningKey = "unit-test-signing-key-that-is-long-enough-for-hmac-sha256",
        AccessTokenLifetimeMinutes = 15,
        RefreshTokenLifetimeDays = 14,
    };

    private readonly FixedDateTimeProvider _clock = new(Now);

    private JwtTokenService CreateService() => new(Options.Create(DefaultOptions), _clock);

    private static TokenSubject Subject(Guid? tenantId) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "operator@example.com",
        "Operator",
        tenantId,
        tenantId is null ? null : "acme",
        Guid.Parse("99999999-9999-9999-9999-999999999999"),
        [Roles.Admin],
        ["contacts:read"]);

    [Fact]
    public void An_access_token_carries_the_tenant_claim_for_a_tenant_user()
    {
        var tenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var token = CreateService().CreateAccessToken(Subject(tenantId));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.Value);

        jwt.Claims.Should().ContainSingle(claim => claim.Type == AppConstants.Claims.TenantId)
            .Which.Value.Should().Be(tenantId.ToString());

        jwt.Claims.Should().Contain(claim =>
            claim.Type == ClaimTypes.Role && claim.Value == Roles.Admin);

        jwt.Claims.Should().Contain(claim =>
            claim.Type == AppConstants.Claims.Permission && claim.Value == "contacts:read");
    }

    [Fact]
    public void An_access_token_for_a_platform_administrator_carries_no_tenant_claim()
    {
        var token = CreateService().CreateAccessToken(Subject(tenantId: null));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.Value);

        // Absence, not an empty string: a claim present but blank would be read as "tenant zero"
        // by anything doing a naive TryParse.
        jwt.Claims.Should().NotContain(claim => claim.Type == AppConstants.Claims.TenantId);
    }

    [Fact]
    public void The_access_token_expires_at_the_configured_lifetime()
    {
        var token = CreateService().CreateAccessToken(Subject(Guid.NewGuid()));

        token.ExpiresAtUtc.Should().Be(Now.AddMinutes(DefaultOptions.AccessTokenLifetimeMinutes));
    }

    [Fact]
    public void Refresh_tokens_are_unique_and_their_hash_is_reproducible()
    {
        var service = CreateService();

        var first = service.CreateRefreshToken();
        var second = service.CreateRefreshToken();

        first.Value.Should().NotBe(second.Value);
        first.Hash.Should().NotBe(first.Value, "the plaintext must never be what is persisted");
        first.Hash.Should().HaveLength(64, "a SHA-256 digest is 64 hex characters");

        // Reproducible, because refresh works by hashing what the client presents and matching it.
        service.HashRefreshToken(first.Value).Should().Be(first.Hash);
    }

    [Fact]
    public void An_expired_token_still_yields_its_claims_for_the_refresh_flow()
    {
        var service = CreateService();
        var token = service.CreateAccessToken(Subject(Guid.NewGuid()));

        _clock.UtcNow = Now.AddHours(2);

        var principal = service.GetPrincipalFromExpiredToken(token.Value);

        principal.Should().NotBeNull();
        principal!.FindFirst(JwtRegisteredClaimNames.Sub)!.Value
            .Should().Be("11111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public void A_token_signed_with_another_key_is_rejected()
    {
        var foreignService = new JwtTokenService(
            Options.Create(new JwtOptions
            {
                Issuer = DefaultOptions.Issuer,
                Audience = DefaultOptions.Audience,
                SigningKey = "a-completely-different-key-also-long-enough-for-hmac256",
            }),
            _clock);

        var foreignToken = foreignService.CreateAccessToken(Subject(Guid.NewGuid()));

        CreateService().GetPrincipalFromExpiredToken(foreignToken.Value).Should().BeNull();
    }

    [Fact]
    public void Garbage_input_is_rejected_without_throwing()
    {
        var service = CreateService();

        service.GetPrincipalFromExpiredToken("not.a.token").Should().BeNull();
        service.GetPrincipalFromExpiredToken(string.Empty).Should().BeNull();
    }

    [Fact]
    public void A_signing_key_below_the_hmac_minimum_is_refused_at_construction()
    {
        var act = () => new JwtTokenService(
            Options.Create(new JwtOptions
            {
                Issuer = "i",
                Audience = "a",
                SigningKey = "too-short",
            }),
            _clock);

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least*");
    }
}
