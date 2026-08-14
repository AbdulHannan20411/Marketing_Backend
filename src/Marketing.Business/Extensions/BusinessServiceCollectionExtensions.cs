using Marketing.Business.Repositories.Implementations;
using Marketing.Business.Repositories.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Marketing.Business.Extensions;

/// <summary>Registers the repository layer.</summary>
public static class BusinessServiceCollectionExtensions
{
    /// <summary>
    /// Registers the generic repository, the unit of work, the aggregate repositories and the
    /// Dapper executor.
    /// </summary>
    /// <param name="services">Service collection.</param>
    public static IServiceCollection AddRepositories(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Open generic: a new aggregate gets a working repository with no registration change.
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IUserTokenRepository, UserTokenRepository>();
        services.AddScoped<ISqlQueryExecutor, SqlQueryExecutor>();
        services.AddScoped<IQueryExecutor, QueryExecutor>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IWhatsAppConnectionRepository, WhatsAppConnectionRepository>();
        services.AddScoped<ICampaignMessageRepository, CampaignMessageRepository>();
        services.AddScoped<IImportJobRepository, ImportJobRepository>();

        return services;
    }
}
