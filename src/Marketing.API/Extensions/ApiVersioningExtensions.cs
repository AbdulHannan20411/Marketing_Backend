using Asp.Versioning;

namespace Marketing.API.Extensions;

/// <summary>Configures URL-segment API versioning.</summary>
public static class ApiVersioningExtensions
{
    /// <summary>Registers versioning and the version-aware API explorer used by Swagger.</summary>
    /// <param name="services">Service collection.</param>
    public static IServiceCollection AddApiVersioningSupport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;

                // Advertises supported and deprecated versions on every response, so a client can
                // detect an impending removal without reading a changelog.
                options.ReportApiVersions = true;

                // The version lives in the path - /api/v1/... - because it is the form that
                // survives proxies, browser address bars, curl and support tickets intact.
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
            });

        return services;
    }
}
