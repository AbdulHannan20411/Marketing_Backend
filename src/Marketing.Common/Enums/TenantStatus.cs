namespace Marketing.Common.Enums;

/// <summary>Lifecycle state of a tenant. Persisted as a string for readability in the database.</summary>
public enum TenantStatus
{
    /// <summary>Created but has not completed onboarding; sign-in is blocked.</summary>
    Pending = 0,

    /// <summary>Fully operational.</summary>
    Active = 1,

    /// <summary>Temporarily disabled, typically for non-payment. Data is retained, sign-in is blocked.</summary>
    Suspended = 2,

    /// <summary>Closed by the customer or the platform. Retained only for the contractual window.</summary>
    Cancelled = 3,
}
