using System.Reflection;
using FluentValidation;
using Marketing.Application.Configurations;
using Marketing.Application.Interfaces;
using Marketing.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Marketing.Application.Extensions;

/// <summary>Registers the business-rules layer.</summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>Registers services, validators, mapping profiles and bound options.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var assembly = Assembly.GetExecutingAssembly();

        services.AddOptions<AuthenticationPolicyOptions>()
            .Bind(configuration.GetSection(AuthenticationPolicyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddAutoMapper(options => options.AddMaps(assembly));

        // Scoped rather than singleton: validators may depend on repositories for uniqueness
        // checks, and a singleton holding a scoped DbContext would be a captive dependency.
        services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Scoped, includeInternalTypes: false);

        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddScoped<ISessionMaintenanceService, SessionMaintenanceService>();
        services.AddScoped<ITenantScopeResolver, TenantScopeResolver>();
        services.AddScoped<IContactService, ContactService>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();
        services.AddScoped<ICampaignService, CampaignService>();
        services.AddScoped<IWhatsAppService, WhatsAppService>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<IPlanManagementService, PlanManagementService>();
        services.AddScoped<IEmployeeService, EmployeeService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<IPlatformService, PlatformService>();

        return services;
    }
}
