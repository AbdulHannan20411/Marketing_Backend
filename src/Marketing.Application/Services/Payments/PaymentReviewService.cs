using Marketing.Application.DTOs.Payments;
using Marketing.Business.Extensions;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.Common.Responses;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Payments;

/// <summary>
/// The platform's half of manual payment: look at the proof and decide.
/// </summary>
/// <remarks>
/// <b>Approval is the only path in the platform that grants a plan from a customer-initiated
/// action.</b> Everything about it is written for that: the status check happens inside the
/// transaction that grants the plan, so two reviewers with the queue open cannot both succeed, and
/// the grant and the stamp commit together so a request can never read as approved with nothing
/// granted behind it.
/// </remarks>
public interface IPaymentReviewService
{
    /// <summary>Returns the review queue.</summary>
    /// <param name="query">Paging, search and the status filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<PaymentRequestResponse>> GetQueueAsync(
        PaymentRequestQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one request, from any tenant.</summary>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PaymentRequestResponse> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Opens a request's proof, from any tenant.</summary>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<StoredFile> OpenProofAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Accepts a payment and grants the plan.</summary>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="BusinessRuleException">Already decided, or the plan is no longer available.</exception>
    public Task<PaymentRequestResponse> ApproveAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Refuses a payment, with a reason the customer is told.</summary>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="reason">Why it was refused. At least ten characters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PaymentRequestResponse> RejectAsync(
        string id,
        string? reason,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPaymentReviewService" />
public sealed class PaymentReviewService : IPaymentReviewService
{
    /// <summary>Shortest rejection reason that can plausibly tell a customer what to fix.</summary>
    public const int MinimumReasonLength = 10;

    private readonly IRepository<PaymentRequest> _requests;
    private readonly IRepository<SubscriptionPlan> _plans;
    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IRepository<Invoice> _invoices;
    private readonly IRepository<Payment> _payments;
    private readonly IRepository<User> _users;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorage _storage;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUser _currentUser;
    private readonly IDateTimeProvider _clock;
    private readonly IPaymentNotifier _notifier;

    /// <summary>Initialises a new instance.</summary>
    public PaymentReviewService(
        IRepository<PaymentRequest> requests,
        IRepository<SubscriptionPlan> plans,
        IRepository<TenantSubscription> subscriptions,
        IRepository<Invoice> invoices,
        IRepository<Payment> payments,
        IRepository<User> users,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IFileStorage storage,
        ITenantContext tenantContext,
        ICurrentUser currentUser,
        IDateTimeProvider clock,
        IPaymentNotifier notifier)
    {
        _requests = requests;
        _plans = plans;
        _subscriptions = subscriptions;
        _invoices = invoices;
        _payments = payments;
        _users = users;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _storage = storage;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
        _clock = clock;
        _notifier = notifier;
    }

    /// <inheritdoc />
    public async Task<PagedResult<PaymentRequestResponse>> GetQueueAsync(
        PaymentRequestQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Platform staff read across every tenant; the query filter is bypassed for them by the
        // tenant context, which is the only thing that grants this breadth.
        var source = _requests.Query();

        if (!string.IsNullOrWhiteSpace(query.Status)
            && !string.Equals(query.Status, PaymentRequestQuery.All, StringComparison.OrdinalIgnoreCase))
        {
            // An unrecognised status matches nothing rather than everything. Silently showing the
            // whole queue would let a reviewer believe they had filtered it.
            var status = Enum.TryParse<PaymentRequestStatus>(query.Status, ignoreCase: true, out var parsed)
                ? parsed
                : (PaymentRequestStatus?)null;

            source = status is { } value
                ? source.Where(request => request.Status == value)
                : source.Where(_ => false);
        }

        source = source.WhereOrganisationOrEmailMatches(query.Search);

        var page = await _queries.ToPagedAsync(
            source.OrderByDescending(request => request.SubmittedAt),
            query.Page,
            query.PageSize,
            cancellationToken);

        var adminIds = await PaymentMapping.LoadAdminIdsAsync(_queries, _users, [.. page.Items], cancellationToken);

        return page.Map(request => PaymentMapping.ToResponse(request, adminIds));
    }

    /// <inheritdoc />
    public async Task<PaymentRequestResponse> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var request = await LoadAsync(id, tracked: false, cancellationToken);

        return await DescribeAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StoredFile> OpenProofAsync(string id, CancellationToken cancellationToken = default)
    {
        var request = await LoadAsync(id, tracked: false, cancellationToken);

        var content = await _storage.OpenAsync(request.ProofStorageKey, cancellationToken);

        return new StoredFile(content, request.ProofFileName, request.ProofContentType);
    }

    /// <inheritdoc />
    public async Task<PaymentRequestResponse> ApproveAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var key = PublicId.Parse(PublicId.PaymentRequest, id, "payment request");

        var (request, periodEnd) = await _unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                var request = await _requests.GetForUpdateAsync(key, token)
                              ?? throw new NotFoundException("Payment request", id);

                // Re-read inside the transaction and refuse anything already decided. Two reviewers
                // with the queue open is the expected case, not the edge case, and a read-then-write
                // outside a transaction would let both of them grant the same plan.
                EnsurePending(request);

                var plan = await _plans.GetByIdAsync(request.SubscriptionPlanId, token)
                           ?? throw new BusinessRuleException(
                               "plan_unavailable", "That plan no longer exists.");

                if (plan.Status != PlanStatus.Active)
                {
                    // Refused rather than granted: honouring a payment for something that has been
                    // withdrawn would put the customer on a plan nobody supports.
                    throw new BusinessRuleException(
                        "plan_unavailable",
                        $"The {plan.Name} plan is no longer available. Refund the payment or offer another plan.");
                }

                var tenantId = request.TenantId
                               ?? throw new BusinessRuleException(
                                   "payment_request_orphaned",
                                   "That payment is not attached to a workspace.");

                // The grant runs inside the requesting tenant, so the subscription, invoice and
                // payment rows land under the right query filter rather than the reviewer's.
                using (_tenantContext.BeginScope(tenantId))
                {
                    var periodEnd = await GrantAsync(request, plan, tenantId, token);

                    request.Status = PaymentRequestStatus.Approved;
                    request.ReviewedAt = _clock.UtcNow;
                    request.ReviewedByUserId = _currentUser.AuditUserId;
                    request.ReviewedByName = _currentUser.DisplayName;

                    await _unitOfWork.SaveChangesAsync(token);

                    return (request, periodEnd);
                }
            },
            cancellationToken);

        var response = await DescribeAsync(request, cancellationToken);

        await _notifier.ApprovedAsync(request, response, periodEnd, cancellationToken);

        return response;
    }

    /// <inheritdoc />
    public async Task<PaymentRequestResponse> RejectAsync(
        string id,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var trimmed = reason?.Trim() ?? string.Empty;

        // Enforced here as well as in the client, because the client's copy is advisory and this
        // sentence is the entire explanation the customer receives.
        if (trimmed.Length < MinimumReasonLength)
        {
            throw new ValidationException(
                "reason",
                $"Explain why the payment could not be confirmed, in at least {MinimumReasonLength} characters.");
        }

        var request = await LoadAsync(id, tracked: true, cancellationToken);

        EnsurePending(request);

        request.Status = PaymentRequestStatus.Rejected;
        request.RejectionReason = trimmed;
        request.ReviewedAt = _clock.UtcNow;
        request.ReviewedByUserId = _currentUser.AuditUserId;
        request.ReviewedByName = _currentUser.DisplayName;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var response = await DescribeAsync(request, cancellationToken);

        await _notifier.RejectedAsync(request, response, cancellationToken);

        return response;
    }

    /// <summary>
    /// Moves the tenant onto the paid plan and records what they paid.
    /// </summary>
    /// <remarks>
    /// Runs inside the approval transaction. The invoice and payment rows exist so the customer's
    /// billing history shows the money they actually sent — without them, an approved manual
    /// payment leaves no trace anywhere the customer can see.
    /// </remarks>
    /// <returns>The instant the newly started period ends.</returns>
    private async Task<DateTimeOffset> GrantAsync(
        PaymentRequest request,
        SubscriptionPlan plan,
        long tenantId,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var periodEnd = now.AddMonths(request.BillingCycle == BillingCycle.Yearly ? 12 : plan.RenewalPeriodMonths);

        var subscription = await _queries.FirstOrDefaultAsync(
            _subscriptions.Query(asNoTracking: false),
            cancellationToken);

        if (subscription is null)
        {
            subscription = new TenantSubscription { TenantId = tenantId };

            _subscriptions.Add(subscription);
        }

        subscription.SubscriptionPlanId = plan.Id;
        subscription.BillingCycle = request.BillingCycle;
        subscription.Status = SubscriptionStatus.Active;
        subscription.CurrentPeriodStart = now;
        subscription.CurrentPeriodEnd = periodEnd;
        subscription.ExpiresAt = periodEnd;
        subscription.NextRenewalAt = subscription.AutoRenew ? periodEnd : null;
        subscription.Amount = request.Amount;
        subscription.Currency = request.Currency;

        // The trial, if any, is over: they have paid.
        subscription.TrialEndsAt = null;

        var invoice = new Invoice
        {
            TenantId = tenantId,
            Number = $"INV-{now:yyyyMM}-{request.Id}",
            PlanName = plan.Name,
            BillingCycle = request.BillingCycle,
            Amount = request.Amount,
            Currency = request.Currency,
            Status = InvoiceStatus.Paid,
            IssuedAt = request.SubmittedAt,
            DueAt = request.SubmittedAt,
            PaidAt = now,

            // The period this invoice covers — the same one the subscription was just moved onto.
            // Left unset these default to year one, which the rendered PDF prints as
            // "1 Jan 0001 – 1 Jan 0001" on a document the customer keeps for their accounts.
            PeriodStart = now,
            PeriodEnd = periodEnd,
        };

        _invoices.Add(invoice);

        _payments.Add(new Payment
        {
            TenantId = tenantId,

            // By navigation: the invoice is inserted in this same unit of work and has no key yet.
            Invoice = invoice,
            InvoiceNumber = invoice.Number,
            Amount = request.Amount,
            Currency = request.Currency,
            Status = PaymentStatus.Succeeded,
            Method = PaymentMethodKind.BankTransfer,
            ProcessedAt = now,
        });

        request.Invoice = invoice;

        return periodEnd;
    }

    /// <summary>Refuses anything already decided.</summary>
    private static void EnsurePending(PaymentRequest request)
    {
        if (request.Status != PaymentRequestStatus.Pending)
        {
            throw new BusinessRuleException(
                "payment_already_decided",
                $"That payment has already been {request.Status.ToString().ToLowerInvariant()}.");
        }
    }

    /// <summary>Loads a request from any tenant, or reports it missing.</summary>
    private async Task<PaymentRequest> LoadAsync(string id, bool tracked, CancellationToken cancellationToken)
    {
        var key = PublicId.Parse(PublicId.PaymentRequest, id, "payment request");

        var request = tracked
            ? await _requests.GetForUpdateAsync(key, cancellationToken)
            : await _queries.FirstOrDefaultAsync(
                _requests.Query().Where(candidate => candidate.Id == key),
                cancellationToken);

        return request ?? throw new NotFoundException("Payment request", id);
    }

    private async Task<PaymentRequestResponse> DescribeAsync(
        PaymentRequest request,
        CancellationToken cancellationToken)
    {
        var adminIds = await PaymentMapping.LoadAdminIdsAsync(_queries, _users, [request], cancellationToken);

        return PaymentMapping.ToResponse(request, adminIds);
    }
}
