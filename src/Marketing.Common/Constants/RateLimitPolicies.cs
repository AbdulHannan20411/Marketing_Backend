namespace Marketing.Common.Constants;

/// <summary>
/// Named rate-limiting policies. Each endpoint group gets its own budget so that a burst of
/// report queries can never starve campaign dispatch or webhook ingestion.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Login, refresh and password endpoints. Partitioned by client IP, deliberately tight.</summary>
    public const string Authentication = "rl:authentication";

    /// <summary>Reporting and analytics endpoints. Partitioned by tenant; expensive, low concurrency.</summary>
    public const string Reports = "rl:reports";

    /// <summary>Campaign creation and dispatch. Partitioned by tenant.</summary>
    public const string Campaigns = "rl:campaigns";

    /// <summary>Meta webhook ingestion. High ceiling - Meta retries aggressively and drops slow endpoints.</summary>
    public const string Webhook = "rl:webhook";

    /// <summary>Platform administration endpoints. Partitioned by user.</summary>
    public const string Admin = "rl:admin";

    /// <summary>Default budget applied to everything else.</summary>
    public const string Default = "rl:default";
}
