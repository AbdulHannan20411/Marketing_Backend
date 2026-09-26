using Marketing.Application.DTOs.Billing;
using Marketing.Application.Services.Billing;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <inheritdoc cref="IBillingService" />
public sealed class BillingService : IBillingService
{
    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IRepository<SubscriptionPlan> _plans;
    private readonly IRepository<Invoice> _invoices;
    private readonly IRepository<Tenant> _tenants;
    private readonly IRepository<BillingProfile> _profiles;
    private readonly Services.Billing.IInvoiceRenderer _renderer;
    private readonly Configurations.EmailOptions _emailOptions;
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
    private readonly ITenantContext _tenantContext;
    private readonly IMemoryCache _memory;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public BillingService(
        IRepository<TenantSubscription> subscriptions,
        IRepository<SubscriptionPlan> plans,
        IRepository<Invoice> invoices,
        IRepository<Tenant> tenants,
        IRepository<BillingProfile> profiles,
        Services.Billing.IInvoiceRenderer renderer,
        Microsoft.Extensions.Options.IOptions<Configurations.EmailOptions> emailOptions,
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
        ITenantContext tenantContext,
        IDateTimeProvider clock,
        IMemoryCache memory)
    {
        _subscriptions = subscriptions;
        _plans = plans;
        _invoices = invoices;
        _tenants = tenants;
        _profiles = profiles;
        _renderer = renderer;
        _emailOptions = emailOptions.Value;
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
        _tenantContext = tenantContext;
        _clock = clock;
        _memory = memory;
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
    public async Task<EntitlementsSnapshot> GetEntitlementsAsync(CancellationToken cancellationToken = default)
    {
        var key = EntitlementsKey(_tenantContext.TenantId);

        // Every screen reads this, several of them on open, and each read is eight queries - the
        // subscription, the plan and six counts. The numbers are gauges, not decisions: a contact
        // added ten seconds ago showing next time is fine, while a plan change clears this at once.
        if (key is not null && _memory.TryGetValue<EntitlementsSnapshot>(key, out var remembered) && remembered is not null)
        {
            return remembered;
        }

        var (subscription, plan) = await LoadSubscriptionAsync(cancellationToken);
        var mapped = MapPlan(plan);

        // Built from the plan and the subscription's state, never from MapSubscription: that
        // carries Amount, Currency, BillingCycle, NextRenewalAt and AutoRenew, and the whole point
        // of this endpoint is that it can be read by somebody with no billing permission.
        var snapshot = new EntitlementsSnapshot(
            mapped.Id,
            mapped.Name,
            subscription.Status,
            subscription.ExpiresAt,
            subscription.TrialEndsAt,
            mapped.Modules,
            mapped.Limits,
            await BuildUsageAsync(plan, subscription, cancellationToken));

        if (key is not null)
        {
            _memory.Set(key, snapshot, EntitlementsLifetime);
        }

        return snapshot;
    }

    /// <summary>How long a workspace's entitlements are reused before they are read again.</summary>
    private static readonly TimeSpan EntitlementsLifetime = TimeSpan.FromSeconds(10);

    /// <summary>Where a workspace's entitlements are remembered, or null when there is no workspace.</summary>
    private static string? EntitlementsKey(long? tenantId) =>
        tenantId is { } id ? $"entitlements:{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : null;

    /// <summary>
    /// Drops the remembered entitlements, so the next read shows what just changed.
    /// </summary>
    /// <remarks>
    /// Called wherever the plan or the subscription's state moves. A limit the customer just paid
    /// for has to apply now, not in ten seconds.
    /// </remarks>
    private void ForgetEntitlements()
    {
        _memory.Remove(EntitlementsKey(_tenantContext.TenantId) ?? string.Empty);

        // The write gate keeps its own one-line answer, for the same reason and on a shorter
        // fuse. Dropped here as well, so a customer who has just paid can use what they paid for
        // on the next request rather than fifteen seconds later.
        if (_tenantContext.TenantId is { } tenantId)
        {
            _memory.Remove(Billing.SubscriptionGate.KeyFor(tenantId));
        }
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

        var target = await _plans.GetForUpdateAsync(planId, cancellationToken)
                     ?? throw new NotFoundException("Plan", request.PlanId);

        if (target.Status != PlanStatus.Active)
        {
            throw new BusinessRuleException("plan_not_available", $"The {target.Name} plan is not available.");
        }

        // A tenant that has never subscribed reaches this endpoint from the "choose a plan" state,
        // which is what GET /subscription's 404 tells the client to render. Without this branch
        // that state is a dead end: the only way to buy a first plan would be to already have one.
        var existing = await _queries.FirstOrDefaultAsync(
            _subscriptions.Query(asNoTracking: false),
            cancellationToken);

        if (existing is null)
        {
            return await StartSubscriptionAsync(target, request.BillingCycle, cancellationToken);
        }

        var (subscription, currentPlan) = (existing, await LoadPlanAsync(existing, cancellationToken));

        var usage = await BuildUsageAsync(target, subscription, cancellationToken);

        // A downgrade below current usage is refused rather than accepted and enforced later,
        // which would leave the tenant instantly over a limit with no way back except deleting
        // data. The message names the offending metric so the customer knows what to shed.
        var breach = usage.FirstOrDefault(metric => metric.Limit is { } limit && metric.Used > limit);

        if (breach is not null)
        {
            // Says how many to remove, not just that there are too many - the customer's next step.
            throw new BusinessRuleException(
                "downgrade_blocked",
                $"The {target.Name} plan allows {breach.Limit} {breach.Unit}, and you are using {breach.Used}. "
                + $"Remove {breach.Used - breach.Limit} {breach.Unit} ({breach.Label}) before switching.");
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
            
            // The expiry countdown starts again. Left set, it would hold the last period's
            // threshold and suppress every reminder for this one - a subscription that warned
            // nobody because it had warned somebody once before.
            subscription.LastExpiryReminderDay = null;
            subscription.NextRenewalAt = subscription.AutoRenew ? subscription.CurrentPeriodEnd : null;
        }

        // A downgrade keeps the current period as-is and takes effect at renewal, because the
        // customer has already paid for the capacity they currently hold.

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        ForgetEntitlements();

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

        ArgumentNullException.ThrowIfNull(request);

        subscription.AutoRenew = false;
        subscription.NextRenewalAt = null;

        if (request.Immediate)
        {
            subscription.Status = SubscriptionStatus.Cancelled;
            subscription.ExpiresAt = _clock.UtcNow;
        }
        else
        {
            // Still active, not yet cancelled. Access runs to the end of the paid period, and the
            // status only changes when it lapses - flipping it now would show the customer a
            // cancelled workspace they are still paying for and can still use.
            subscription.Status = SubscriptionStatus.Active;
            subscription.ExpiresAt = subscription.CurrentPeriodEnd;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        ForgetEntitlements();

        return new SubscriptionSnapshot(
            MapSubscription(subscription, plan),
            MapPlan(plan),
            await BuildUsageAsync(plan, subscription, cancellationToken));
    }

    /// <summary>
    /// Creates a tenant's first subscription.
    /// </summary>
    /// <remarks>
    /// A trial when the plan offers one, otherwise active immediately. No proration and no charge:
    /// no payment provider is configured, so this records the commercial state and nothing more.
    /// </remarks>
    /// <param name="plan">Plan being bought.</param>
    /// <param name="cycle">Billing cadence.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<SubscriptionSnapshot> StartSubscriptionAsync(
        SubscriptionPlan plan,
        BillingCycle cycle,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var periodEnd = cycle == BillingCycle.Yearly
            ? now.AddYears(1)
            : now.AddMonths(Math.Max(1, plan.RenewalPeriodMonths));

        var isTrial = plan.TrialDays > 0;

        // A trial ends when the trial ends, not when the paid period would have. Setting expiry to
        // the period end would silently give away the whole first period for free.
        var expiresAt = isTrial ? now.AddDays(plan.TrialDays) : periodEnd;

        var subscription = new TenantSubscription
        {
            TenantId = _tenantContext.RequireTenantId(),
            SubscriptionPlanId = plan.Id,
            Status = isTrial ? SubscriptionStatus.Trial : SubscriptionStatus.Active,
            BillingCycle = cycle,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = periodEnd,
            ExpiresAt = expiresAt,
            NextRenewalAt = periodEnd,
            AutoRenew = true,
            TrialEndsAt = isTrial ? expiresAt : null,
            SeatsPurchased = plan.MaxEmployees ?? 0,
            Amount = PriceFor(plan, cycle),
            Currency = plan.Currency,
        };

        _subscriptions.Add(subscription);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        ForgetEntitlements();

        return new SubscriptionSnapshot(
            MapSubscription(subscription, plan),
            MapPlan(plan),
            await BuildUsageAsync(plan, subscription, cancellationToken));
    }

    /// <summary>Loads the plan a subscription points at.</summary>
    private async Task<SubscriptionPlan> LoadPlanAsync(
        TenantSubscription subscription,
        CancellationToken cancellationToken) =>
        await _queries.FirstOrDefaultAsync(
            _plans.Query(asNoTracking: false).Where(plan => plan.Id == subscription.SubscriptionPlanId),
            cancellationToken)
        ?? throw new NotFoundException("Plan", subscription.SubscriptionPlanId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public async Task<SubscriptionSnapshot> ResumeAsync(CancellationToken cancellationToken = default)
    {
        var (subscription, plan) = await LoadSubscriptionAsync(cancellationToken, tracked: true);

        // Only a cancellation that has not yet taken effect can be reversed. Once the period has
        // lapsed there is nothing to resume - that is a new purchase, and pretending otherwise
        // would give away time nobody paid for.
        if (subscription.ExpiresAt <= _clock.UtcNow || subscription.Status == SubscriptionStatus.Expired)
        {
            throw new BusinessRuleException(
                "subscription_lapsed",
                "This subscription has already ended. Choose a plan to start again.");
        }

        if (subscription.AutoRenew && subscription.Status == SubscriptionStatus.Active)
        {
            throw new BusinessRuleException(
                "not_cancelled",
                "This subscription is not scheduled to end.");
        }

        subscription.Status = SubscriptionStatus.Active;
        subscription.AutoRenew = true;
        subscription.NextRenewalAt = subscription.CurrentPeriodEnd;
        subscription.ExpiresAt = subscription.CurrentPeriodEnd;

        // Reinstated, so any countdown recorded while it was winding down no longer applies.
        subscription.LastExpiryReminderDay = null;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        ForgetEntitlements();

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

        ForgetEntitlements();

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
    /// <summary>
    /// The workspace's subscription and its plan.
    /// </summary>
    /// <remarks>
    /// A workspace that has never bought a plan is a <b>404 carrying <c>no_subscription</c></b>,
    /// and deliberately not a 200 with an empty snapshot. The client distinguishes that one status
    /// from every other failure: a 404 is an answer - there is no plan, lock and grant nothing - 
    /// while a 500, a timeout or a dropped connection fails open, because locking a paying
    /// customer out over a lost request is worse than the hole this closes.
    /// <para>
    /// The code is there so that branch can key on the reason rather than on the status. A bare
    /// 404 on this route would also be produced by a misspelled path or a version bump, and those
    /// are "unknown", not "no plan".
    /// </para>
    /// <para>
    /// A subscription pointing at a plan that no longer exists is a different 404, with the
    /// ordinary code. That is a broken row rather than an absent one, and reporting it as "no
    /// plan" would let a paying customer be locked out by a data fault.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="tracked">Whether the rows are tracked for update.</param>
    private async Task<(TenantSubscription Subscription, SubscriptionPlan Plan)> LoadSubscriptionAsync(
        CancellationToken cancellationToken,
        bool tracked = false)
    {
        var subscription = await _queries.FirstOrDefaultAsync(
            _subscriptions.Query(asNoTracking: !tracked),
            cancellationToken)
            ?? throw new RequestRejectedException(
                System.Net.HttpStatusCode.NotFound,
                NoSubscriptionCode,
                "This organisation has no subscription.");

        // A 500, not a 404, and the difference is load-bearing for the client. It reads any 404
        // on this route as "there is no plan" and locks; a 5xx it reads as "no answer" and fails
        // open. A subscription whose plan row has been deleted is a data fault on our side, so
        // failing open is the right direction - the alternative is locking a paying customer out
        // of a product they bought because somebody archived a plan.
        var plan = await _queries.FirstOrDefaultAsync(
            _plans.Query(asNoTracking: !tracked)
                .Where(plan => plan.Id == subscription.SubscriptionPlanId),
            cancellationToken)
            ?? throw new RequestRejectedException(
                System.Net.HttpStatusCode.InternalServerError,
                "plan_unavailable",
                $"This workspace's subscription points at a plan that no longer exists "
                + $"(#{subscription.SubscriptionPlanId}). Restore the plan or move the workspace to another.");

        return (subscription, plan);
    }

    /// <summary>
    /// The code on the 404 a workspace with no plan gets.
    /// </summary>
    /// <remarks>
    /// Public because it is a contract term, not an implementation detail - the client branches
    /// on it, and a test asserts it.
    /// </remarks>
    public const string NoSubscriptionCode = "no_subscription";

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
            // Every number not removed, disconnected ones included: each occupies a slot until it is
            // deleted, or disconnecting and connecting another would walk past the plan.
            new(UsageMetricKey.WhatsAppAccounts, "WhatsApp numbers", whatsAppAccounts,
                plan.MaxWhatsAppAccounts, "numbers"),

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
                plan.MonthlyMessageLimit,
                plan.MonthlyAiReplyLimit,
                plan.MaxSearchRadiusKm),
            plan.Highlights,
            plan.SortOrder,
            plan.ModifiedOn ?? plan.CreatedOn,
            AutoReplyTriggers.Expand(plan.AutoReplyTriggers));

    /// <inheritdoc />
    public async Task<RenderedInvoice> RenderInvoiceAsync(
        string invoiceId,
        CancellationToken cancellationToken = default)
    {
        var id = PublicId.Parse(PublicId.Invoice, invoiceId, "invoice");

        // Tenant-filtered. Another workspace's identifier resolves to nothing and surfaces as a
        // 404, so an invoice id cannot be probed for existence from outside the workspace.
        var invoice = await _queries.FirstOrDefaultAsync(
            _invoices.Query().Where(candidate => candidate.Id == id),
            cancellationToken)
            ?? throw new NotFoundException("Invoice", invoiceId);

        var profile = await _queries.FirstOrDefaultAsync(_profiles.Query(), cancellationToken);

        var tenant = _tenantContext.TenantId is { } tenantId
            ? await _queries.FirstOrDefaultAsync(
                _tenants.Query().Where(candidate => candidate.Id == tenantId),
                cancellationToken)
            : null;

        var document = new InvoiceDocument(
            MapInvoice(invoice),
            profile is null ? null : MapProfile(profile),
            tenant?.Name ?? string.Empty,
            _emailOptions.FromName);

        var content = await _renderer.RenderAsync(document, cancellationToken);

        // Named for the invoice, matching what the client saves it as.
        return new RenderedInvoice(content, $"{invoice.Number}.pdf", _renderer.ContentType);
    }

    /// <summary>Projects the stored billing profile onto its response shape.</summary>
    private static BillingProfileResponse MapProfile(BillingProfile profile) =>
        new(
            profile.CompanyName,
            profile.AddressLine1,
            profile.AddressLine2,
            profile.City,
            profile.Region,
            profile.PostalCode,
            profile.Country,
            profile.TaxId,
            profile.BillingEmail);

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
