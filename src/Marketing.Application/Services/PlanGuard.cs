using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;

namespace Marketing.Application.Services;

/// <summary>
/// Answers what the resolved tenant's plan permits.
/// <para>
/// Both checks it performs are enforced here rather than in the client, because both are commercial
/// boundaries: a module the tenant has not paid for, and a record ceiling they have not paid to
/// exceed. The client hides the affordances; this is what happens when someone calls the endpoint
/// anyway.
/// </para>
/// </summary>
public interface IPlanGuard
{
    /// <summary>Refuses the request when the resolved tenant's plan lacks a module.</summary>
    /// <param name="module">Module key from <see cref="PlanModules"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ForbiddenException">The plan does not include the module.</exception>
    public Task EnsureModuleAsync(string module, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refuses the request when creating <paramref name="adding"/> contacts would exceed the plan.
    /// </summary>
    /// <param name="adding">How many contacts are about to be created.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="BusinessRuleException">The ceiling would be breached.</exception>
    public Task EnsureContactCapacityAsync(int adding, CancellationToken cancellationToken = default);

    /// <summary>
    /// How many more contacts the tenant may store.
    /// <para>
    /// Used by the import, which fills to the ceiling and reports the remainder as skipped rather
    /// than failing a job the operator has already waited for.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see cref="int.MaxValue"/> when the plan is unlimited or no plan applies.</returns>
    public Task<int> RemainingContactCapacityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Module keys the resolved tenant's plan includes.
    /// </summary>
    /// <remarks>
    /// Every module when no plan applies, so platform-level work is not accidentally restricted by
    /// a commercial rule that has nothing to say about it.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyCollection<string>> EnabledModulesAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPlanGuard" />
public sealed class PlanGuard : IPlanGuard
{
    private readonly IRepository<TenantSubscription> _subscriptions;
    private readonly IRepository<SubscriptionPlan> _plans;
    private readonly IRepository<Contact> _contacts;
    private readonly IQueryExecutor _queries;
    private readonly ITenantContext _tenantContext;

    private SubscriptionPlan? _cachedPlan;
    private bool _planResolved;

    /// <summary>Initialises a new instance.</summary>
    public PlanGuard(
        IRepository<TenantSubscription> subscriptions,
        IRepository<SubscriptionPlan> plans,
        IRepository<Contact> contacts,
        IQueryExecutor queries,
        ITenantContext tenantContext)
    {
        _subscriptions = subscriptions;
        _plans = plans;
        _contacts = contacts;
        _queries = queries;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task EnsureModuleAsync(string module, CancellationToken cancellationToken = default)
    {
        var plan = await ResolvePlanAsync(cancellationToken);

        if (plan is null)
        {
            // No subscription resolves for this context: platform-level work, or a Super Admin
            // acting on their own account. Commercial limits describe what a customer bought and
            // have nothing to say here.
            return;
        }

        if (plan.EnabledModules.Contains(module, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        throw new ForbiddenException(
            $"The {plan.Name} plan does not include this feature. Upgrade to enable it.");
    }

    /// <inheritdoc />
    public async Task EnsureContactCapacityAsync(int adding, CancellationToken cancellationToken = default)
    {
        var remaining = await RemainingContactCapacityAsync(cancellationToken);

        if (remaining >= adding)
        {
            return;
        }

        var plan = await ResolvePlanAsync(cancellationToken);

        throw new BusinessRuleException(
            "contact_limit_reached",
            $"Your {plan?.Name} plan allows {plan?.MaxContacts:N0} contacts. Upgrade to add more.");
    }

    /// <inheritdoc />
    public async Task<int> RemainingContactCapacityAsync(CancellationToken cancellationToken = default)
    {
        var plan = await ResolvePlanAsync(cancellationToken);

        if (plan?.MaxContacts is not { } ceiling || ceiling <= 0)
        {
            return int.MaxValue;
        }

        // Counted live rather than read from a stored figure. The same number drives the usage
        // gauge, and a nightly total would let a tenant cross the ceiling for a whole day.
        var stored = await _queries.CountAsync(_contacts.Query(), cancellationToken);

        return Math.Max(0, ceiling - stored);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> EnabledModulesAsync(CancellationToken cancellationToken = default)
    {
        var plan = await ResolvePlanAsync(cancellationToken);

        return plan?.EnabledModules ?? [.. PlanModules.All];
    }

    /// <summary>Loads the tenant's plan once per request, or null when none applies.</summary>
    private async Task<SubscriptionPlan?> ResolvePlanAsync(CancellationToken cancellationToken)
    {
        if (_planResolved)
        {
            return _cachedPlan;
        }

        _planResolved = true;

        if (!_tenantContext.HasTenant)
        {
            return _cachedPlan = null;
        }

        var subscription = await _queries.FirstOrDefaultAsync(_subscriptions.Query(), cancellationToken);

        if (subscription is null)
        {
            return _cachedPlan = null;
        }

        return _cachedPlan = await _queries.FirstOrDefaultAsync(
            _plans.Query().Where(plan => plan.Id == subscription.SubscriptionPlanId),
            cancellationToken);
    }
}
