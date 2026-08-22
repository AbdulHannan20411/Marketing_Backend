using Marketing.Application.DTOs.Workspace;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Workspace;

/// <summary>Switching a whole workspace off at its owner's request.</summary>
public interface IWorkspaceDeactivationService
{
    /// <summary>Deactivates the caller's workspace.</summary>
    /// <param name="request">Reason, details and the caller's password.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WorkspaceDeactivationResponse> DeactivateAsync(
        WorkspaceDeactivationRequest request,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWorkspaceDeactivationService" />
/// <remarks>
/// Deactivation, never deletion. The workspace stops working and the data stays: a self-service
/// button that destroys a customer's data forever is a support ticket waiting to happen, and the
/// client's copy promises the data survives.
/// </remarks>
public sealed partial class WorkspaceDeactivationService : IWorkspaceDeactivationService
{
    /// <summary>
    /// How long the data is kept after the workspace goes off.
    /// </summary>
    /// <remarks>
    /// Returned to the customer and therefore a commitment, not a note. If deletion after this date
    /// is ever automated, a workspace that comes back must be removed from that queue first.
    /// </remarks>
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);

    private readonly IRepository<Tenant> _tenants;
    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<WorkspaceDeactivationService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public WorkspaceDeactivationService(
        IRepository<Tenant> tenants,
        IRepository<TenantSubscription> subscriptions,
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        ICurrentUser currentUser,
        ITenantContext tenantContext,
        IDateTimeProvider clock,
        ILogger<WorkspaceDeactivationService> logger)
    {
        _tenants = tenants;
        _subscriptions = subscriptions;
        _users = users;
        _refreshTokens = refreshTokens;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _currentUser = currentUser;
        _tenantContext = tenantContext;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WorkspaceDeactivationResponse> DeactivateAsync(
        WorkspaceDeactivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tenantId = _tenantContext.RequireTenantId();
        var userId = _currentUser.UserId ?? throw new AuthenticationException("not_authenticated");

        // Ownership, not a permission. A permission could be granted to an employee by an admin who
        // did not think it through, and "can switch off the company" should follow ownership rather
        // than a checkbox somebody can tick.
        if (!_currentUser.IsInRole(Roles.Admin) && !_currentUser.IsInRole(Roles.SuperAdmin))
        {
            throw new ForbiddenException("Only the workspace owner can deactivate the workspace.");
        }

        ValidateReason(request);

        var user = await _users.GetForUpdateAsync(userId, cancellationToken)
                   ?? throw new NotFoundException(nameof(User), userId);

        // The only thing standing between a session left open on a shared laptop and a switched-off
        // company. Re-verified here regardless of how recently they signed in.
        var (isValid, _) = _passwordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash);

        if (!isValid)
        {
            throw new ValidationException(
                nameof(request.CurrentPassword),
                "That is not your current password.");
        }

        var tenant = await _tenants.GetForUpdateAsync(tenantId, cancellationToken)
                     ?? throw new NotFoundException(nameof(Tenant), tenantId);

        if (tenant.Status is TenantStatus.Deactivated)
        {
            throw new BusinessRuleException(
                "already_deactivated",
                "This workspace is already deactivated.");
        }

        var now = _clock.UtcNow;
        var retainedUntil = DateOnly.FromDateTime(now.Add(RetentionPeriod).UtcDateTime);

        tenant.Status = TenantStatus.Deactivated;
        tenant.DeactivatedOn = now;
        tenant.DeactivationReason = request.Reason;
        tenant.DeactivationDetails = request.Details?.Trim();
        tenant.DeactivatedByUserId = userId;
        tenant.DataRetainedUntil = retainedUntil;

        await StopBillingAsync(tenantId, cancellationToken);
        await RevokeEveryoneAsync(tenantId, now, cancellationToken);

        // One transaction. A workspace marked off whose sessions survived, or sessions killed
        // without the workspace being marked, are both worse than either outcome alone.
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogWorkspaceDeactivated(tenantId, userId, request.Reason.ToString());

        return new WorkspaceDeactivationResponse(now, retainedUntil);
    }

    /// <summary>Refuses a reason the platform does not recognise, or "other" with nothing said.</summary>
    private static void ValidateReason(WorkspaceDeactivationRequest request)
    {
        if (!Enum.IsDefined(request.Reason))
        {
            throw new ValidationException(nameof(request.Reason), "Choose why you are deactivating.");
        }

        // "Something else" with no text is the same as no answer, and this is the one moment a
        // departing customer is willing to say why.
        if (request.Reason == DeactivationReason.Other && string.IsNullOrWhiteSpace(request.Details))
        {
            throw new ValidationException(nameof(request.Details), "Tell us a little more.");
        }
    }

    /// <summary>Stops the subscription renewing, so the next cycle does not charge.</summary>
    private async Task StopBillingAsync(long tenantId, CancellationToken cancellationToken)
    {
        var subscription = await _queries.FirstOrDefaultAsync(
            _subscriptions.Query(asNoTracking: false).Where(entity => entity.TenantId == tenantId),
            cancellationToken);

        if (subscription is null)
        {
            return;
        }

        subscription.AutoRenew = false;
        subscription.Status = SubscriptionStatus.Cancelled;
        subscription.NextRenewalAt = null;

        // The countdown is meaningless now, and leaving it set would have the reminder job email a
        // workspace nobody is watching about a plan nobody is paying for.
        subscription.LastExpiryReminderDay = null;
    }

    /// <summary>
    /// Ends every session for every user in the workspace, not only the caller's.
    /// </summary>
    /// <remarks>
    /// The owner switched off the company, not their own account. An employee left holding a live
    /// token would keep working in a workspace the owner believes is closed - and the security stamp
    /// is bumped as well as the sessions revoked, so an access token already issued stops validating
    /// rather than surviving to its own expiry.
    /// </remarks>
    private async Task RevokeEveryoneAsync(long tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var members = await _users.GetTenantMemberIdsAsync(tenantId, cancellationToken);

        foreach (var memberId in members)
        {
            var member = await _users.GetForUpdateAsync(memberId, cancellationToken);

            if (member is not null)
            {
                member.SecurityStamp = Guid.NewGuid();
            }

            await _refreshTokens.RevokeAllForUserAsync(
                memberId,
                "Workspace deactivated.",
                now,
                cancellationToken);
        }
    }

    [LoggerMessage(
        EventId = 3701,
        Level = LogLevel.Warning,
        Message = "Workspace {TenantId} was deactivated by user {UserId}. Reason: {Reason}.")]
    private partial void LogWorkspaceDeactivated(long tenantId, long userId, string reason);
}
