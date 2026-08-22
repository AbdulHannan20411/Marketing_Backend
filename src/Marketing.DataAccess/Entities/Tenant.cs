using static Marketing.Common.Constants.AppConstants;

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

    /// <summary>Instant the owner switched the workspace off.</summary>
    public DateTimeOffset? DeactivatedOn { get; set; }

    /// <summary>Why they switched it off.</summary>
    public Marketing.Common.Constants.ContractEnums.DeactivationReason? DeactivationReason { get; set; }

    /// <summary>What they said, when they said anything.</summary>
    public string? DeactivationDetails { get; set; }

    /// <summary>Who did it. Recorded because "the workspace went dark" is asked about later.</summary>
    public long? DeactivatedByUserId { get; set; }

    /// <summary>
    /// Date the data stops being kept, or null when no promise was made.
    /// </summary>
    /// <remarks>
    /// Shown to the customer verbatim, which makes it a commitment rather than a note. If deletion
    /// after this date is ever automated, a workspace that comes back must be taken out of that
    /// queue - deleting a returning customer would be unrecoverable and entirely self-inflicted.
    /// </remarks>
    public DateOnly? DataRetainedUntil { get; set; }

    /// <summary>Commercial plan band shown on the platform screens.</summary>
    public Marketing.Common.Constants.ContractEnums.TenantPlan PlanBand { get; set; } =
        Marketing.Common.Constants.ContractEnums.TenantPlan.Starter;

    /// <summary>Instant anyone in this tenant was last active.</summary>
    public DateTimeOffset? LastActiveOn { get; set; }

    /// <summary>Messages sent in the current calendar month.</summary>
    public int MessagesThisMonth { get; set; }

    /// <summary>Users belonging to this tenant.</summary>
    public ICollection<User> Users { get; set; } = [];
}
