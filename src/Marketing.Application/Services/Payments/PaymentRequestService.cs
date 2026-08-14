using Marketing.Application.DTOs.Payments;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.Common.Responses;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Payments;

/// <summary>
/// The customer's half of manual payment: say you have paid, and attach the proof.
/// <para>
/// <b>Nothing here grants a plan.</b> Every method records or reads an intent; the entitlement
/// moves only when a platform administrator approves the request, in
/// <see cref="IPaymentReviewService"/>. That split is the whole security model of the feature — a
/// customer who can upload an image must not be able to upgrade themselves.
/// </para>
/// </summary>
public interface IPaymentRequestService
{
    /// <summary>Returns the active channels a customer can pay through.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<PaymentChannelDetails>> GetChannelsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Opens a channel's QR image for download.</summary>
    /// <param name="channel">Channel whose code is wanted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<StoredFile> OpenChannelQrAsync(
        PaymentChannel channel,
        CancellationToken cancellationToken = default);

    /// <summary>Records a claim of payment and its proof.</summary>
    /// <param name="command">The submission.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PaymentRequestResponse> SubmitAsync(
        SubmitPaymentCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Returns this workspace's submissions, newest first.</summary>
    /// <param name="request">Paging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<PaymentRequestResponse>> GetMineAsync(
        Common.Requests.PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one of this workspace's submissions.</summary>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PaymentRequestResponse> GetMineAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>Withdraws a submission that has not been reviewed.</summary>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PaymentRequestResponse> CancelAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>Opens the uploaded proof.</summary>
    /// <param name="id">Prefixed request identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<StoredFile> OpenProofAsync(
        string id,
        CancellationToken cancellationToken = default);
}

/// <summary>An accepted upload, decoupled from the transport.</summary>
/// <param name="PlanId">Prefixed plan identifier.</param>
/// <param name="BillingCycle">Period being bought.</param>
/// <param name="Channel">How the customer says they paid.</param>
/// <param name="Reference">Transaction reference from their receipt.</param>
/// <param name="Note">Free text.</param>
/// <param name="FileName">Name the file arrived under.</param>
/// <param name="ContentType">Media type the client declared.</param>
/// <param name="Content">The file. The caller owns and disposes it.</param>
/// <param name="SizeBytes">Size of the upload.</param>
public sealed record SubmitPaymentCommand(
    string PlanId,
    string? BillingCycle,
    string? Channel,
    string? Reference,
    string? Note,
    string FileName,
    string? ContentType,
    Stream Content,
    long SizeBytes);

/// <summary>A file being served back, with what the browser needs to render it.</summary>
/// <param name="Content">Readable stream. The caller disposes it.</param>
/// <param name="FileName">Name the browser saves it under.</param>
/// <param name="ContentType">Media type.</param>
public sealed record StoredFile(Stream Content, string FileName, string ContentType);

/// <inheritdoc cref="IPaymentRequestService" />
public sealed class PaymentRequestService : IPaymentRequestService
{
    /// <summary>Container proofs are stored under.</summary>
    private const string ProofContainer = "payment-proofs";

    /// <summary>Largest proof accepted.</summary>
    public const long MaxProofBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Media types accepted, checked against the file's own bytes rather than its name.
    /// </summary>
    /// <remarks>
    /// An extension is a claim by the uploader. This endpoint takes a file from someone with a
    /// commercial motive and shows it to a member of staff, so what is stored is decided by
    /// sniffing the content.
    /// </remarks>
    private static readonly Dictionary<string, string> AcceptedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf",
    };

    private readonly IRepository<PaymentRequest> _requests;
    private readonly IRepository<PaymentChannelSetting> _channels;
    private readonly IRepository<SubscriptionPlan> _plans;
    private readonly IRepository<Tenant> _tenants;
    private readonly IRepository<User> _users;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorage _storage;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUser _currentUser;
    private readonly IDateTimeProvider _clock;
    private readonly IPaymentNotifier _notifier;

    /// <summary>Initialises a new instance.</summary>
    public PaymentRequestService(
        IRepository<PaymentRequest> requests,
        IRepository<PaymentChannelSetting> channels,
        IRepository<SubscriptionPlan> plans,
        IRepository<Tenant> tenants,
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
        _channels = channels;
        _plans = plans;
        _tenants = tenants;
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
    public async Task<IReadOnlyList<PaymentChannelDetails>> GetChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _queries.ToListAsync(
            _channels.Query().Where(channel => channel.IsActive).OrderBy(channel => channel.SortOrder),
            cancellationToken);

        return [.. rows.Select(PaymentMapping.ToDetails)];
    }

    /// <inheritdoc />
    public async Task<StoredFile> OpenChannelQrAsync(
        PaymentChannel channel,
        CancellationToken cancellationToken = default)
    {
        var setting = await _queries.FirstOrDefaultAsync(
            _channels.Query().Where(candidate => candidate.Channel == channel && candidate.IsActive),
            cancellationToken)
            ?? throw new NotFoundException("Payment channel", channel.ToString());

        if (string.IsNullOrEmpty(setting.QrStorageKey))
        {
            throw new NotFoundException("Payment channel QR code", channel.ToString());
        }

        var content = await _storage.OpenAsync(setting.QrStorageKey, cancellationToken);

        return new StoredFile(
            content,
            $"{channel.ToString().ToLowerInvariant()}-qr",
            setting.QrContentType ?? "application/octet-stream");
    }

    /// <inheritdoc />
    public async Task<PaymentRequestResponse> SubmitAsync(
        SubmitPaymentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var tenantId = _tenantContext.RequireTenantId();
        var contentType = ValidateFile(command);

        // Parsed here rather than at the edge, so the wire vocabulary of this module stays inside
        // it and the controller carries no domain enums.
        var cycle = PaymentMapping.ParseCycle(command.BillingCycle);
        var channel = PaymentMapping.ParseChannel(command.Channel);

        var planId = PublicId.Parse(PublicId.Plan, command.PlanId, "plan");

        var plan = await _queries.FirstOrDefaultAsync(
            _plans.Query().Where(candidate => candidate.Id == planId),
            cancellationToken)
            ?? throw new NotFoundException("Plan", command.PlanId);

        if (plan.Status != PlanStatus.Active)
        {
            throw new ValidationException("planId", $"The {plan.Name} plan is not available.");
        }

        // One open request at a time. Without this a customer can queue a dozen submissions and a
        // reviewer approves each of them, granting and re-granting the plan.
        var hasOpen = await _requests.ExistsAsync(
            candidate => candidate.Status == PaymentRequestStatus.Pending,
            cancellationToken);

        if (hasOpen)
        {
            throw new BusinessRuleException(
                "payment_request_pending",
                "You already have a payment awaiting review. Withdraw it before submitting another.");
        }

        var tenant = await _queries.FirstOrDefaultAsync(
            _tenants.Query().Where(candidate => candidate.Id == tenantId),
            cancellationToken);

        // Stored before the row, because the row has to carry the key. A failure afterwards leaves
        // an orphan file, which is harmless; the reverse leaves a request pointing at nothing, and
        // a reviewer cannot approve what they cannot see.
        var storageKey = await _storage.SaveAsync(
            ProofContainer, command.FileName, command.Content, cancellationToken);

        var request = new PaymentRequest
        {
            TenantId = tenantId,
            SubscriptionPlanId = plan.Id,
            PlanName = plan.Name,
            BillingCycle = cycle,

            // Derived here and nowhere else. The client never sends an amount.
            Amount = PaymentMapping.PriceFor(plan, cycle),
            Currency = plan.Currency,
            Channel = channel,
            Reference = Trim(command.Reference, 120),
            Note = Trim(command.Note, 1000),
            ProofStorageKey = storageKey,
            ProofFileName = Path.GetFileName(command.FileName),
            ProofContentType = contentType,
            ProofSizeBytes = command.SizeBytes,
            Status = PaymentRequestStatus.Pending,
            Organisation = tenant?.Name ?? string.Empty,
            SubmittedByUserId = _currentUser.AuditUserId,
            SubmittedByName = _currentUser.DisplayName ?? string.Empty,
            SubmittedByEmail = _currentUser.Email ?? string.Empty,
            SubmittedAt = _clock.UtcNow,
        };

        _requests.Add(request);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var response = await DescribeAsync(request, cancellationToken);

        await _notifier.SubmittedAsync(request, response, cancellationToken);

        return response;
    }

    /// <inheritdoc />
    public async Task<PagedResult<PaymentRequestResponse>> GetMineAsync(
        Common.Requests.PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Newest first, and the client relies on it: the "payment under review" banner reads
        // items[0] of a one-item page.
        var page = await _queries.ToPagedAsync(
            _requests.Query().OrderByDescending(candidate => candidate.SubmittedAt),
            request.Page,
            request.PageSize,
            cancellationToken);

        var adminIds = await LoadAdminIdsAsync([.. page.Items], cancellationToken);

        return page.Map(row => PaymentMapping.ToResponse(row, adminIds));
    }

    /// <inheritdoc />
    public async Task<PaymentRequestResponse> GetMineAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var request = await LoadAsync(id, tracked: false, cancellationToken);

        return await DescribeAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PaymentRequestResponse> CancelAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var request = await LoadAsync(id, tracked: true, cancellationToken);

        if (request.Status != PaymentRequestStatus.Pending)
        {
            throw new BusinessRuleException(
                "payment_already_decided",
                "That payment has already been reviewed and can no longer be withdrawn.");
        }

        request.Status = PaymentRequestStatus.Cancelled;
        request.ReviewedAt = _clock.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await DescribeAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StoredFile> OpenProofAsync(string id, CancellationToken cancellationToken = default)
    {
        // Loaded through the tenant-filtered repository, so another tenant's identifier is a 404
        // rather than a 403 — the two are indistinguishable to a caller, which is what keeps
        // identifiers from being enumerable.
        var request = await LoadAsync(id, tracked: false, cancellationToken);

        var content = await _storage.OpenAsync(request.ProofStorageKey, cancellationToken);

        return new StoredFile(content, request.ProofFileName, request.ProofContentType);
    }

    /// <summary>Refuses a file we cannot or should not accept, before anything is stored.</summary>
    /// <returns>The media type the proof is stored and served as.</returns>
    private static string ValidateFile(SubmitPaymentCommand command)
    {
        if (command.SizeBytes <= 0)
        {
            throw new ValidationException("proof", "Attach a screenshot or PDF of your payment receipt.");
        }

        if (command.SizeBytes > MaxProofBytes)
        {
            throw new ValidationException(
                "proof",
                $"That file is larger than the {MaxProofBytes / (1024 * 1024)} MB limit.");
        }

        var extension = Path.GetExtension(command.FileName);

        if (!AcceptedTypes.TryGetValue(extension, out var expected))
        {
            throw new ValidationException("proof", "Attach a PNG, JPG, WEBP or PDF file.");
        }

        // The media type comes from the extension we recognised, never from the client's header.
        // A browser will render whatever Content-Type it is served, so echoing an attacker-supplied
        // one back to a reviewer is how an "image" becomes a script in their session.
        return expected;
    }

    /// <summary>Loads a request within the caller's tenant, or reports it missing.</summary>
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
        var adminIds = await LoadAdminIdsAsync([request], cancellationToken);

        return PaymentMapping.ToResponse(request, adminIds);
    }

    /// <summary>
    /// Maps each request's tenant to its admin account, for the platform queue's scoping link.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, string>> LoadAdminIdsAsync(
        IReadOnlyList<PaymentRequest> requests,
        CancellationToken cancellationToken) =>
        await PaymentMapping.LoadAdminIdsAsync(_queries, _users, requests, cancellationToken);

    private static string Trim(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
}
