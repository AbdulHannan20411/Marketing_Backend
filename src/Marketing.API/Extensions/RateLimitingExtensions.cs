using System.Globalization;
using System.Threading.RateLimiting;
using Marketing.Common.Constants;
using Microsoft.AspNetCore.RateLimiting;

namespace Marketing.API.Extensions;

/// <summary>Configures the per-endpoint rate-limiting policies.</summary>
public static class RateLimitingExtensions
{
    /// <summary>
    /// Registers the global limiter and the named policies.
    /// <para>
    /// Partitioning matters more than the numbers. Authentication is partitioned by client address
    /// because there is no authenticated identity yet; everything else is partitioned by tenant, so
    /// one customer's bulk import cannot consume another customer's budget. In a multi-tenant
    /// system a single global bucket is a cross-tenant denial of service waiting to happen.
    /// </para>
    /// </summary>
    /// <param name="services">Service collection.</param>
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                context.HttpContext.Response.ContentType = "application/problem+json";

                await context.HttpContext.Response.WriteAsync(
                    """
                    {"type":"https://docs.marketing-platform.io/errors/rate_limit_exceeded",
                     "title":"Too many requests",
                     "status":429,
                     "detail":"The rate limit for this endpoint has been exceeded. Retry after the interval in the Retry-After header.",
                     "errorCode":"rate_limit_exceeded"}
                    """,
                    cancellationToken);
            };

            // Backstop applied to every request, including endpoints that declare no policy.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetTokenBucketLimiter(
                    ResolvePartitionKey(context),
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 300,
                        TokensPerPeriod = 300,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));

            // Tight and per-address: this is the bucket that makes credential stuffing expensive.
            limiter.AddPolicy(AppConstants.RateLimits.Authentication, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ResolveClientAddress(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            // Reports are expensive to compute. Concurrency, not throughput, is the constraint.
            limiter.AddPolicy(AppConstants.RateLimits.Reports, context =>
                RateLimitPartition.GetConcurrencyLimiter(
                    ResolvePartitionKey(context),
                    _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = 4,
                        QueueLimit = 8,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    }));

            limiter.AddPolicy(AppConstants.RateLimits.Campaigns, context =>
                RateLimitPartition.GetTokenBucketLimiter(
                    ResolvePartitionKey(context),
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 60,
                        TokensPerPeriod = 60,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        QueueLimit = 10,
                        AutoReplenishment = true,
                    }));

            // Generous by design. Meta retries webhooks aggressively and de-subscribes endpoints
            // that reject or stall, so throttling this one costs delivery receipts.
            limiter.AddPolicy(AppConstants.RateLimits.Webhook, _ =>
                RateLimitPartition.GetTokenBucketLimiter(
                    "webhook",
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 2_000,
                        TokensPerPeriod = 2_000,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        QueueLimit = 100,
                        AutoReplenishment = true,
                    }));

            limiter.AddPolicy(AppConstants.RateLimits.Admin, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ResolveUserKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            limiter.AddPolicy(AppConstants.RateLimits.Default, context =>
                RateLimitPartition.GetTokenBucketLimiter(
                    ResolvePartitionKey(context),
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = 200,
                        TokensPerPeriod = 200,
                        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                        QueueLimit = 20,
                        AutoReplenishment = true,
                    }));
        });

        return services;
    }

    /// <summary>
    /// Prefers the tenant, then the user, then the client address. Read from validated claims, so a
    /// caller cannot widen their own budget by forging a header.
    /// </summary>
    private static string ResolvePartitionKey(HttpContext context)
    {
        var tenantId = context.User.FindFirst(AppConstants.Claims.TenantId)?.Value;

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            return $"tenant:{tenantId}";
        }

        return ResolveUserKey(context);
    }

    private static string ResolveUserKey(HttpContext context)
    {
        var userId = context.User.FindFirst("sub")?.Value;

        return string.IsNullOrWhiteSpace(userId)
            ? ResolveClientAddress(context)
            : $"user:{userId}";
    }

    private static string ResolveClientAddress(HttpContext context) =>
        $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}
