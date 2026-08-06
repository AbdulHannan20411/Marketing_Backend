using Marketing.Application.DTOs.Billing;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <inheritdoc cref="IPlanManagementService" />
public sealed class PlanManagementService : IPlanManagementService
{
    private readonly IRepository<SubscriptionPlan> _plans;
    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initialises a new instance.</summary>
    public PlanManagementService(
        IRepository<SubscriptionPlan> plans,
        IRepository<TenantSubscription> subscriptions,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork)
    {
        _plans = plans;
        _subscriptions = subscriptions;
        _queries = queries;
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubscriptionPlanResponse>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        // Unlike the customer-facing list, this includes inactive and archived plans - the whole
        // point of the management screen is to see and revive them.
        var plans = await _queries.ToListAsync(
            _plans.Query().OrderBy(plan => plan.SortOrder).ThenBy(plan => plan.Name),
            cancellationToken);

        return [.. plans.Select(BillingService.MapPlan)];
    }

    /// <inheritdoc />
    public async Task<SubscriptionPlanResponse> CreateAsync(
        PlanDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var plan = new SubscriptionPlan
        {
            Id = SequentialGuid.Create(),
            Name = draft.Name,
            Tagline = draft.Tagline,
            MonthlyPrice = draft.MonthlyPrice,
            YearlyPrice = draft.YearlyPrice,
            Currency = draft.Currency,
            TrialDays = draft.TrialDays,
            RenewalPeriodMonths = draft.RenewalPeriodMonths,
            DiscountPercent = draft.DiscountPercent,
            IsPromotional = draft.IsPromotional,
            IsMostPopular = draft.IsMostPopular,
            IsRecommended = draft.IsRecommended,
            Status = draft.Status,
            SupportLevel = draft.SupportLevel,
            EnabledModules = PlanModules.Collapse(draft.Modules),
            Highlights = [.. draft.Highlights ?? []],
            SortOrder = draft.SortOrder,
        };

        ApplyLimits(plan, draft.Limits);

        _plans.Add(plan);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return BillingService.MapPlan(plan);
    }

    /// <inheritdoc />
    public async Task<SubscriptionPlanResponse> UpdateAsync(
        string planId,
        PlanPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var plan = await LoadForUpdateAsync(planId, cancellationToken);

        // Patch semantics: only supplied fields are applied. The client sends just what changed,
        // and an activate arrives as { "status": "inactive" } on its own - binding a full draft
        // would blank every field it omitted.
        plan.Name = patch.Name ?? plan.Name;
        plan.Tagline = patch.Tagline ?? plan.Tagline;
        plan.MonthlyPrice = patch.MonthlyPrice ?? plan.MonthlyPrice;
        plan.YearlyPrice = patch.YearlyPrice ?? plan.YearlyPrice;
        plan.Currency = patch.Currency ?? plan.Currency;
        plan.TrialDays = patch.TrialDays ?? plan.TrialDays;
        plan.RenewalPeriodMonths = patch.RenewalPeriodMonths ?? plan.RenewalPeriodMonths;
        plan.DiscountPercent = patch.DiscountPercent ?? plan.DiscountPercent;
        plan.IsPromotional = patch.IsPromotional ?? plan.IsPromotional;
        plan.IsMostPopular = patch.IsMostPopular ?? plan.IsMostPopular;
        plan.IsRecommended = patch.IsRecommended ?? plan.IsRecommended;
        plan.Status = patch.Status ?? plan.Status;
        plan.SupportLevel = patch.SupportLevel ?? plan.SupportLevel;
        plan.SortOrder = patch.SortOrder ?? plan.SortOrder;

        if (patch.Modules is not null)
        {
            plan.EnabledModules = PlanModules.Collapse(patch.Modules);
        }

        if (patch.Highlights is not null)
        {
            plan.Highlights = [.. patch.Highlights];
        }

        if (patch.Limits is not null)
        {
            ApplyLimits(plan, patch.Limits);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return BillingService.MapPlan(plan);
    }

    /// <inheritdoc />
    public async Task<SubscriptionPlanResponse> DuplicateAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        var source = await LoadForUpdateAsync(planId, cancellationToken);

        var copy = new SubscriptionPlan
        {
            Id = SequentialGuid.Create(),
            Name = $"{source.Name} (copy)",
            Tagline = source.Tagline,
            MonthlyPrice = source.MonthlyPrice,
            YearlyPrice = source.YearlyPrice,
            Currency = source.Currency,
            TrialDays = source.TrialDays,
            RenewalPeriodMonths = source.RenewalPeriodMonths,
            DiscountPercent = source.DiscountPercent,
            IsPromotional = source.IsPromotional,

            // Badges are cleared: two plans both claiming to be the most popular is a pricing page
            // that contradicts itself, and a copy is a draft nobody has decided to promote yet.
            IsMostPopular = false,
            IsRecommended = false,

            // Inactive, so a half-edited copy cannot appear on the pricing page.
            Status = PlanStatus.Inactive,

            SupportLevel = source.SupportLevel,
            EnabledModules = [.. source.EnabledModules],
            Highlights = [.. source.Highlights],
            SortOrder = source.SortOrder,
            MaxEmployees = source.MaxEmployees,
            MaxContacts = source.MaxContacts,
            MaxCampaigns = source.MaxCampaigns,
            MaxWhatsAppAccounts = source.MaxWhatsAppAccounts,
            MaxEmailAccounts = source.MaxEmailAccounts,
            MaxSocialAccounts = source.MaxSocialAccounts,
            MaxApiCallsPerMonth = source.MaxApiCallsPerMonth,
            MaxStorageMb = source.MaxStorageMb,
            DailyMessageLimit = source.DailyMessageLimit,
            MonthlyMessageLimit = source.MonthlyMessageLimit,
        };

        _plans.Add(copy);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return BillingService.MapPlan(copy);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string planId, CancellationToken cancellationToken = default)
    {
        var plan = await LoadForUpdateAsync(planId, cancellationToken);

        // Only platform staff reach this endpoint, and their tenant filter is already bypassed, so
        // this counts subscribers across every tenant rather than just one.
        var hasSubscribers = await _queries.CountAsync(
            _subscriptions.Query().Where(subscription =>
                subscription.SubscriptionPlanId == plan.Id
                && subscription.Status != SubscriptionStatus.Cancelled),
            cancellationToken) > 0;

        if (hasSubscribers)
        {
            // Archived rather than deleted. The contract requires that existing subscribers keep
            // their terms while the plan stops being offered, and this model can honour that - so
            // it does, instead of returning the 409 the contract allows as a fallback.
            plan.Status = PlanStatus.Archived;
            plan.IsMostPopular = false;
            plan.IsRecommended = false;
        }
        else
        {
            _plans.Remove(plan);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<SubscriptionPlan> LoadForUpdateAsync(string planId, CancellationToken cancellationToken)
    {
        var id = PublicId.Parse(PublicId.Plan, planId, "plan");

        return await _plans.GetForUpdateAsync(id, cancellationToken)
               ?? throw new NotFoundException("Plan", planId);
    }

    private static void ApplyLimits(SubscriptionPlan plan, PlanLimits? limits)
    {
        if (limits is null)
        {
            return;
        }

        // Assigned wholesale, including nulls: null is a meaningful value here - it means
        // unlimited - so a null cannot be treated as "leave unchanged" the way it is elsewhere in
        // the patch.
        plan.MaxEmployees = limits.MaxEmployees;
        plan.MaxContacts = limits.MaxContacts;
        plan.MaxCampaigns = limits.MaxCampaigns;
        plan.MaxWhatsAppAccounts = limits.MaxWhatsAppAccounts;
        plan.MaxEmailAccounts = limits.MaxEmailAccounts;
        plan.MaxSocialAccounts = limits.MaxSocialAccounts;
        plan.MaxApiCallsPerMonth = limits.MaxApiCallsPerMonth;
        plan.MaxStorageMb = limits.MaxStorageMb;
        plan.DailyMessageLimit = limits.DailyMessageLimit;
        plan.MonthlyMessageLimit = limits.MonthlyMessageLimit;
    }
}
