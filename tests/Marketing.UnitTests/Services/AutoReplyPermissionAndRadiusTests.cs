using System.Reflection;
using AwesomeAssertions;
using Marketing.Application.DTOs.Billing;
using Marketing.Application.Interfaces;
using Marketing.Application.Services;
using Marketing.Application.Services.BusinessDiscovery;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Marketing.UnitTests.Services;

/// <summary>Auto-reply's own permission.</summary>
public sealed class AutoReplyPermissionTests
{
    [Fact]
    public void Admins_hold_it_by_role_and_new_employees_do_not()
    {
        Permissions.ForRole(Roles.Admin).Should().Contain(Permissions.Ai.AutoReplyManage);
        Permissions.ForRole(Roles.Employee).Should().NotContain(Permissions.Ai.AutoReplyManage);
        Permissions.IsKnown("ai.autoreply.manage").Should().BeTrue();
    }
}

/// <summary>The plan's ceiling on the nearby-business search radius.</summary>
public sealed class SearchRadiusLimitTests
{
    private static readonly MethodInfo ApplyLimits =
        typeof(PlanManagementService).GetMethod("ApplyLimits", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo EnforceRadius =
        typeof(BusinessDiscoveryService).GetMethod("EnforcePlanRadiusAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static PlanLimits Limits(int? radius) =>
        new(null, null, null, null, null, null, null, null, null, null, null, radius);

    private static void Apply(SubscriptionPlan plan, int? radius)
    {
        try
        {
            ApplyLimits.Invoke(null, [plan, Limits(radius)]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(-1)]
    public void A_radius_outside_0_to_10_is_refused(int radius)
    {
        var apply = () => Apply(new SubscriptionPlan { Name = "Growth" }, radius);

        var errors = apply.Should().Throw<ValidationException>().Which.Errors;

        errors.Should().ContainKey("limits.maxSearchRadiusKm");
        errors["limits.maxSearchRadiusKm"][0].Should().Be("Search radius must be between 0 and 10 km, or unlimited.");
    }

    [Fact]
    public void Unlimited_is_stored_as_null()
    {
        var plan = new SubscriptionPlan { Name = "Enterprise", MaxSearchRadiusKm = 5 };

        Apply(plan, null);

        plan.MaxSearchRadiusKm.Should().BeNull();
    }

    private static async Task EnforceAsync(int? planLimit, double radiusKm, bool superAdmin = false)
    {
        var planGuard = Substitute.For<IPlanGuard>();
        planGuard.CurrentPlanAsync(Arg.Any<CancellationToken>())
            .Returns(new SubscriptionPlan { Name = "Growth", MaxSearchRadiusKm = planLimit });

        var service = new BusinessDiscoveryService(
            Substitute.For<IPlaceProvider>(),
            Substitute.For<ICacheService>(),
            Substitute.For<IRepository<Contact>>(),
            Substitute.For<IRepository<ContactGroup>>(),
            Substitute.For<IRepository<ContactGroupMember>>(),
            Substitute.For<IQueryExecutor>(),
            Substitute.For<IUnitOfWork>(),
            new StubTenantContext { TenantId = 3 },
            new StubCurrentUser { UserId = 9, Roles = superAdmin ? [Roles.SuperAdmin] : [Roles.Admin] },
            planGuard,
            NullLogger<BusinessDiscoveryService>.Instance);

        await (Task)EnforceRadius.Invoke(service, [radiusKm, CancellationToken.None])!;
    }

    [Fact]
    public async Task A_search_inside_the_plan_passes_and_one_past_it_is_refused()
    {
        await EnforceAsync(planLimit: 5, radiusKm: 5);

        var wider = () => EnforceAsync(planLimit: 5, radiusKm: 6);

        var refusal = (await wider.Should().ThrowAsync<ForbiddenException>()).Which;

        refusal.ErrorCode.Should().Be("radius_exceeds_plan");
        refusal.Message.Should().Be("Your plan allows searching up to 5 km.");
    }

    [Fact]
    public async Task A_plan_with_zero_has_no_search_at_all()
    {
        var search = () => EnforceAsync(planLimit: 0, radiusKm: 1);

        (await search.Should().ThrowAsync<ForbiddenException>()).Which.Message
            .Should().Be("Your plan does not include nearby business search.");
    }

    [Fact]
    public async Task No_plan_ceiling_leaves_only_the_platforms_10_km()
    {
        await EnforceAsync(planLimit: null, radiusKm: 10);
    }

    [Fact]
    public async Task Platform_staff_are_exempt_even_when_viewing_a_workspace()
    {
        await EnforceAsync(planLimit: 5, radiusKm: 10, superAdmin: true);
    }
}
