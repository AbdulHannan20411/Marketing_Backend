using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Marketing.API.Configurations;
using Marketing.API.Extensions;
using Marketing.API.Filters;
using Marketing.API.Middlewares;
using Marketing.Application.Extensions;
using Marketing.Business.Extensions;
using Marketing.Common.Constants;
using Marketing.DataAccess.Extensions;
using Marketing.Infrastructure.Email;
using Marketing.Infrastructure.Extensions;
using Marketing.Infrastructure.Logging;
using Marketing.Scheduler.Extensions;
using Marketing.Shared.Abstractions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Serilog;

// A bootstrap logger so failures during host construction - a malformed configuration file, a
// missing signing key - are recorded instead of vanishing into an unhandled exception.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, _, configuration) =>
        SerilogConfigurator.Configure(configuration, context.Configuration, context.HostingEnvironment));

    // ---------------------------------------------------------------------------------------
    // Layer registration, outermost to innermost.
    // ---------------------------------------------------------------------------------------
    builder.Services.AddDataAccess(builder.Configuration, builder.Environment.IsDevelopment());
    builder.Services.AddRepositories();
    builder.Services.AddApplicationServices(builder.Configuration);
    builder.Services.AddInfrastructure(builder.Configuration);

    var smtpConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Smtp:Host"]);

    if (builder.Environment.IsDevelopment() && !smtpConfigured)
    {
        // Keeps a copy of every message the platform tries to send, so an invitation or reset can
        // be completed without a mail provider. Registered here rather than inside AddInfrastructure
        // so the capture path cannot reach any other environment: invitation tokens are stored
        // hashed, so the raw token exists only in the message body this holds in memory.
        //
        // Skipped once a relay is configured - real delivery is what the operator asked for, and
        // silently intercepting it would be worse than useless.
        builder.Services.AddSingleton<DevMailbox>();
        builder.Services.AddSingleton<IEmailSender, CapturingEmailSender>();
    }

    // Discovers every [ScheduledJob] class in Marketing.Scheduler and wires it into Quartz.
    // Adding a job - an email dispatcher, a campaign runner - needs no change here.
    builder.Services.AddScheduler(builder.Configuration);

    // ---------------------------------------------------------------------------------------
    // API surface.
    // ---------------------------------------------------------------------------------------
    builder.Services.AddApiAuthentication(builder.Configuration);
    builder.Services.AddApiCors(builder.Configuration);
    builder.Services.AddApiVersioningSupport();
    builder.Services.AddApiRateLimiting();
    builder.Services.AddApiHealthChecks(builder.Configuration);
    builder.Services.AddApiDocumentation();
    builder.Services.AddRealtime(builder.Configuration);

    builder.Services
        .AddControllers(options =>
        {
            options.Filters.Add<FluentValidationActionFilter>();
            options.SuppressAsyncSuffixInActionNames = false;
        })
        .AddJsonOptions(options =>
        {
            // Nulls are meaningful in this contract - limit: null means unlimited, and the client
            // renders it as an infinity glyph - so they must be serialised, not omitted.
            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;

            // The import contract's enums are PascalCase. Registered first, because the first
            // matching converter wins and the general one below would otherwise claim them.
            foreach (var converter in Marketing.Application.DTOs.Imports.ImportContractJson.Converters)
            {
                options.JsonSerializerOptions.Converters.Add(converter);
            }

            foreach (var converter in Marketing.Application.DTOs.Payments.PaymentContractJson.Converters)
            {
                options.JsonSerializerOptions.Converters.Add(converter);
            }

            // Every other enum crosses the wire as a camelCase string. Integers would break every
            // badge and filter silently, because the client compares exact literals such as
            // "subscribed".
            options.JsonSerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

    // Model binding failures are turned into the same exception the rest of the stack throws, so
    // a malformed payload and a failed business rule produce identically shaped problem documents.
    builder.Services.Configure<ApiBehaviorOptions>(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState
                .Where(entry => entry.Value?.Errors.Count > 0)
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value!.Errors.Select(error => error.ErrorMessage).ToArray());

            throw new Marketing.Common.Exceptions.ValidationException(errors);
        };
    });

    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    builder.Services.AddResponseCompression(options =>
    {
        // Only over HTTPS-terminated transport that this API always uses; combined with the fact
        // that no secret is reflected from the request into the response body, this avoids the
        // BREACH class of compression side channel.
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
        options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/problem+json"]);
    });

    builder.Services.Configure<BrotliCompressionProviderOptions>(
        options => options.Level = CompressionLevel.Fastest);
    builder.Services.Configure<GzipCompressionProviderOptions>(
        options => options.Level = CompressionLevel.Fastest);

    // The API runs behind a reverse proxy, so the client address and scheme have to be recovered
    // from the forwarded headers - otherwise every rate-limit partition keyed by IP collapses onto
    // the proxy's address.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 2;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });

    var app = builder.Build();

    // ---------------------------------------------------------------------------------------
    // Pipeline. Order is behaviour, not preference - see the notes on each stage.
    // ---------------------------------------------------------------------------------------

    // First, so downstream middleware sees the real client address and scheme.
    app.UseForwardedHeaders();

    // Before anything that can throw, so every failure becomes a problem document.
    app.UseExceptionHandler();

    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseResponseCompression();

    if (app.Environment.IsDevelopment())
    {
        app.UseApiDocumentation();
    }
    else
    {
        app.UseHsts();
    }

    app.UseHttpsRedirection();

    // After authentication so the log context can carry the user and tenant, and before the
    // request-logging middleware so those properties appear on the completion entry.
    app.UseRouting();
    app.UseCors(ApiCorsOptions.PolicyName);
    app.UseAuthentication();
    app.UseMiddleware<CorrelationIdMiddleware>();

    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate =
            "{RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";

        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            var currentUser = httpContext.RequestServices.GetRequiredService<ICurrentUser>();
            var tenantContext = httpContext.RequestServices.GetRequiredService<ITenantContext>();
            var requestContext = httpContext.RequestServices.GetRequiredService<IRequestContext>();

            SerilogConfigurator.EnrichFromRequest(
                diagnosticContext,
                requestContext.CorrelationId,
                tenantContext.TenantSlug,
                currentUser.UserId);
        };
    });

    // After authentication: partitions keyed by tenant or user need the principal to exist.
    app.UseRateLimiter();
    app.UseAuthorization();

    app.MapControllers().RequireRateLimiting(AppConstants.RateLimits.Default);
    app.MapApiHealthChecks();

    // After UseAuthorization, so the hub's [Authorize] is enforced by the same pipeline
    // that guards the REST endpoints.
    app.MapRealtime();

    await app.InitialiseDatabaseAsync();

    await app.RunAsync();
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    Log.Fatal(exception, "The API host terminated unexpectedly.");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>
/// Exposed so <c>WebApplicationFactory</c> can boot the real host in integration tests.
/// </summary>
public partial class Program
{
    /// <summary>Prevents instantiation of the entry-point shim.</summary>
    protected Program()
    {
    }
}
