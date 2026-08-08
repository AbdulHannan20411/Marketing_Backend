using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
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

    private static TokenSubject Subject(long? tenantId) => new(
        UserId: 1001,
        Email: "operator@example.com",
        Name: "Operator",
        Role: tenantId is null ? Roles.SuperAdmin : Roles.Admin,
        Permissions: [Permissions.Contacts.View, Permissions.Reports.View],
        WorkspaceName: tenantId is null ? null : "Acme Retail",
        AvatarUrl: null,
        TenantId: tenantId,
        TenantSlug: tenantId is null ? null : "acme",
        SessionId: Guid.Parse("99999999-9999-9999-9999-999999999999"));

    [Fact]
    public void An_access_token_carries_the_tenant_claim_for_a_tenant_user()
    {
        var tenantId = 2001;
        var token = CreateService().CreateAccessToken(Subject(tenantId));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.Value);

        jwt.Claims.Should().ContainSingle(claim => claim.Type == AppConstants.Claims.TenantId)
            .Which.Value.Should().Be(tenantId.ToString());

        // Single-valued role claim, as the client contract requires, plus the framework's own role
        // claim type so server-side RequireRole keeps working.
        jwt.Claims.Should().ContainSingle(claim => claim.Type == AppConstants.Claims.Role)
            .Which.Value.Should().Be(Roles.Admin);

        jwt.Claims.Should().Contain(claim =>
            claim.Type == ClaimTypes.Role && claim.Value == Roles.Admin);

        jwt.Claims.Should().ContainSingle(claim => claim.Type == AppConstants.Claims.Name)
            .Which.Value.Should().Be("Operator");

        jwt.Claims.Should().ContainSingle(claim => claim.Type == AppConstants.Claims.WorkspaceName)
            .Which.Value.Should().Be("Acme Retail");
    }

    [Fact]
    public void The_decoded_payload_matches_the_shape_the_client_reads()
    {
        var token = CreateService().CreateAccessToken(Subject(5100));

        // Decoded the way the client does it - straight from the base64url payload - rather than
        // through JwtSecurityTokenHandler, which re-expands a JSON array claim back into repeated
        // claims and so cannot show whether the wire format is an array.
        var payload = DecodePayload(token.Value);

        payload.GetProperty("permissions").ValueKind.Should().Be(
            JsonValueKind.Array,
            "the client always indexes permissions, so a single grant must not serialise as a bare string");

        payload.GetProperty("permissions").EnumerateArray()
            .Select(element => element.GetString())
            .Should().Contain(Permissions.Contacts.View);

        payload.GetProperty("role").ValueKind.Should().Be(
            JsonValueKind.String,
            "the client reads one role string, not a list");

        foreach (var required in new[] { "sub", "email", "name", "role", "permissions", "workspaceName", "iat", "exp" })
        {
            payload.TryGetProperty(required, out _).Should().BeTrue($"'{required}' is a mandatory claim");
        }
    }

    /// <summary>Base64url-decodes a JWT payload without validating it, as a browser would.</summary>
    private static JsonElement DecodePayload(string token)
    {
        var payload = token.Split('.')[1];
        var padded = payload.Replace('-', '+').Replace('_', '/').PadRight((payload.Length + 3) / 4 * 4, '=');

        return JsonDocument.Parse(Convert.FromBase64String(padded)).RootElement.Clone();
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
        var token = CreateService().CreateAccessToken(Subject(5100));

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
        var token = service.CreateAccessToken(Subject(5101));

        _clock.UtcNow = Now.AddHours(2);

        var principal = service.GetPrincipalFromExpiredToken(token.Value);

        principal.Should().NotBeNull();
        principal!.FindFirst(JwtRegisteredClaimNames.Sub)!.Value
            .Should().Be("1001");
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

        var foreignToken = foreignService.CreateAccessToken(Subject(5102));

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
