using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Billing;

/// <summary>Whether a workspace currently has a plan that entitles it to use the product.</summary>
public interface ISubscriptionGate
{
    /// <summary>Whether the caller's workspace has an active or trialling subscription.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<bool> IsActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Refuses the operation when the workspace has no plan.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ForbiddenException">The workspace has no active or trialling plan.</exception>
    public Task DemandActiveAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISubscriptionGate" />
public sealed class SubscriptionGate : ISubscriptionGate
{
    /// <summary>
    /// The code the client keys its copy off.
    /// </summary>
    /// <remarks>
    /// A code rather than a sentence, because the useful message is per-action - "choose a plan to
    /// add contacts" - and a gate applied across every write cannot know which action it stopped.
    /// The client knows what the user was doing; the API says why it refused.
    /// </remarks>
    public const string ErrorCode = "subscription_required";

    /// <summary>
    /// How long a workspace's answer is reused.
    /// </summary>
    /// <remarks>
    /// Short. This runs on every write in the product, so it must not be a query each time; and a
    /// customer who has just paid must not spend a minute locked out of what they paid for. The
    /// subscription write paths clear it outright, so this is the ceiling on staleness rather
    /// than the usual wait.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IQueryExecutor _queries;
    private readonly ITenantContext _tenantContext;
    private readonly ICurrentUser _currentUser;
    private readonly IMemoryCache _memory;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public SubscriptionGate(
        IRepository<TenantSubscription> subscriptions,
        IQueryExecutor queries,
        ITenantContext tenantContext,
        ICurrentUser currentUser,
        IMemoryCache memory,
        IDateTimeProvider clock)
    {
        _subscriptions = subscriptions;
        _queries = queries;
        _tenantContext = tenantContext;
        _currentUser = currentUser;
        _memory = memory;
        _clock = clock;
    }

    /// <summary>The two columns the gate reads.</summary>
    /// <param name="Status">Stored state.</param>
    /// <param name="ExpiresAt">End of the paid period.</param>
    private sealed record SubscriptionState(SubscriptionStatus Status, DateTimeOffset ExpiresAt);

    /// <summary>Cache key for one workspace's answer.</summary>
    /// <param name="tenantId">Workspace key.</param>
    public static string KeyFor(long tenantId) => $"subscription-gate:{tenantId}";

    /// <inheritdoc />
    public async Task<bool> IsActiveAsync(CancellationToken cancellationToken = default)
    {
        // Platform staff are not the customer. A Super Admin working inside a workspace through
        // ?adminId= is doing support, and support is most needed on the workspace that has not
        // paid.
        if (_currentUser.IsSuperAdmin)
        {
            return true;
        }

        // No workspace is a platform-level or unauthenticated route. There is no plan to check,
        // and inventing a refusal here would break the Meta webhook, which has no token.
        if (_tenantContext.TenantId is not { } tenantId)
        {
            return true;
        }

        var key = KeyFor(tenantId);

        if (_memory.TryGetValue<bool>(key, out var remembered))
        {
            return remembered;
        }

        // Two columns, one row. The entitlements endpoint answers a richer question at the cost
        // of eight queries, which is the wrong trade for something on the write path.
        var row = await _queries.FirstOrDefaultAsync(
            _subscriptions.Query().Select(subscription => new SubscriptionState(
                subscription.Status,
                subscription.ExpiresAt)),
            cancellationToken);

        // No row is the case this gate exists for: a workspace that never bought anything. It
        // used to fall through every check as "no status, therefore not a bad status".
        //
        // The date is checked as well as the status, because nothing in this codebase moves a
        // subscription to Expired - the reminder job only reminds. A subscription that ran out
        // last month still reads Active, so a gate keyed on status alone would let it write, and
        // the entitlements endpoint would report it as active to the client at the same time.
        // ResumeAsync already reasons this way for the same reason.
        var active = row is not null
                     && row.Status is SubscriptionStatus.Active or SubscriptionStatus.Trial
                     && row.ExpiresAt > _clock.UtcNow;

        _memory.Set(key, active, Lifetime);

        return active;
    }

    /// <inheritdoc />
    public async Task DemandActiveAsync(CancellationToken cancellationToken = default)
    {
        if (await IsActiveAsync(cancellationToken))
        {
            return;
        }

        throw new ForbiddenException(
            ErrorCode,
            "This workspace has no active plan. Choose a plan to continue.");
    }
}
