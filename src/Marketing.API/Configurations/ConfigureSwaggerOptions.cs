using Asp.Versioning.ApiExplorer;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Marketing.API.Configurations;

/// <summary>
/// Generates one Swagger document per discovered API version, so a new version appears in the UI
/// without anyone editing startup code.
/// </summary>
public sealed class ConfigureSwaggerOptions : IConfigureOptions<SwaggerGenOptions>
{
    private readonly IApiVersionDescriptionProvider _provider;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="provider">Supplies the discovered API versions.</param>
    public ConfigureSwaggerOptions(IApiVersionDescriptionProvider provider)
    {
        _provider = provider;
    }

    /// <inheritdoc />
    public void Configure(SwaggerGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var description in _provider.ApiVersionDescriptions)
        {
            options.SwaggerDoc(description.GroupName, CreateInfo(description));
        }
    }

    private static OpenApiInfo CreateInfo(ApiVersionDescription description)
    {
        var info = new OpenApiInfo
        {
            Title = "WhatsApp Marketing SaaS API",
            Version = description.ApiVersion.ToString(),
            Description =
                """
                Multi-tenant marketing platform built on the Meta WhatsApp Business Platform (Cloud API).

                **Tenancy.** The tenant is resolved from the `tenant_id` claim on the bearer token and
                from nowhere else. No endpoint accepts a tenant identifier in a path, query string,
                header or body, and none returns one to a tenant-scoped client.

                **Errors.** Failures are returned as RFC 7807 problem documents carrying a stable
                `errorCode`, a `correlationId` and, for server faults, an `exceptionId` to quote to
                support.
                """,
            Contact = new OpenApiContact
            {
                Name = "Platform Engineering",
                Email = "platform-engineering@marketing-platform.io",
            },
        };

        if (description.IsDeprecated)
        {
            info.Description += "\n\n**This API version is deprecated and will be removed.**";
        }

        return info;
    }
}
