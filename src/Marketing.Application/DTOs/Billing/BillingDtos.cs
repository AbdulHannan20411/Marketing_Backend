using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Billing;

/// <summary>Plan ceilings. Null means unlimited throughout, rendered by the client as an infinity glyph.</summary>
/// <param name="MaxEmployees">Employee seats.</param>
/// <param name="MaxContacts">Stored contacts.</param>
/// <param name="MaxCampaigns">Campaigns.</param>
/// <param name="MaxWhatsAppAccounts">Connected WhatsApp accounts.</param>
/// <param name="MaxEmailAccounts">Connected email accounts.</param>
/// <param name="MaxSocialAccounts">Connected social accounts.</param>
/// <param name="MaxApiCallsPerMonth">API calls per month.</param>
/// <param name="MaxStorageMb">Stored media, in megabytes.</param>
/// <param name="DailyMessageLimit">Messages per day.</param>
/// <param name="MonthlyMessageLimit">Messages per month.</param>
public sealed record PlanLimits(
    int? MaxEmployees,
    int? MaxContacts,
    int? MaxCampaigns,
    int? MaxWhatsAppAccounts,
    int? MaxEmailAccounts,
    int? MaxSocialAccounts,
    int? MaxApiCallsPerMonth,
    int? MaxStorageMb,
    int? DailyMessageLimit,
    int? MonthlyMessageLimit);

/// <summary>A subscription plan.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>plan_</c>.</param>
/// <param name="Name">Plan name.</param>
/// <param name="Tagline">Short marketing line.</param>
/// <param name="MonthlyPrice">Monthly price in major units.</param>
/// <param name="YearlyPrice">Yearly price in major units.</param>
/// <param name="Currency">ISO 4217 currency code.</param>
/// <param name="TrialDays">Free trial length.</param>
/// <param name="RenewalPeriodMonths">Months between renewals.</param>
/// <param name="DiscountPercent">Promotional discount percentage.</param>
/// <param name="IsPromotional">Whether currently promoted.</param>
/// <param name="IsMostPopular">Whether badged most popular.</param>
/// <param name="IsRecommended">Whether badged recommended.</param>
/// <param name="Status">Availability.</param>
/// <param name="SupportLevel">Support tier.</param>
/// <param name="Modules">All eight feature modules, each true or false.</param>
/// <param name="Limits">Plan ceilings.</param>
/// <param name="Highlights">Marketing bullets.</param>
/// <param name="SortOrder">Display order.</param>
/// <param name="UpdatedAt">Instant it last changed.</param>
public sealed record SubscriptionPlanResponse(
    string Id,
    string Name,
    string Tagline,
    decimal MonthlyPrice,
    decimal YearlyPrice,
    string Currency,
    int TrialDays,
    int RenewalPeriodMonths,
    decimal DiscountPercent,
    bool IsPromotional,
    bool IsMostPopular,
    bool IsRecommended,
    PlanStatus Status,
    SupportLevel SupportLevel,
    IReadOnlyDictionary<string, bool> Modules,
    PlanLimits Limits,
    IReadOnlyList<string> Highlights,
    int SortOrder,
    DateTimeOffset UpdatedAt);

/// <summary>A tenant's subscription.</summary>
/// <param name="PlanId">Plan identifier.</param>
/// <param name="PlanName">Plan name.</param>
/// <param name="Status">Subscription state.</param>
/// <param name="BillingCycle">Billing cadence.</param>
/// <param name="CurrentPeriodStart">Start of the current period.</param>
/// <param name="CurrentPeriodEnd">End of the current period.</param>
/// <param name="NextRenewalAt">Next renewal, when auto-renew is on.</param>
/// <param name="ExpiresAt">Instant access lapses without renewal.</param>
/// <param name="AutoRenew">Whether it renews automatically.</param>
/// <param name="TrialEndsAt">Instant the trial ends.</param>
/// <param name="SeatsPurchased">Seats purchased.</param>
/// <param name="Amount">Amount per period, in major units.</param>
/// <param name="Currency">ISO 4217 currency code.</param>
public sealed record SubscriptionResponse(
    string PlanId,
    string PlanName,
    SubscriptionStatus Status,
    BillingCycle BillingCycle,
    DateTimeOffset CurrentPeriodStart,
    DateTimeOffset CurrentPeriodEnd,
    DateTimeOffset? NextRenewalAt,
    DateTimeOffset ExpiresAt,
    bool AutoRenew,
    DateTimeOffset? TrialEndsAt,
    int SeatsPurchased,
    decimal Amount,
    string Currency);

/// <summary>Current consumption against one plan limit.</summary>
/// <param name="Key">Which metric.</param>
/// <param name="Label">Display label.</param>
/// <param name="Used">Current usage.</param>
/// <param name="Limit">Ceiling, or null for unlimited.</param>
/// <param name="Unit">Unit of measure.</param>
public sealed record UsageMetric(UsageMetricKey Key, string Label, int Used, int? Limit, string Unit);

/// <summary>
/// What a workspace is allowed to do, for any member of it.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="SubscriptionSnapshot"/>, which is gated on
/// <c>settings.subscription</c> because it carries what the workspace pays. Every member needs to
/// know which modules exist and where the limits are - the shell reads it on every page load to
/// decide what to render - and an employee should not have to hold a billing permission to learn
/// that their own workspace has WhatsApp enabled.
/// <para>
/// It carries no amount, no currency, no renewal date and no auto-renew flag. Plan prices are
/// public and appear on the pricing page; what <em>this</em> workspace was charged is not.
/// </para>
/// </remarks>
/// <param name="PlanId">Plan identifier.</param>
/// <param name="PlanName">Plan name, for display.</param>
/// <param name="Status">Subscription state, so the shell knows whether the workspace is locked.</param>
/// <param name="ExpiresAt">Instant access lapses without renewal.</param>
/// <param name="TrialEndsAt">Instant the trial ends, when there is one.</param>
/// <param name="Modules">Which feature modules the plan includes.</param>
/// <param name="Limits">The plan's ceilings.</param>
/// <param name="Usage">Current consumption against those ceilings.</param>
public sealed record EntitlementsSnapshot(
    string PlanId,
    string PlanName,
    SubscriptionStatus Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? TrialEndsAt,
    IReadOnlyDictionary<string, bool> Modules,
    PlanLimits Limits,
    IReadOnlyList<UsageMetric> Usage);

/// <summary>
/// The subscription screen in one payload.
/// <para>
/// Usage is computed live rather than from a nightly rollup: the client renders every gauge and
/// upgrade prompt from it, and a stale number either blocks a customer who has room or lets one
/// sail past a limit.
/// </para>
/// </summary>
/// <param name="Subscription">The subscription.</param>
/// <param name="Plan">The plan it is on.</param>
/// <param name="Usage">One entry per limit the plan meaningfully imposes.</param>
public sealed record SubscriptionSnapshot(
    SubscriptionResponse Subscription,
    SubscriptionPlanResponse Plan,
    IReadOnlyList<UsageMetric> Usage);

/// <summary>An issued invoice.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>inv_</c>.</param>
/// <param name="Number">Invoice number.</param>
/// <param name="PlanName">Plan billed.</param>
/// <param name="BillingCycle">Cadence charged.</param>
/// <param name="Amount">Net amount, in major units.</param>
/// <param name="Tax">Tax, in major units. The client displays amount plus tax.</param>
/// <param name="Currency">ISO 4217 currency code.</param>
/// <param name="Status">Settlement state.</param>
/// <param name="IssuedAt">Instant issued.</param>
/// <param name="DueAt">Instant payment falls due.</param>
/// <param name="PaidAt">Instant settled.</param>
/// <param name="PeriodStart">Start of the period billed.</param>
/// <param name="PeriodEnd">End of the period billed.</param>
/// <param name="DownloadUrl">Relative API path to the PDF.</param>
public sealed record InvoiceResponse(
    string Id,
    string Number,
    string PlanName,
    BillingCycle BillingCycle,
    decimal Amount,
    decimal Tax,
    string Currency,
    InvoiceStatus Status,
    DateTimeOffset IssuedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string DownloadUrl);

/// <summary>A payment attempt.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>pay_</c>.</param>
/// <param name="InvoiceNumber">Invoice paid.</param>
/// <param name="Amount">Amount, in major units.</param>
/// <param name="Currency">ISO 4217 currency code.</param>
/// <param name="Status">Outcome.</param>
/// <param name="Method">How it was paid.</param>
/// <param name="CardBrand">Card brand, when paid by card.</param>
/// <param name="CardLast4">Last four digits only; never a full card number.</param>
/// <param name="ProcessedAt">Instant processed.</param>
/// <param name="FailureReason">Why it failed, when it did.</param>
public sealed record PaymentResponse(
    string Id,
    string InvoiceNumber,
    decimal Amount,
    string Currency,
    PaymentStatus Status,
    PaymentMethodKind Method,
    string? CardBrand,
    string? CardLast4,
    DateTimeOffset ProcessedAt,
    string? FailureReason);

/// <summary>A completed renewal.</summary>
/// <param name="Id">Opaque identifier.</param>
/// <param name="PlanName">Plan renewed.</param>
/// <param name="BillingCycle">Cadence.</param>
/// <param name="Amount">Amount charged.</param>
/// <param name="Currency">ISO 4217 currency code.</param>
/// <param name="RenewedAt">Instant the renewal completed.</param>
/// <param name="PeriodEnd">End of the period it bought.</param>
/// <param name="Automatic">Whether it renewed automatically.</param>
public sealed record RenewalRecordResponse(
    string Id,
    string PlanName,
    BillingCycle BillingCycle,
    decimal Amount,
    string Currency,
    DateTimeOffset RenewedAt,
    DateTimeOffset PeriodEnd,
    bool Automatic);

/// <summary>The billing history screen in one payload.</summary>
/// <param name="Invoices">Invoices, newest first.</param>
/// <param name="Payments">Payments, newest first.</param>
/// <param name="Renewals">Renewals, newest first.</param>
public sealed record BillingHistory(
    IReadOnlyList<InvoiceResponse> Invoices,
    IReadOnlyList<PaymentResponse> Payments,
    IReadOnlyList<RenewalRecordResponse> Renewals);
