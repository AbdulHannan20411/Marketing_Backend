using Marketing.API.Configurations;
using Marketing.Common.Constants;

namespace Marketing.API.Extensions;

/// <summary>Configures cross-origin resource sharing for the Angular client.</summary>
public static class CorsExtensions
{
    /// <summary>Registers the SPA CORS policy.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    public static IServiceCollection AddApiCors(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ApiCorsOptions>()
            .Bind(configuration.GetSection(ApiCorsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var corsOptions = configuration.GetSection(ApiCorsOptions.SectionName).Get<ApiCorsOptions>()
                          ?? new ApiCorsOptions();

        services.AddCors(options => options.AddPolicy(ApiCorsOptions.PolicyName, policy =>
        {
            policy
                .WithOrigins(corsOptions.AllowedOrigins)
                .AllowAnyMethod()
                .WithHeaders("Authorization", "Content-Type", "Accept", ApplicationHeaderNames.CorrelationId)
                // Without this the browser hides these from the client's JavaScript, so the SPA
                // cannot read paging counts or surface a correlation id in an error report.
                .WithExposedHeaders(
                    ApplicationHeaderNames.CorrelationId,
                    ApplicationHeaderNames.TotalCount,
                    ApplicationHeaderNames.ExceptionId,
                    "X-Token-Expired")
                .AllowCredentials()
                .SetPreflightMaxAge(TimeSpan.FromSeconds(corsOptions.PreflightMaxAgeSeconds));
        }));

        return services;
    }
}
