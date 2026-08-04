using Marketing.DataAccess.Context;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Marketing.IntegrationTests.Fixtures;

/// <summary>
/// Boots the real API host against throwaway PostgreSQL and Redis containers.
/// <para>
/// Real containers rather than in-memory doubles, because the behaviour worth testing here is
/// exactly what a fake cannot reproduce: partial unique indexes, the <c>xmin</c> concurrency token,
/// <c>jsonb</c> columns and the global query filters as PostgreSQL actually applies them.
/// </para>
/// <para>Requires a working Docker daemon.</para>
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("marketing_tests")
        .WithUsername("marketing")
        .WithPassword("marketing_test_password")
        .WithCleanUp(true)
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCleanUp(true)
        .Build();

    /// <summary>Address of the bootstrap administrator seeded for these tests.</summary>
    public const string AdministratorEmail = "admin@integration.test";

    /// <summary>Password of the bootstrap administrator seeded for these tests.</summary>
    public const string AdministratorPassword = "IntegrationTest!Password1";

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    /// <summary>Opens a scope and resolves the database context, for arranging and asserting state.</summary>
    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    /// <summary>Runs an action against a scoped database context.</summary>
    /// <param name="action">Work to perform.</param>
    public async Task WithDbContextAsync(Func<ApplicationDbContext, Task> action)
    {
        await using var scope = CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await action(context);
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = _postgres.GetConnectionString(),
                ["Database:ApplyMigrationsOnStartup"] = "true",
                ["Database:SeedOnStartup"] = "true",
                ["Database:EnableSensitiveDataLogging"] = "false",

                ["Redis:ConnectionString"] = _redis.GetConnectionString(),
                ["Redis:Enabled"] = "true",

                ["Authentication:Jwt:Issuer"] = "https://api.integration.test",
                ["Authentication:Jwt:Audience"] = "integration-tests",
                ["Authentication:Jwt:SigningKey"] =
                    "integration-test-signing-key-long-enough-for-hmac-sha256",
                ["Authentication:Jwt:AccessTokenLifetimeMinutes"] = "15",

                // Low so the lockout tests do not have to issue five failed requests.
                ["Authentication:Policy:MaxFailedLoginAttempts"] = "3",
                ["Authentication:PasswordHashing:Iterations"] = "100000",

                ["WhatsApp:AppId"] = "000000000000000",
                ["WhatsApp:AppSecret"] = "integration-test-secret",
                ["WhatsApp:WebhookVerifyToken"] = "integration-verify-token",

                ["Cors:AllowedOrigins:0"] = "http://localhost:4200",

                ["Bootstrap:AdministratorEmail"] = AdministratorEmail,
                ["Bootstrap:AdministratorPassword"] = AdministratorPassword,
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove only Quartz's hosted service. Clearing every IHostedService would take the
            // test server itself down with it, since that is hosted the same way.
            var schedulerServices = services
                .Where(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType?.FullName?.StartsWith("Quartz.", StringComparison.Ordinal) == true)
                .ToList();

            foreach (var descriptor in schedulerServices)
            {
                services.Remove(descriptor);
            }
        });
    }
}
