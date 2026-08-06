using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// A subscription plan offered by the platform.
/// <para>
/// Platform-level data, so not tenant-scoped: plans are defined once by a Super Admin and bought
/// by every tenant.
/// </para>
/// </summary>
public sealed class SubscriptionPlan : BaseEntity
{
    /// <summary>Plan name.</summary>
    public required string Name { get; set; }

    /// <summary>Short marketing line.</summary>
    public string Tagline { get; set; } = string.Empty;

    /// <summary>Price per month in major units.</summary>
    public decimal MonthlyPrice { get; set; }

    /// <summary>Price per year in major units.</summary>
    public decimal YearlyPrice { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Length of the free trial, in days.</summary>
    public int TrialDays { get; set; }

    /// <summary>Months between renewals.</summary>
    public int RenewalPeriodMonths { get; set; } = 1;

    /// <summary>Promotional discount, as a percentage.</summary>
    public decimal DiscountPercent { get; set; }

    /// <summary>Whether the plan is currently promoted.</summary>
    public bool IsPromotional { get; set; }

    /// <summary>Whether to badge the plan as most popular.</summary>
    public bool IsMostPopular { get; set; }

    /// <summary>Whether to badge the plan as recommended.</summary>
    public bool IsRecommended { get; set; }

    /// <summary>Availability.</summary>
    public PlanStatus Status { get; set; } = PlanStatus.Inactive;

    /// <summary>Support tier.</summary>
    public SupportLevel SupportLevel { get; set; } = SupportLevel.Community;

    /// <summary>Feature modules switched on, stored as a text array of module names.</summary>
    public List<string> EnabledModules { get; set; } = [];

    /// <summary>Marketing bullets.</summary>
    public List<string> Highlights { get; set; } = [];

    /// <summary>Display order on the pricing page.</summary>
    public int SortOrder { get; set; }

    // Limits. Null means unlimited throughout, which the client renders as an infinity glyph.

    /// <summary>Maximum employee seats, or null for unlimited.</summary>
    public int? MaxEmployees { get; set; }

    /// <summary>Maximum stored contacts, or null for unlimited.</summary>
    public int? MaxContacts { get; set; }

    /// <summary>Maximum campaigns, or null for unlimited.</summary>
    public int? MaxCampaigns { get; set; }

    /// <summary>Maximum connected WhatsApp accounts, or null for unlimited.</summary>
    public int? MaxWhatsAppAccounts { get; set; }

    /// <summary>Maximum connected email accounts, or null for unlimited.</summary>
    public int? MaxEmailAccounts { get; set; }

    /// <summary>Maximum connected social accounts, or null for unlimited.</summary>
    public int? MaxSocialAccounts { get; set; }

    /// <summary>Maximum API calls per month, or null for unlimited.</summary>
    public int? MaxApiCallsPerMonth { get; set; }

    /// <summary>Maximum stored media in megabytes, or null for unlimited.</summary>
    public int? MaxStorageMb { get; set; }

    /// <summary>Maximum messages per day, or null for unlimited.</summary>
    public int? DailyMessageLimit { get; set; }

    /// <summary>Maximum messages per month, or null for unlimited.</summary>
    public int? MonthlyMessageLimit { get; set; }
}

/// <summary>A tenant's subscription to a plan.</summary>
public sealed class TenantSubscription : BaseEntity, IRequiresTenant
{
    /// <summary>Plan subscribed to.</summary>
    public Guid SubscriptionPlanId { get; set; }

    /// <summary>Subscription state.</summary>
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trial;

    /// <summary>Billing cadence.</summary>
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;

    /// <summary>Start of the current billing period.</summary>
    public DateTimeOffset CurrentPeriodStart { get; set; }

    /// <summary>End of the current billing period.</summary>
    public DateTimeOffset CurrentPeriodEnd { get; set; }

    /// <summary>Instant of the next renewal, when auto-renew is on.</summary>
    public DateTimeOffset? NextRenewalAt { get; set; }

    /// <summary>Instant access lapses without renewal.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Whether the subscription renews automatically.</summary>
    public bool AutoRenew { get; set; } = true;

    /// <summary>Instant the trial ends.</summary>
    public DateTimeOffset? TrialEndsAt { get; set; }

    /// <summary>Seats purchased.</summary>
    public int SeatsPurchased { get; set; } = 1;

    /// <summary>Amount charged per period, in major units.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Plan navigation.</summary>
    public SubscriptionPlan SubscriptionPlan { get; set; } = null!;
}

/// <summary>An issued invoice.</summary>
public sealed class Invoice : BaseEntity, IRequiresTenant
{
    /// <summary>Human-readable invoice number.</summary>
    public required string Number { get; set; }

    /// <summary>Plan name at the time of issue.</summary>
    public string PlanName { get; set; } = string.Empty;

    /// <summary>Billing cadence charged.</summary>
    public BillingCycle BillingCycle { get; set; }

    /// <summary>Net amount, in major units.</summary>
    public decimal Amount { get; set; }

    /// <summary>Tax, in major units. The client displays amount plus tax.</summary>
    public decimal Tax { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Settlement state.</summary>
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Due;

    /// <summary>Instant the invoice was issued.</summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>Instant payment falls due.</summary>
    public DateTimeOffset DueAt { get; set; }

    /// <summary>Instant it was settled.</summary>
    public DateTimeOffset? PaidAt { get; set; }

    /// <summary>Start of the period billed.</summary>
    public DateTimeOffset PeriodStart { get; set; }

    /// <summary>End of the period billed.</summary>
    public DateTimeOffset PeriodEnd { get; set; }
}

/// <summary>A payment attempt against an invoice.</summary>
public sealed class Payment : BaseEntity, IRequiresTenant
{
    /// <summary>Invoice paid.</summary>
    public Guid? InvoiceId { get; set; }

    /// <summary>Invoice number, denormalised for the payments list.</summary>
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>Amount, in major units.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Outcome.</summary>
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    /// <summary>How it was paid.</summary>
    public PaymentMethodKind Method { get; set; } = PaymentMethodKind.Card;

    /// <summary>Card brand, when paid by card.</summary>
    public string? CardBrand { get; set; }

    /// <summary>
    /// Last four digits only. A full card number must never reach this database - storing one
    /// would drag the whole platform into PCI scope.
    /// </summary>
    public string? CardLast4 { get; set; }

    /// <summary>Instant the attempt was processed.</summary>
    public DateTimeOffset ProcessedAt { get; set; }

    /// <summary>Why it failed, when it did.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Invoice navigation.</summary>
    public Invoice? Invoice { get; set; }
}

/// <summary>A completed renewal.</summary>
public sealed class RenewalRecord : BaseEntity, IRequiresTenant
{
    /// <summary>Plan renewed.</summary>
    public string PlanName { get; set; } = string.Empty;

    /// <summary>Billing cadence.</summary>
    public BillingCycle BillingCycle { get; set; }

    /// <summary>Amount charged, in major units.</summary>
    public decimal Amount { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Instant the renewal completed.</summary>
    public DateTimeOffset RenewedAt { get; set; }

    /// <summary>End of the period the renewal bought.</summary>
    public DateTimeOffset PeriodEnd { get; set; }

    /// <summary>Whether it renewed automatically or was paid manually.</summary>
    public bool Automatic { get; set; }
}
