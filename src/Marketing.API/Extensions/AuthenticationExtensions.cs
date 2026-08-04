using System.Text;
using Marketing.Common.Constants;
using Marketing.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Marketing.API.Extensions;

/// <summary>Configures bearer authentication and the authorization policies.</summary>
public static class AuthenticationExtensions
{
    /// <summary>Registers JWT bearer authentication and the platform's authorization policies.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    public static IServiceCollection AddApiAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
                         ?? throw new InvalidOperationException(
                             $"Configuration section '{JwtOptions.SectionName}' is missing.");

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                // Without this the handler rewrites "sub" to the long ClaimTypes.NameIdentifier
                // URI and "role" likewise, so what comes out no longer matches what was issued.
                options.MapInboundClaims = false;

                options.RequireHttpsMetadata = true;
                options.SaveToken = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtOptions.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(jwtOptions.ClockSkewSeconds),

                    // MapInboundClaims is off, so the principal is told explicitly which claims
                    // carry the name and the roles.
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = System.Security.Claims.ClaimTypes.Role,

                    // Refuses a token whose header names a different algorithm - the algorithm
                    // confusion attack that turns "alg: none" into a valid signature.
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                };

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        if (context.Exception is SecurityTokenExpiredException)
                        {
                            // Lets the client distinguish "refresh me" from "sign in again"
                            // without inspecting the token itself.
                            context.Response.Headers["X-Token-Expired"] = "true";
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyNames.PlatformAdministration, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(RoleNames.PlatformAdmin))
            .AddPolicy(PolicyNames.TenantAdministration, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(RoleNames.TenantOwner, RoleNames.PlatformAdmin))
            .AddPolicy(PolicyNames.TenantMembership, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(RoleNames.TenantUser, RoleNames.TenantOwner, RoleNames.PlatformAdmin))
            .AddPolicy(PolicyNames.RequireTenant, policy => policy
                .RequireAuthenticatedUser()
                // Guards endpoints that are meaningless without a tenant. Enforcing it as a policy
                // means the check happens before the action, not inside every service.
                .RequireClaim(ApplicationClaimTypes.TenantId));

        return services;
    }
}
