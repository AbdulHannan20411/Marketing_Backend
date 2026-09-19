using System.Reflection;
using AwesomeAssertions;
using Marketing.Application.Configurations;
using Marketing.Application.DTOs.Security;
using Marketing.Application.Services;
using Marketing.Application.Services.Security;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;
using RoleCatalog = Marketing.Common.Constants.Roles;

namespace Marketing.UnitTests.Services;

/// <summary>Suspending and reactivating accounts from the security screens.</summary>
public sealed class AccountSuspensionTests
{
    private const long WorkspaceA = 10;
    private const long WorkspaceB = 20;
    private const long AdminId = 101;
    private const long EmployeeId = 102;
    private const long OtherWorkspaceEmployeeId = 201;
    private const long PlatformStaffId = 1;

    private static readonly DateTimeOffset Now = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    private readonly List<User> _people = [];
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISessionTracker _tracker = Substitute.For<ISessionTracker>();
    private readonly IAccountRiskEvaluator _risk = Substitute.For<IAccountRiskEvaluator>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IRepository<Notification> _notifications = Substitute.For<IRepository<Notification>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AuditLog> _audited = [];
    private readonly List<Notification> _notified = [];

    public AccountSuspensionTests()
    {
        var admin = new Role { Id = 1, Name = RoleCatalog.Admin, NormalizedName = "ADMIN", Description = string.Empty };
        var employee = new Role { Id = 2, Name = RoleCatalog.Employee, NormalizedName = "EMPLOYEE", Description = string.Empty };
        var staff = new Role { Id = 3, Name = RoleCatalog.SuperAdmin, NormalizedName = "SUPERADMIN", Description = string.Empty };

        _people.Add(Person(AdminId, WorkspaceA, "Ayesha Khan", admin));
        _people.Add(Person(EmployeeId, WorkspaceA, "Sara Khan", employee));
        _people.Add(Person(OtherWorkspaceEmployeeId, WorkspaceB, "Bilal", employee));
        _people.Add(Person(PlatformStaffId, null, "Platform", staff));

        _users.Query(Arg.Any<bool>()).Returns(_ => _people.AsQueryable());
        _users.GetTenantAdministratorsAsync(WorkspaceA, Arg.Any<CancellationToken>())
            .Returns([_people[0]]);

        _risk.EvaluateAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new RiskAssessment(RiskLevel.Medium, 45, ["4 devices in 30 days"]));

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()!(CancellationToken.None));

        _audit.When(audit => audit.Add(Arg.Any<AuditLog>())).Do(call => _audited.Add(call.Arg<AuditLog>()!));
        _notifications.When(repository => repository.Add(Arg.Any<Notification>()))
            .Do(call => _notified.Add(call.Arg<Notification>()!));
    }

    private static User Person(long id, long? tenantId, string name, Role role)
    {
        var user = new User
        {
            Id = id,
            TenantId = tenantId,
            Email = $"{name.Split(' ')[0].ToLowerInvariant()}@example.test",
            NormalizedEmail = name.ToUpperInvariant(),
            DisplayName = name,
            PasswordHash = "hash",
            Status = UserStatus.Active,
        };

        user.UserRoles.Add(new UserRole { UserId = id, RoleId = role.Id, Role = role, User = user });

        return user;
    }

    private SecurityOverviewService CreateService()
    {
        var tenants = Substitute.For<IRepository<Tenant>>();
        tenants.Query(Arg.Any<bool>()).Returns(_ => new[]
        {
            new Tenant { Id = WorkspaceA, Name = "Glow Studio", Slug = "glow", ContactEmail = "a@glow.test" },
            new Tenant { Id = WorkspaceB, Name = "Other", Slug = "other", ContactEmail = "b@other.test" },
        }.AsQueryable());

        var sessions = Substitute.For<IRepository<UserSession>>();
        sessions.Query(Arg.Any<bool>()).Returns(_ => Enumerable.Empty<UserSession>().AsQueryable());

        var events = Substitute.For<IRepository<SecurityEvent>>();
        events.Query(Arg.Any<bool>()).Returns(_ => Enumerable.Empty<SecurityEvent>().AsQueryable());

        var subscriptions = Substitute.For<IRepository<TenantSubscription>>();
        subscriptions.Query(Arg.Any<bool>()).Returns(_ => Enumerable.Empty<TenantSubscription>().AsQueryable());

        return new SecurityOverviewService(
            sessions,
            events,
            tenants,
            subscriptions,
            _users,
            _tracker,
            _risk,
            new InMemoryQueryExecutor(),
            new FixedDateTimeProvider(Now),
            Options.Create(new AuthenticationPolicyOptions()),
            _unitOfWork,
            _audit,
            _notifications,
            new StubRequestContext());
    }

    private User Get(long id) => _people.Single(person => person.Id == id);

    [Fact]
    public async Task An_admin_suspends_an_employee_who_is_signed_out_everywhere()
    {
        var stampBefore = Get(EmployeeId).SecurityStamp;

        var row = await CreateService().SuspendAsync(
            WorkspaceA,
            EmployeeId,
            new SuspendAccountRequest("  Login shared with a second person ", "warning"),
            byPlatformStaff: false,
            AdminId,
            TestContext.Current.CancellationToken);

        row.Status.Should().Be(EmployeeStatus.Suspended);
        row.Risk.Should().BeNull();
        Get(EmployeeId).Status.Should().Be(UserStatus.Disabled);
        Get(EmployeeId).SecurityStamp.Should().NotBe(stampBefore);

        await _tracker.Received(1).RevokeAllAsync(EmployeeId, Arg.Any<string>(), AdminId, Arg.Any<CancellationToken>());

        // The screen's claim and the server's own assessment, side by side, with the reason trimmed.
        var entry = _audited.Should().ContainSingle().Which;
        entry.EntityName.Should().Be("security.account.suspended");
        entry.Changes.Should().Contain("\"alertLevel\":\"warning\"")
            .And.Contain("\"riskLevel\":\"medium\"")
            .And.Contain("\"riskScore\":45")
            .And.Contain("\"reason\":\"Login shared with a second person\"");

        // Only platform staff suspending someone tells the workspace's admins.
        _notified.Should().BeEmpty();
    }

    [Fact]
    public async Task Nobody_can_suspend_themselves()
    {
        var suspend = () => CreateService().SuspendAsync(
            WorkspaceA, AdminId, new SuspendAccountRequest(null, "low"), false, AdminId, TestContext.Current.CancellationToken);

        (await suspend.Should().ThrowAsync<ForbiddenException>()).Which.ErrorCode.Should().Be("cannot_suspend_self");
    }

    [Fact]
    public async Task A_workspace_cannot_suspend_its_admin_but_platform_staff_can()
    {
        var fromWorkspace = () => CreateService().SuspendAsync(
            WorkspaceA, AdminId, new SuspendAccountRequest(null, "low"), false, EmployeeId, TestContext.Current.CancellationToken);

        (await fromWorkspace.Should().ThrowAsync<ForbiddenException>()).Which.ErrorCode.Should().Be("cannot_suspend_admin");

        var row = await CreateService().SuspendAsync(
            WorkspaceA, AdminId, new SuspendAccountRequest(null, "high"), true, PlatformStaffId, TestContext.Current.CancellationToken);

        row.Status.Should().Be(EmployeeStatus.Suspended);
        row.Risk.Should().NotBeNull();

        // The admin is told too, and the rest of the workspace is untouched.
        _notified.Should().ContainSingle().Which.Kind.Should().Be(NotificationKind.SecurityAccountSuspended);
        _notified[0].ActionRoute.Should().Be("/settings/security");
        Get(EmployeeId).Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task Platform_staff_are_never_suspended_on_any_route()
    {
        var fromWorkspace = () => CreateService().SuspendAsync(
            WorkspaceA, PlatformStaffId, new SuspendAccountRequest(null, "low"), false, AdminId, TestContext.Current.CancellationToken);
        var fromPlatform = () => CreateService().SuspendAsync(
            WorkspaceA, PlatformStaffId, new SuspendAccountRequest(null, "low"), true, 999, TestContext.Current.CancellationToken);

        (await fromWorkspace.Should().ThrowAsync<ForbiddenException>()).Which.ErrorCode.Should().Be("cannot_suspend_platform_staff");
        (await fromPlatform.Should().ThrowAsync<ForbiddenException>()).Which.ErrorCode.Should().Be("cannot_suspend_platform_staff");
    }

    [Fact]
    public async Task Someone_from_another_workspace_is_not_found()
    {
        var suspend = () => CreateService().SuspendAsync(
            WorkspaceA, OtherWorkspaceEmployeeId, new SuspendAccountRequest(null, "low"), false, AdminId,
            TestContext.Current.CancellationToken);

        await suspend.Should().ThrowAsync<NotFoundException>();
        Get(OtherWorkspaceEmployeeId).Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task Reactivating_lets_them_sign_in_again_and_is_audited()
    {
        Get(EmployeeId).Status = UserStatus.Disabled;

        var row = await CreateService().ReactivateAsync(
            WorkspaceA, EmployeeId, false, AdminId, TestContext.Current.CancellationToken);

        row.Status.Should().Be(EmployeeStatus.Active);
        Get(EmployeeId).Status.Should().Be(UserStatus.Active);
        _audited.Should().ContainSingle().Which.EntityName.Should().Be("security.account.reactivated");
    }

    [Fact]
    public async Task The_overview_says_who_each_viewer_may_suspend()
    {
        var workspace = await CreateService().GetOrganizationAsync(
            WorkspaceA, includeRisk: false, AdminId, TestContext.Current.CancellationToken);

        workspace.Employees.Single(row => row.UserId == "emp_101").CanSuspend.Should().BeFalse(); // themselves
        workspace.Employees.Single(row => row.UserId == "emp_102").CanSuspend.Should().BeTrue();

        var platform = await CreateService().GetOrganizationAsync(
            WorkspaceA, includeRisk: true, PlatformStaffId, TestContext.Current.CancellationToken);

        platform.Employees.Should().OnlyContain(row => row.CanSuspend); // the admin included
    }

    [Fact]
    public void A_suspended_account_is_told_so_at_sign_in_and_refresh()
    {
        var ensure = typeof(AuthenticationService).GetMethod("EnsureAccountUsable", BindingFlags.NonPublic | BindingFlags.Static)!;
        var user = Get(EmployeeId);
        user.Status = UserStatus.Disabled;

        var check = () =>
        {
            try
            {
                ensure.Invoke(null, [user]);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        };

        var refusal = check.Should().Throw<AuthenticationException>().Which;

        refusal.ErrorCode.Should().Be("account_suspended");
        refusal.Message.Should().Contain("suspended").And.Contain("workspace admin");
    }
}

/// <summary>Session security never applies to platform staff.</summary>
public sealed class PlatformStaffSessionTests : IDisposable
{
    private readonly List<UserSession> _sessionRows = [];
    private readonly List<SecurityEvent> _eventRows = [];
    private readonly IRepository<UserSession> _sessions = Substitute.For<IRepository<UserSession>>();
    private readonly IRepository<SecurityEvent> _events = Substitute.For<IRepository<SecurityEvent>>();
    private readonly ISecurityAlertService _alerts = Substitute.For<ISecurityAlertService>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private readonly User _staff = new()
    {
        Id = 1,
        TenantId = null,
        Email = "superadmin@nextreach.io",
        NormalizedEmail = "SUPERADMIN@NEXTREACH.IO",
        DisplayName = "Platform",
        PasswordHash = "hash",
    };

    public PlatformStaffSessionTests()
    {
        _sessions.Query(Arg.Any<bool>()).Returns(_ => _sessionRows.AsQueryable());
        _events.Query(Arg.Any<bool>()).Returns(_ => _eventRows.AsQueryable());
        _sessions.When(repository => repository.Add(Arg.Any<UserSession>()))
            .Do(call => _sessionRows.Add(call.Arg<UserSession>()!));
        _events.When(repository => repository.Add(Arg.Any<SecurityEvent>()))
            .Do(call => _eventRows.Add(call.Arg<SecurityEvent>()!));

        // Signed in before, on a different machine: for anyone else, a new device and a displacement.
        _sessionRows.Add(new UserSession
        {
            Id = 1,
            UserId = 1,
            SessionId = Guid.NewGuid(),
            DeviceId = "other-machine",
            LastActivityAt = new DateTimeOffset(2026, 9, 18, 9, 0, 0, TimeSpan.Zero),
        });
    }

    public void Dispose() => _cache.Dispose();

    private SessionTracker CreateTracker() =>
        new(
            _sessions,
            _events,
            Substitute.For<IRefreshTokenRepository>(),
            new InMemoryQueryExecutor(),
            Substitute.For<IUnitOfWork>(),
            new StubRequestContext { DeviceId = "this-machine", UserAgent = "Mozilla/5.0 (Windows NT 10.0) Chrome/128.0" },
            _alerts,
            _cache,
            new FixedDateTimeProvider(new DateTimeOffset(2026, 9, 19, 9, 0, 0, TimeSpan.Zero)),
            Options.Create(new AuthenticationPolicyOptions()),
            NullLogger<SessionTracker>.Instance);

    [Fact]
    public async Task A_second_machine_is_recorded_for_their_own_device_list_and_nothing_else()
    {
        var tracker = CreateTracker();

        var facts = await tracker.StartAsync(_staff, Guid.NewGuid(), [], TestContext.Current.CancellationToken);

        _sessionRows.Should().HaveCount(2);
        _eventRows.Should().BeEmpty();
        facts.IsNewDevice.Should().BeFalse();

        await tracker.AnnounceAsync(_staff, facts, TestContext.Current.CancellationToken);
        await tracker.RecordFailedLoginAsync(_staff, TestContext.Current.CancellationToken);

        await _alerts.DidNotReceiveWithAnyArgs().AnnounceSignInAsync(default!, default!, TestContext.Current.CancellationToken);
        await _alerts.DidNotReceiveWithAnyArgs().AnnounceFailedLoginsAsync(default!, default, TestContext.Current.CancellationToken);
        _eventRows.Should().BeEmpty();
    }
}
