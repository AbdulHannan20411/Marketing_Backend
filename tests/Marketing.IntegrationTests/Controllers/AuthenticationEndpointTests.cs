using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Marketing.Common.Constants;
using Marketing.DataAccess.Entities;
using Marketing.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Marketing.IntegrationTests.Controllers;

/// <summary>End-to-end coverage of the authentication endpoints against real PostgreSQL and Redis.</summary>
public sealed class AuthenticationEndpointTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _factory;

    public AuthenticationEndpointTests(ApiFactory factory) => _factory = factory;

    /// <summary>Ties every awaited call to the test runner's cancellation, so a hung request fails fast.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Envelope<TData>(TData Data, string? Message, bool Success);

    private sealed record AuthPayload(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAtUtc,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAtUtc,
        JsonElement User);

    private static async Task<AuthPayload> SignInAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = ApiFactory.AdministratorEmail, password = ApiFactory.AdministratorPassword },
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var envelope = await response.Content.ReadFromJsonAsync<Envelope<AuthPayload>>(Json, Ct);

        return envelope!.Data;
    }

    [Fact]
    public async Task Seeded_administrator_can_sign_in_and_receives_a_token_pair()
    {
        var client = _factory.CreateClient();

        var payload = await SignInAsync(client);

        payload.AccessToken.Should().NotBeNullOrWhiteSpace();
        payload.RefreshToken.Should().NotBeNullOrWhiteSpace();
        payload.AccessTokenExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task The_profile_response_never_carries_a_tenant_identifier()
    {
        var client = _factory.CreateClient();
        var payload = await SignInAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.AccessToken);

        var response = await client.GetAsync("/api/v1/auth/me", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(Ct);

        // The contract the Angular client depends on: the browser is told the organisation's name,
        // never its key. A regression here is a tenancy leak, not a cosmetic change.
        body.Should().NotContain("tenantId", "the tenant key must never be serialised to a client");
        body.Should().Contain("tenantName");
    }

    [Fact]
    public async Task The_profile_of_the_seeded_administrator_reports_the_super_admin_role()
    {
        var client = _factory.CreateClient();
        var payload = await SignInAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.AccessToken);

        var body = await (await client.GetAsync("/api/v1/auth/me", Ct)).Content.ReadAsStringAsync(Ct);

        body.Should().Contain(Roles.SuperAdmin);
        body.Should().Contain(Permissions.Platform.All);
    }

    [Fact]
    public async Task Wrong_credentials_are_refused_with_a_problem_document()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = ApiFactory.AdministratorEmail, password = "definitely-not-the-password" },
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("errorCode");
    }

    [Fact]
    public async Task An_unknown_address_and_a_wrong_password_are_indistinguishable()
    {
        var client = _factory.CreateClient();

        var unknown = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = "nobody@integration.test", password = "whatever" },
            Ct);

        var wrongPassword = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = ApiFactory.AdministratorEmail, password = "definitely-not-the-password" },
            Ct);

        // Identical status and body, so the endpoint cannot be used to enumerate registered users.
        unknown.StatusCode.Should().Be(wrongPassword.StatusCode);

        var unknownBody = await unknown.Content.ReadAsStringAsync(Ct);
        var wrongBody = await wrongPassword.Content.ReadAsStringAsync(Ct);

        ExtractDetail(unknownBody).Should().Be(ExtractDetail(wrongBody));
    }

    [Fact]
    public async Task A_malformed_request_returns_a_validation_problem_with_field_errors()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = "not-an-email", password = string.Empty },
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("errors").And.Contain("validation_failed");
    }

    [Fact]
    public async Task Protected_endpoints_reject_an_anonymous_caller()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/me", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_refresh_token_can_be_used_once_and_the_replay_revokes_the_session()
    {
        var client = _factory.CreateClient();
        var payload = await SignInAsync(client);

        var first = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = payload.RefreshToken },
            Ct);

        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var replay = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = payload.RefreshToken },
            Ct);

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Every session for that user is torn down, not just the replayed one.
        await _factory.WithDbContextAsync(async context =>
        {
            var live = await context.RefreshTokens
                .IgnoreQueryFilters()
                .CountAsync(token => token.RevokedOn == null && token.ConsumedOn == null, Ct);

            live.Should().Be(0);
        });
    }

    [Fact]
    public async Task Every_response_carries_a_correlation_id()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live", Ct);

        response.Headers.Should().ContainKey(AppConstants.Headers.CorrelationId);
    }

    [Fact]
    public async Task Security_headers_are_present_on_every_response()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live", Ct);

        response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        response.Headers.Should().ContainKey("Content-Security-Policy");
    }

    [Fact]
    public async Task Sign_in_writes_an_audit_trail_entry_in_the_same_transaction()
    {
        var client = _factory.CreateClient();
        await SignInAsync(client);

        await _factory.WithDbContextAsync(async context =>
        {
            var entries = await context.AuditLogs
                .AsNoTracking()
                .Where(log => log.EntityName == nameof(User))
                .ToListAsync(Ct);

            entries.Should().NotBeEmpty();

            // The audit trail must never become a second copy of the credential store.
            entries.Should().AllSatisfy(entry =>
                entry.Changes.Should().NotContain("pbkdf2-sha256"));
        });
    }

    private static string? ExtractDetail(string problemJson)
    {
        using var document = JsonDocument.Parse(problemJson);

        return document.RootElement.TryGetProperty("detail", out var detail)
            ? detail.GetString()
            : null;
    }
}
