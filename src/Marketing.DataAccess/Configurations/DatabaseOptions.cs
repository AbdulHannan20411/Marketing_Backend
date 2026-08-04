using System.ComponentModel.DataAnnotations;

namespace Marketing.DataAccess.Configurations;

/// <summary>Bound from the <c>Database</c> configuration section and validated at startup.</summary>
public sealed class DatabaseOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Database";

    /// <summary>Npgsql connection string. Supplied by user secrets or the environment, never committed.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>Attempts made against transient PostgreSQL failures before giving up.</summary>
    [Range(0, 10)]
    public int MaxRetryCount { get; init; } = 3;

    /// <summary>Ceiling on the exponential backoff between retries.</summary>
    [Range(1, 60)]
    public int MaxRetryDelaySeconds { get; init; } = 10;

    /// <summary>Command timeout in seconds.</summary>
    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>Duration above which a command is logged as slow.</summary>
    [Range(1, 60_000)]
    public int SlowQueryThresholdMilliseconds { get; init; } = 500;

    /// <summary>
    /// Whether EF Core may include parameter values in logs and exceptions.
    /// <para>
    /// Parameter values here are customer PII and password hashes. This must remain false outside
    /// a developer's machine; <c>AddDataAccess</c> refuses to honour it in production.
    /// </para>
    /// </summary>
    public bool EnableSensitiveDataLogging { get; init; }

    /// <summary>Whether to emit detailed EF Core errors. Development only.</summary>
    public bool EnableDetailedErrors { get; init; }

    /// <summary>Whether to run pending migrations on startup. Suitable for development only.</summary>
    public bool ApplyMigrationsOnStartup { get; init; }

    /// <summary>Whether to seed baseline roles and the bootstrap platform administrator.</summary>
    public bool SeedOnStartup { get; init; }
}
