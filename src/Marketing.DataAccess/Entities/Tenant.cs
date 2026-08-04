using Marketing.Common.Enums;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// A customer organisation. The root of every isolation boundary in the system.
/// <para>
/// Deliberately not <see cref="ITenantScoped"/>: the tenant table is platform-level data, guarded
/// by the platform-administration policy rather than by the tenant query filter.
/// </para>
/// </summary>
public sealed class Tenant : BaseEntity
{
    /// <summary>Legal or trading name shown throughout the product.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// URL-safe unique identifier. Used for sign-in routing and log correlation; never used as an
    /// authorisation input.
    /// </summary>
    public required string Slug { get; set; }

    /// <summary>Lifecycle state. Anything other than <see cref="TenantStatus.Active"/> blocks sign-in.</summary>
    public TenantStatus Status { get; set; } = TenantStatus.Pending;

    /// <summary>Primary billing and notification contact address.</summary>
    public required string ContactEmail { get; set; }

    /// <summary>IANA time zone used when rendering schedules and reports for this tenant.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>ISO 4217 currency used for billing.</summary>
    public string CurrencyCode { get; set; } = "USD";

    /// <summary>Maximum contacts the plan allows. Enforced by the contact import and create paths.</summary>
    public int ContactQuota { get; set; }

    /// <summary>Maximum outbound messages per calendar month.</summary>
    public int MonthlyMessageQuota { get; set; }

    /// <summary>Instant onboarding completed, in UTC.</summary>
    public DateTimeOffset? ActivatedOn { get; set; }

    /// <summary>Instant the tenant was suspended, in UTC.</summary>
    public DateTimeOffset? SuspendedOn { get; set; }

    /// <summary>Users belonging to this tenant.</summary>
    public ICollection<User> Users { get; set; } = [];
}
