using System.Reflection;
using Asp.Versioning.ApiExplorer;
using Marketing.API.Configurations;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Marketing.API.Extensions;

/// <summary>Configures OpenAPI generation and the Swagger UI.</summary>
public static class SwaggerExtensions
{
    /// <summary>Registers Swagger generation with bearer security and XML documentation.</summary>
    /// <param name="services">Service collection.</param>
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddEndpointsApiExplorer();
        services.ConfigureOptions<ConfigureSwaggerOptions>();

        services.AddSwaggerGen(options =>
        {
            options.SupportNonNullableReferenceTypes();
            options.UseAllOfToExtendReferenceSchemas();

            const string schemeId = "Bearer";

            options.AddSecurityDefinition(schemeId, new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description =
                    "JWT access token issued by POST /api/v1/auth/login. Enter the token only - the "
                    + "'Bearer' prefix is added for you.",
            });

            // Microsoft.OpenApi v2 split the scheme from the reference to it: a requirement now
            // points at a registered definition by id instead of embedding a self-referencing
            // copy of the scheme object.
            //
            // The document argument is not optional in practice. A reference constructed without
            // it has nothing to resolve against, so it serialises as an empty object and the
            // document ends up declaring "security": [ { } ] - which reads as "no authentication
            // required". Swagger UI then stores the token from the Authorize dialog and never
            // sends it, and every call comes back 401 with no Authorization header in the curl.
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(schemeId, document)] = [],
            });

            // Surfaces the same XML comments that document the code, so the API reference and the
            // source cannot drift apart.
            IncludeXmlComments(options, Assembly.GetExecutingAssembly());
            IncludeXmlComments(options, typeof(Application.Interfaces.IAuthenticationService).Assembly);
            IncludeXmlComments(options, typeof(Common.Responses.PagedResult<>).Assembly);
        });

        return services;
    }

    /// <summary>Maps the Swagger endpoints and UI. Development only.</summary>
    /// <param name="app">Application builder.</param>
    public static WebApplication UseApiDocumentation(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseSwagger(options => options.RouteTemplate = "swagger/{documentName}/swagger.json");

        app.UseSwaggerUI(options =>
        {
            var provider = app.Services.GetRequiredService<IApiVersionDescriptionProvider>();

            foreach (var description in provider.ApiVersionDescriptions.Reverse())
            {
                options.SwaggerEndpoint(
                    $"/swagger/{description.GroupName}/swagger.json",
                    $"Marketing API {description.GroupName.ToUpperInvariant()}");
            }

            options.DocumentTitle = "WhatsApp Marketing SaaS API";
            options.DisplayRequestDuration();
            options.EnableTryItOutByDefault();
        });

        return app;
    }

    private static void IncludeXmlComments(SwaggerGenOptions options, Assembly assembly)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"{assembly.GetName().Name}.xml");

        if (File.Exists(path))
        {
            options.IncludeXmlComments(path, includeControllerXmlComments: true);
        }
    }
}
