using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Billing;

/// <summary>
/// A plan as submitted for creation. <c>SubscriptionPlan</c> without <c>id</c> and
/// <c>updatedAt</c>, both of which are server-owned.
/// </summary>
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
/// <param name="Modules">Feature modules, keyed by module name.</param>
/// <param name="Limits">Plan ceilings; null means unlimited.</param>
/// <param name="Highlights">Marketing bullets.</param>
/// <param name="SortOrder">Display order.</param>
public sealed record PlanDraft(
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
    IReadOnlyDictionary<string, bool>? Modules,
    PlanLimits? Limits,
    IReadOnlyList<string>? Highlights,
    int SortOrder);

/// <summary>
/// A partial plan update.
/// <para>
/// Every field is nullable and only supplied fields are applied, because the client sends just
/// what changed - an activate or archive arrives as <c>{ "status": "inactive" }</c> alone. Binding
/// this to a full draft would blank every field the caller omitted.
/// </para>
/// </summary>
public sealed record PlanPatch(
    string? Name = null,
    string? Tagline = null,
    decimal? MonthlyPrice = null,
    decimal? YearlyPrice = null,
    string? Currency = null,
    int? TrialDays = null,
    int? RenewalPeriodMonths = null,
    decimal? DiscountPercent = null,
    bool? IsPromotional = null,
    bool? IsMostPopular = null,
    bool? IsRecommended = null,
    PlanStatus? Status = null,
    SupportLevel? SupportLevel = null,
    IReadOnlyDictionary<string, bool>? Modules = null,
    PlanLimits? Limits = null,
    IReadOnlyList<string>? Highlights = null,
    int? SortOrder = null);

/// <summary>Request to move to another plan.</summary>
/// <param name="PlanId">Plan to move to.</param>
/// <param name="BillingCycle">Cadence to bill at.</param>
public sealed record ChangePlanRequest(string PlanId, BillingCycle BillingCycle);

/// <summary>Request to cancel a subscription.</summary>
/// <param name="Reason">Optional free-text reason, recorded for churn analysis.</param>
public sealed record CancelSubscriptionRequest(string? Reason = null);

/// <summary>Request to switch automatic renewal on or off.</summary>
/// <param name="Enabled">Whether the subscription should renew automatically.</param>
public sealed record AutoRenewRequest(bool Enabled);
