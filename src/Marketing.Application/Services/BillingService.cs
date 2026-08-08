using Marketing.Application.DTOs.Billing;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <inheritdoc cref="IBillingService" />
public sealed class BillingService : IBillingService
{
    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IRepository<SubscriptionPlan> _plans;
    private readonly IRepository<Invoice> _invoices;
    private readonly IRepository<Payment> _payments;
    private readonly IRepository<RenewalRecord> _renewals;
    private readonly IRepository<Contact> _contacts;
    private readonly IRepository<Campaign> _campaigns;
    private readonly IRepository<MessageDailyStat> _stats;
    private readonly IRepository<WhatsAppConnection> _connections;
    private readonly IRepository<User> _users;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPaymentGateway _gateway;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public BillingService(
        IRepository<TenantSubscription> subscriptions,
        IRepository<SubscriptionPlan> plans,
        IRepository<Invoice> invoices,
        IRepository<Payment> payments,
        IRepository<RenewalRecord> renewals,
        IRepository<Contact> contacts,
        IRepository<Campaign> campaigns,
        IRepository<MessageDailyStat> stats,
        IRepository<WhatsAppConnection> connections,
        IRepository<User> users,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IPaymentGateway gateway,
        IDateTimeProvider clock)
    {
        _subscriptions = subscriptions;
        _plans = plans;
        _invoices = invoices;
        _payments = payments;
        _renewals = renewals;
        _contacts = contacts;
        _campaigns = campaigns;
        _stats = stats;
        _connections = connections;
        _users = users;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _gateway = gateway;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<SubscriptionSnapshot> GetSubscriptionAsync(CancellationToken cancellationToken = default)
    {
        var (subscription, plan) = await LoadSubscriptionAsync(cancellationToken);

        return new SubscriptionSnapshot(
            MapSubscription(subscription, plan),
            MapPlan(plan),
            await BuildUsageAsync(plan, subscription, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubscriptionPlanResponse>> GetPurchasablePlansAsync(
        CancellationToken cancellationToken = default)
    {
        // Active only. Inactive plans stay honoured for existing subscribers but must not appear
        // on the pricing page, and archived ones are gone for good.
        var plans = await _queries.ToListAsync(
            _plans.Query()
                .Where(plan => plan.Status == PlanStatus.Active)
                .OrderBy(plan => plan.SortOrder),
            cancellationToken);

        return [.. plans.Select(MapPlan)];
    }

    /// <inheritdoc />
    public async Task<BillingHistory> GetBillingHistoryAsync(CancellationToken cancellationToken = default)
    {
        var invoices = await _queries.ToListAsync(
            _invoices.Query().OrderByDescending(invoice => invoice.IssuedAt),
            cancellationToken);

        var payments = await _queries.ToListAsync(
            _payments.Query().OrderByDescending(payment => payment.ProcessedAt),
            cancellationToken);

        var renewals = await _queries.ToListAsync(
            _renewals.Query().OrderByDescending(renewal => renewal.RenewedAt),
            cancellationToken);

        return new BillingHistory(
            [.. invoices.Select(MapInvoice)],
            [.. payments.Select(payment => new PaymentResponse(
                PublicId.From(PublicId.Payment, payment.Id),
                payment.InvoiceNumber,
                payment.Amount,
                payment.Currency,
                payment.Status,
                payment.Method,
                payment.CardBrand,
                payment.CardLast4,
                payment.ProcessedAt,
                payment.FailureReason))],
            [.. renewals.Select(renewal => new RenewalRecordResponse(
                PublicId.From(PublicId.Renewal, renewal.Id),
                renewal.PlanName,
                renewal.BillingCycle,
                renewal.Amount,
                renewal.Currency,
                renewal.RenewedAt,
                renewal.PeriodEnd,
                renewal.Automatic))]);
    }

    /// <inheritdoc />
    public async Task<SubscriptionSnapshot> ChangePlanAsync(
        ChangePlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var planId = PublicId.Parse(PublicId.Plan, request.PlanId, "plan");
        var (subscription, currentPlan) = await LoadSubscriptionAsync(cancellationToken, tracked: true);

        var target = await _plans.GetForUpdateAsync(planId, cancellationToken)
                     ?? throw new NotFoundException("Plan", request.PlanId);

        if (target.Status != PlanStatus.Active)
        {
            throw new BusinessRuleException("plan_not_available", $"The {target.Name} plan is not available.");
        }

        var usage = await BuildUsageAsync(target, subscription, cancellationToken);

        // A downgrade below current usage is refused rather than accepted and enforced later,
        // which would leave the tenant instantly over a limit with no way back except deleting
        // data. The message names the offending metric so the customer knows what to shed.
        var breach = usage.FirstOrDefault(metric => metric.Limit is { } limit && metric.Used > limit);

        if (breach is not null)
        {
            throw new BusinessRuleException(
                "downgrade_blocked",
                $"The {target.Name} plan allows {breach.Limit} {breach.Label.ToLowerInvariant()}, "
                + $"and you are using {breach.Used}. Reduce them before switching.");
        }

        var isUpgrade = PriceFor(target, request.BillingCycle) >= PriceFor(currentPlan, subscription.BillingCycle);

        subscription.SubscriptionPlanId = target.Id;
        subscription.BillingCycle = request.BillingCycle;
        subscription.Amount = PriceFor(target, request.BillingCycle);
        subscription.Currency = target.Currency;

        if (isUpgrade)
        {
            // Upgrades apply now: the customer has paid more and expects the capacity immediately.
            subscription.Status = SubscriptionStatus.Active;
            subscription.CurrentPeriodStart = _clock.UtcNow;
            subscription.CurrentPeriodEnd = _clock.UtcNow.AddMonths(target.RenewalPeriodMonths);
            subscription.ExpiresAt = subscription.CurrentPeriodEnd;
            subscription.NextRenewalAt = subscription.AutoRenew ? subscription.CurrentPeriodEnd : null;
        }

        // A downgrade keeps the current period as-is and takes effect at renewal, because the
        // customer has already paid for the capacity they currently hold.

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new SubscriptionSnapshot(
            MapSubscription(subscription, target),
            MapPlan(target),
            await BuildUsageAsync(target, subscription, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<SubscriptionSnapshot> CancelAsync(
        CancelSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        var (subscription, plan) = await LoadSubscriptionAsync(cancellationToken, tracked: true);

        // Cancelled, not terminated: access runs to the end of the paid period. Cutting it off
        // immediately would be taking money for time the customer cannot use.
        subscription.Status = SubscriptionStatus.Cancelled;
        subscription.AutoRenew = false;
        subscription.NextRenewalAt = null;
        subscription.ExpiresAt = subscription.CurrentPeriodEnd;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new SubscriptionSnapshot(
            MapSubscription(subscription, plan),
            MapPlan(plan),
            await BuildUsageAsync(plan, subscription, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<SubscriptionSnapshot> SetAutoRenewAsync(
        AutoRenewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (subscription, plan) = await LoadSubscriptionAsync(cancellationToken, tracked: true);

        subscription.AutoRenew = request.Enabled;
        subscription.NextRenewalAt = request.Enabled ? subscription.CurrentPeriodEnd : null;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new SubscriptionSnapshot(
            MapSubscription(subscription, plan),
            MapPlan(plan),
            await BuildUsageAsync(plan, subscription, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<InvoiceResponse> PayInvoiceAsync(
        string invoiceId,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Invoice, invoiceId, "invoice");

        var invoice = await _invoices.GetForUpdateAsync(id, cancellationToken)
                      ?? throw new NotFoundException("Invoice", invoiceId);

        // Already settled, so return it unchanged rather than charging again. This is the second
        // line of defence behind the idempotency key, for the case where the key was not supplied.
        if (invoice.Status is InvoiceStatus.Paid or InvoiceStatus.Void or InvoiceStatus.Refunded)
        {
            return MapInvoice(invoice);
        }

        var gross = invoice.Amount + invoice.Tax;

        var result = await _gateway.ChargeAsync(
            new PaymentAttempt(invoice.Number, gross, invoice.Currency, $"Invoice {invoice.Number}"),
            idempotencyKey,
            cancellationToken);

        // The attempt is recorded whether or not it succeeded. A failed charge that leaves no
        // trace makes "why was I not billed" unanswerable.
        _payments.Add(new Payment
        {
            TenantId = invoice.TenantId,
            InvoiceId = invoice.Id,
            InvoiceNumber = invoice.Number,
            Amount = gross,
            Currency = invoice.Currency,
            Status = result.Succeeded ? PaymentStatus.Succeeded : PaymentStatus.Failed,
            Method = PaymentMethodKind.BankTransfer,
            CardBrand = result.CardBrand,
            CardLast4 = result.CardLast4,
            ProcessedAt = _clock.UtcNow,
            FailureReason = result.FailureReason,
        });

        if (result.Succeeded)
        {
            invoice.Status = InvoiceStatus.Paid;
            invoice.PaidAt = _clock.UtcNow;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (!result.Succeeded)
        {
            throw new BusinessRuleException(
                "payment_failed",
                result.FailureReason ?? "The payment could not be completed.");
        }

        return MapInvoice(invoice);
    }

    /// <summary>Loads the caller's subscription and its plan, or fails with a clear message.</summary>
    private async Task<(TenantSubscription Subscription, SubscriptionPlan Plan)> LoadSubscriptionAsync(
        CancellationToken cancellationToken,
        bool tracked = false)
    {
        var subscription = await _queries.FirstOrDefaultAsync(
            _subscriptions.Query(asNoTracking: !tracked),
            cancellationToken)
            ?? throw new NotFoundException("This organisation has no subscription.");

        var plan = await _queries.FirstOrDefaultAsync(
            _plans.Query(asNoTracking: !tracked)
                .Where(plan => plan.Id == subscription.SubscriptionPlanId),
            cancellationToken)
            ?? throw new NotFoundException("Plan", subscription.SubscriptionPlanId);

        return (subscription, plan);
    }

    /// <summary>
    /// Counts current usage against every metric a plan can limit.
    /// <para>
    /// All ten are returned, with a null limit where the plan is unlimited, so the client can
    /// render a complete panel rather than a list whose length varies by plan.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<UsageMetric>> BuildUsageAsync(
        SubscriptionPlan plan,
        TenantSubscription subscription,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        var employees = await _queries.CountAsync(_users.Query(), cancellationToken);
        var contacts = await _queries.CountAsync(_contacts.Query(), cancellationToken);
        var campaigns = await _queries.CountAsync(_campaigns.Query(), cancellationToken);
        var whatsAppAccounts = await _queries.CountAsync(_connections.Query(), cancellationToken);

        var messagesToday = await _queries.SumAsync(
            _stats.Query().Where(stat => stat.Date == today).Select(stat => stat.Sent),
            cancellationToken);

        var messagesThisMonth = await _queries.SumAsync(
            _stats.Query().Where(stat => stat.Date >= monthStart).Select(stat => stat.Sent),
            cancellationToken);

        return
        [
            new(UsageMetricKey.Employees, "Employees", employees, plan.MaxEmployees, "seats"),
            new(UsageMetricKey.Contacts, "Contacts", contacts, plan.MaxContacts, "contacts"),
            new(UsageMetricKey.Campaigns, "Campaigns", campaigns, plan.MaxCampaigns, "campaigns"),
            new(UsageMetricKey.WhatsAppAccounts, "WhatsApp accounts", whatsAppAccounts,
                plan.MaxWhatsAppAccounts, "accounts"),

            // Not yet tracked - the email and social modules do not exist. Reported as zero rather
            // than omitted, so the panel shape stays constant and the limit is still visible.
            new(UsageMetricKey.EmailAccounts, "Email accounts", 0, plan.MaxEmailAccounts, "accounts"),
            new(UsageMetricKey.SocialAccounts, "Social accounts", 0, plan.MaxSocialAccounts, "accounts"),
            new(UsageMetricKey.ApiCalls, "API calls", 0, plan.MaxApiCallsPerMonth, "calls"),
            new(UsageMetricKey.Storage, "Storage", 0, plan.MaxStorageMb, "MB"),

            new(UsageMetricKey.MessagesDaily, "Messages today", messagesToday, plan.DailyMessageLimit, "messages"),
            new(UsageMetricKey.MessagesMonthly, "Messages this month", messagesThisMonth,
                plan.MonthlyMessageLimit, "messages"),
        ];
    }

    private static decimal PriceFor(SubscriptionPlan plan, BillingCycle cycle) =>
        cycle == BillingCycle.Yearly ? plan.YearlyPrice : plan.MonthlyPrice;

    private static SubscriptionResponse MapSubscription(TenantSubscription subscription, SubscriptionPlan plan) =>
        new(
            PublicId.From(PublicId.Plan, plan.Id),
            plan.Name,
            subscription.Status,
            subscription.BillingCycle,
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd,
            subscription.NextRenewalAt,
            subscription.ExpiresAt,
            subscription.AutoRenew,
            subscription.TrialEndsAt,
            subscription.SeatsPurchased,
            subscription.Amount,
            subscription.Currency);

    /// <summary>Shared plan projection, used by both the customer and the platform screens.</summary>
    internal static SubscriptionPlanResponse MapPlan(SubscriptionPlan plan) =>
        new(
            PublicId.From(PublicId.Plan, plan.Id),
            plan.Name,
            plan.Tagline,
            plan.MonthlyPrice,
            plan.YearlyPrice,
            plan.Currency,
            plan.TrialDays,
            plan.RenewalPeriodMonths,
            plan.DiscountPercent,
            plan.IsPromotional,
            plan.IsMostPopular,
            plan.IsRecommended,
            plan.Status,
            plan.SupportLevel,
            PlanModules.Expand(plan.EnabledModules),
            new PlanLimits(
                plan.MaxEmployees,
                plan.MaxContacts,
                plan.MaxCampaigns,
                plan.MaxWhatsAppAccounts,
                plan.MaxEmailAccounts,
                plan.MaxSocialAccounts,
                plan.MaxApiCallsPerMonth,
                plan.MaxStorageMb,
                plan.DailyMessageLimit,
                plan.MonthlyMessageLimit),
            plan.Highlights,
            plan.SortOrder,
            plan.ModifiedOn ?? plan.CreatedOn);

    private static InvoiceResponse MapInvoice(Invoice invoice) =>
        new(
            PublicId.From(PublicId.Invoice, invoice.Id),
            invoice.Number,
            invoice.PlanName,
            invoice.BillingCycle,
            invoice.Amount,
            invoice.Tax,
            invoice.Currency,
            invoice.Status,
            invoice.IssuedAt,
            invoice.DueAt,
            invoice.PaidAt,
            invoice.PeriodStart,
            invoice.PeriodEnd,
            $"/api/v1/billing/invoices/{PublicId.From(PublicId.Invoice, invoice.Id)}/pdf");
}
