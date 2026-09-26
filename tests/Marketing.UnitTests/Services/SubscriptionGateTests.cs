using AwesomeAssertions;
using Marketing.Application.Services.Billing;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Whether a workspace may write, which is the half of the lock screen that is a boundary.
/// </summary>
/// <remarks>
/// The reported bug was that a workspace which had never bought a plan could use everything. On
/// the client that was one missing case in a status check; here it is the same shape of mistake -
/// "no subscription" is not a bad status, so a check written against statuses waves it through.
/// </remarks>
public sealed class SubscriptionGateTests : IDisposable
{
    private const long TenantId = 9401;

    private readonly List<TenantSubscription> _subscriptions = [];
    private readonly IRepository<TenantSubscription> _repository =
        Substitute.For<IRepository<TenantSubscription>>();
    private readonly InMemoryQueryExecutor _queries = new();
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    private readonly StubCurrentUser _caller = new() { UserId = 1, Roles = [Roles.Admin] };
    private StubTenantContext _tenant = new() { TenantId = TenantId };

    public SubscriptionGateTests()
    {
        _repository.Query(Arg.Any<bool>()).Returns(_ => _subscriptions.AsQueryable());
    }

    public void Dispose() => _memory.Dispose();

    private static readonly DateTimeOffset Now = new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

    private SubscriptionGate CreateGate() =>
        new(_repository, _queries, _tenant, _caller, _memory, new FixedDateTimeProvider(Now));

    private void Subscribed(SubscriptionStatus status, DateTimeOffset? expiresAt = null) =>
        _subscriptions.Add(new TenantSubscription
        {
            Id = 1,
            TenantId = TenantId,
            SubscriptionPlanId = 1,
            Status = status,
            ExpiresAt = expiresAt ?? Now.AddDays(30),
        });

    [Theory]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Trial)]
    public async Task A_paid_or_trialling_workspace_may_write(SubscriptionStatus status)
    {
        Subscribed(status);

        (await CreateGate().IsActiveAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Theory]
    [InlineData(SubscriptionStatus.Expired)]
    [InlineData(SubscriptionStatus.Suspended)]
    [InlineData(SubscriptionStatus.Cancelled)]
    public async Task A_lapsed_workspace_may_not(SubscriptionStatus status)
    {
        Subscribed(status);

        (await CreateGate().IsActiveAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task A_workspace_that_never_bought_anything_may_not()
    {
        // The reported bug, in one line. There is no row at all, so a check written against
        // statuses finds nothing bad and lets everything through - which is how a workspace with
        // no plan could add contacts, run imports and build campaigns.
        (await CreateGate().IsActiveAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task A_subscription_that_has_run_out_may_not_write_however_its_status_reads()
    {
        Subscribed(SubscriptionStatus.Active, expiresAt: Now.AddDays(-1));

        // Nothing in this codebase moves a subscription to Expired - the reminder job only
        // reminds - so a subscription that ran out last month still reads Active. A gate keyed on
        // status alone would let it write, which is a third way past the gate on top of the two
        // in the report.
        (await CreateGate().IsActiveAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task A_trial_that_has_run_out_may_not_write_either()
    {
        Subscribed(SubscriptionStatus.Trial, expiresAt: Now.AddDays(-1));

        (await CreateGate().IsActiveAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task A_subscription_ending_later_today_still_may()
    {
        Subscribed(SubscriptionStatus.Active, expiresAt: Now.AddHours(1));

        // The boundary is the instant, not the day. Somebody on their last afternoon has paid
        // for that afternoon.
        (await CreateGate().IsActiveAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task The_refusal_carries_the_code_the_client_keys_its_copy_off()
    {
        var call = async () => await CreateGate().DemandActiveAsync(TestContext.Current.CancellationToken);

        var refusal = (await call.Should().ThrowAsync<ForbiddenException>()).Which;

        // A code, not a sentence: the useful message is per-action ("choose a plan to add
        // contacts") and a gate that covers every write cannot know which action it stopped.
        refusal.ErrorCode.Should().Be("subscription_required");
        refusal.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_whole_subscription_refusal_names_no_module()
    {
        var call = async () => await CreateGate().DemandActiveAsync(TestContext.Current.CancellationToken);

        var refusal = (await call.Should().ThrowAsync<ForbiddenException>()).Which;

        // What tells the two dialogs apart. No module means there is no usable subscription at
        // all, so the offer is "purchase a plan"; naming one would say the workspace pays for
        // something already, which it does not.
        refusal.Extensions.Should().NotContainKey("module");
    }

    [Fact]
    public void The_module_refusal_uses_the_same_code_as_the_subscription_refusal()
    {
        // One code, two refusals. The client listens for exactly this string and turns it back
        // into an upgrade offer; a second code would arrive as a red toast the customer can do
        // nothing with. The two constants live in different services, so this is what keeps them
        // from drifting.
        Marketing.Application.Services.PlanGuard.SubscriptionRequiredCode
            .Should().Be(SubscriptionGate.ErrorCode);
    }

    [Fact]
    public async Task Platform_staff_are_not_the_customer()
    {
        _caller.Roles = [Roles.SuperAdmin];

        // Support is most needed on the workspace that has not paid. A Super Admin working inside
        // one through ?adminId= is not subject to its plan.
        (await CreateGate().IsActiveAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task A_request_with_no_workspace_is_not_gated()
    {
        _tenant = new StubTenantContext { TenantId = null };

        // Platform routes, and the Meta webhook, which carries no token. Refusing that would make
        // an unpaid invoice into lost inbound messages, which are the customer's, not ours.
        (await CreateGate().IsActiveAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task The_answer_is_read_once_and_remembered()
    {
        Subscribed(SubscriptionStatus.Active);

        var gate = CreateGate();

        await gate.IsActiveAsync(TestContext.Current.CancellationToken);
        await gate.IsActiveAsync(TestContext.Current.CancellationToken);
        await gate.IsActiveAsync(TestContext.Current.CancellationToken);

        // This runs on every write in the product. A query each time would put a round trip in
        // front of every save in the application.
        _queries.Queries.Should().Be(1);
    }

    /// <summary>Builds a plan guard over a workspace on a plan with the given modules.</summary>
    /// <param name="modules">Modules the plan includes.</param>
    private Marketing.Application.Services.PlanGuard GuardOnPlanWith(params string[] modules)
    {
        Subscribed(SubscriptionStatus.Active);

        var plans = Substitute.For<IRepository<SubscriptionPlan>>();

        plans.Query(Arg.Any<bool>()).Returns(_ => new[]
        {
            new SubscriptionPlan
            {
                Id = 1,
                Name = "Starter",
                EnabledModules = [.. modules],
            },
        }.AsQueryable());

        return new Marketing.Application.Services.PlanGuard(
            _repository, plans, Substitute.For<IRepository<Contact>>(), _queries, _tenant);
    }

    [Fact]
    public async Task A_plan_without_the_module_is_refused_with_the_module_named()
    {
        var guard = GuardOnPlanWith(PlanModules.Crm);

        var call = async () => await guard.EnsureModuleAsync(
            PlanModules.WhatsApp, TestContext.Current.CancellationToken);

        var refusal = (await call.Should().ThrowAsync<ForbiddenException>()).Which;

        // The client turns this into "Upgrade plan" rather than "Purchase a plan", and names the
        // module in the copy. Before this it was a bare forbidden, which arrived as a red toast
        // the customer could do nothing with.
        refusal.ErrorCode.Should().Be(SubscriptionGate.ErrorCode);
        refusal.Extensions.Should().ContainKey("module").WhoseValue.Should().Be(PlanModules.WhatsApp);
    }

    [Fact]
    public async Task A_plan_that_includes_the_module_is_not_refused()
    {
        var guard = GuardOnPlanWith(PlanModules.Crm, PlanModules.WhatsApp);

        await guard.EnsureModuleAsync(PlanModules.WhatsApp, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Paying_takes_effect_without_waiting_for_the_cache()
    {
        Subscribed(SubscriptionStatus.Expired);

        var gate = CreateGate();

        (await gate.IsActiveAsync(TestContext.Current.CancellationToken)).Should().BeFalse();

        // What BillingService does when a plan changes or a payment is approved.
        _subscriptions[0].Status = SubscriptionStatus.Active;
        _memory.Remove(SubscriptionGate.KeyFor(TenantId));

        (await gate.IsActiveAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }
}
