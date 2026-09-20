using AwesomeAssertions;
using Marketing.Application.Configurations;
using Marketing.Application.DTOs.Security;
using Marketing.Application.Services.Security;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;
using RoleCatalog = Marketing.Common.Constants.Roles;

namespace Marketing.UnitTests.Services;

/// <summary>
/// The platform security screen in one request rather than one per customer.
/// </summary>
/// <remarks>
/// The screen used to list the administrators and then ask about each workspace in turn, so drawing
/// it cost a request per customer and its "riskiest first" ordering only held within the page the
/// browser happened to have. Ordering across every workspace is the part worth testing: it is what
/// makes page one the page worth reading.
/// </remarks>
public sealed class PlatformSecuritySummaryTests
{
    private const long Calm = 1;
    private const long Busy = 2;
    private const long Shared = 3;

    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly List<User> _people = [];
    private readonly List<UserSession> _sessions = [];
    private readonly List<SecurityEvent> _events = [];
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();

    public PlatformSecuritySummaryTests()
    {
        var employee = new Role { Id = 2, Name = RoleCatalog.Employee, NormalizedName = "EMPLOYEE", Description = string.Empty };
        var staff = new Role { Id = 3, Name = RoleCatalog.SuperAdmin, NormalizedName = "SUPERADMIN", Description = string.Empty };

        // One quiet workspace, one with a person on too many devices, one where a login is plainly
        // being passed around: six displacements in a day is the heaviest signal there is.
        Add(10, Calm, "Quiet Person", employee);
        Add(20, Busy, "Busy Person", employee);
        Add(30, Shared, "Shared Login", employee);
        Add(99, null, "Platform Staff", staff);

        Session(10, Calm, "laptop");
        Session(20, Busy, "laptop");
        Session(20, Busy, "phone");
        Session(20, Busy, "tablet");
        Session(20, Busy, "desktop");
        // Two cities in a week on top of the displacements: together that is a high score, which
        // is the point - no single signal condemns an account on its own.
        Session(30, Shared, "laptop", "Lahore, PK");
        Session(30, Shared, "cafe", "Karachi, PK");

        for (var index = 0; index < 6; index++)
        {
            _events.Add(new SecurityEvent
            {
                Id = 500 + index,
                TenantId = Shared,
                UserId = 30,
                Kind = SecurityEventKind.SessionDisplaced,
                Detail = "Signed in elsewhere.",
                OccurredAt = Now.AddHours(-index),
            });
        }

        _users.Query(Arg.Any<bool>()).Returns(_ => _people.AsQueryable());
    }

    private void Add(long id, long? tenantId, string name, Role role)
    {
        var user = new User
        {
            Id = id,
            TenantId = tenantId,
            Email = $"{id}@example.test",
            NormalizedEmail = $"{id}@EXAMPLE.TEST",
            DisplayName = name,
            PasswordHash = "hash",
            Status = UserStatus.Active,
        };

        user.UserRoles.Add(new UserRole { UserId = id, RoleId = role.Id, Role = role, User = user });
        _people.Add(user);
    }

    private void Session(long userId, long tenantId, string device, string? location = null) =>
        _sessions.Add(new UserSession
        {
            Id = _sessions.Count + 1,
            TenantId = tenantId,
            UserId = userId,
            SessionId = Guid.NewGuid(),
            DeviceId = device,
            DeviceLabel = device,
            Location = location,
            LastActivityAt = Now.AddMinutes(-1),
        });

    private SecurityOverviewService CreateService()
    {
        var policy = Options.Create(new AuthenticationPolicyOptions());
        var queries = new InMemoryQueryExecutor();

        var sessions = Substitute.For<IRepository<UserSession>>();
        sessions.Query(Arg.Any<bool>()).Returns(_ => _sessions.AsQueryable());

        var events = Substitute.For<IRepository<SecurityEvent>>();
        events.Query(Arg.Any<bool>()).Returns(_ => _events.AsQueryable());

        var tenants = Substitute.For<IRepository<Tenant>>();
        tenants.Query(Arg.Any<bool>()).Returns(_ => new[]
        {
            new Tenant { Id = Calm, Name = "Quiet Studio", Slug = "quiet", ContactEmail = "a@quiet.test" },
            new Tenant { Id = Busy, Name = "Busy Agency", Slug = "busy", ContactEmail = "b@busy.test" },
            new Tenant { Id = Shared, Name = "Shared Shop", Slug = "shared", ContactEmail = "c@shared.test" },
        }.AsQueryable());

        var subscriptions = Substitute.For<IRepository<TenantSubscription>>();
        subscriptions.Query(Arg.Any<bool>()).Returns(_ => Enumerable.Empty<TenantSubscription>().AsQueryable());

        return new SecurityOverviewService(
            sessions,
            events,
            tenants,
            subscriptions,
            _users,
            Substitute.For<ISessionTracker>(),
            new AccountRiskEvaluator(sessions, events, queries, new FixedDateTimeProvider(Now), policy),
            queries,
            new FixedDateTimeProvider(Now),
            policy,
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IAuditLogRepository>(),
            Substitute.For<IRepository<Notification>>(),
            new StubRequestContext());
    }

    [Fact]
    public async Task The_riskiest_workspace_is_first_whatever_it_is_called()
    {
        var page = await CreateService().GetPlatformSummaryAsync(
            new SecuritySummaryQuery(), TestContext.Current.CancellationToken);

        page.TotalItems.Should().Be(3);

        // Alphabetically "Busy Agency" would lead. It does not, because the ordering is by risk.
        page.Items[0].OrganizationName.Should().Be("Shared Shop");
        page.Items[0].HighRisk.Should().Be(1);
        page.Items[0].NeedsAttention.Should().Be(1);

        page.Items[^1].OrganizationName.Should().Be("Quiet Studio");
        page.Items[^1].HighRisk.Should().Be(0);
        page.Items[^1].MediumRisk.Should().Be(0);
    }

    [Fact]
    public async Task Someone_over_the_device_limit_needs_attention_without_being_high_risk()
    {
        var page = await CreateService().GetPlatformSummaryAsync(
            new SecuritySummaryQuery(), TestContext.Current.CancellationToken);

        var busy = page.Items.Single(row => row.OrganizationName == "Busy Agency");

        // Four devices against a limit of three: worth a look, not worth an accusation.
        busy.NeedsAttention.Should().Be(1);
        busy.HighRisk.Should().Be(0);
        busy.People.Should().Be(1);
        busy.ActiveSessions.Should().Be(4);
    }

    [Fact]
    public async Task Platform_staff_are_not_counted_into_anybody_s_workspace()
    {
        var page = await CreateService().GetPlatformSummaryAsync(
            new SecuritySummaryQuery(), TestContext.Current.CancellationToken);

        // The Super Admin row carries no tenant, and the checks exempt platform staff everywhere
        // else; counting them here would put our own sign-ins into a customer's numbers.
        page.Items.Sum(row => row.People).Should().Be(3);
    }

    [Fact]
    public async Task A_second_page_continues_the_same_ordering()
    {
        var service = CreateService();

        var first = await service.GetPlatformSummaryAsync(
            new SecuritySummaryQuery { Page = 1, PageSize = 2 }, TestContext.Current.CancellationToken);

        var second = await service.GetPlatformSummaryAsync(
            new SecuritySummaryQuery { Page = 2, PageSize = 2 }, TestContext.Current.CancellationToken);

        first.Items.Should().HaveCount(2);
        second.Items.Should().ContainSingle();
        second.TotalItems.Should().Be(3);
        second.Items[0].OrganizationName.Should().Be("Quiet Studio");
    }

    [Fact]
    public async Task Risk_is_read_in_a_fixed_number_of_queries_however_many_people_there_are()
    {
        var queries = new InMemoryQueryExecutor();
        var policy = Options.Create(new AuthenticationPolicyOptions());

        var sessions = Substitute.For<IRepository<UserSession>>();
        sessions.Query(Arg.Any<bool>()).Returns(_ => _sessions.AsQueryable());

        var events = Substitute.For<IRepository<SecurityEvent>>();
        events.Query(Arg.Any<bool>()).Returns(_ => _events.AsQueryable());

        var evaluator = new AccountRiskEvaluator(sessions, events, queries, new FixedDateTimeProvider(Now), policy);

        var assessed = await evaluator.EvaluateTenantsAsync([Calm, Busy, Shared], TestContext.Current.CancellationToken);

        // Two reads for the whole platform. The per-account method costs two each, which is what
        // made the old screen one request per customer.
        queries.Queries.Should().Be(2);
        assessed.Should().HaveCount(3);
        assessed[30].Assessment.Level.Should().Be(RiskLevel.High);
        assessed[10].Assessment.Level.Should().Be(RiskLevel.Low);
    }
}
