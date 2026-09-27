using AwesomeAssertions;
using Marketing.Application.DTOs.Platform;
using Marketing.Application.Services;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using NSubstitute;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// The platform audit log narrows the whole log, not the page on screen.
/// </summary>
/// <remarks>
/// The severity tabs used to filter in the browser, over the twenty-five rows it happened to hold,
/// while the total underneath went on describing thousands. Page two then showed a different
/// number of "warnings" than page one and neither matched the count. Every filter here is asserted
/// against <c>TotalItems</c> as well as the rows, because the count is the half that was wrong.
/// </remarks>
public sealed class PlatformAuditFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private const long Ayesha = 10;
    private const long Bilal = 20;
    private const long Workspace = 1;

    private readonly List<AuditLog> _entries = [];
    private readonly List<User> _people = [];

    public PlatformAuditFilterTests()
    {
        Person(Ayesha, "Ayesha Khan");
        Person(Bilal, "Bilal Rana");

        // Four entries: two deletions (warnings) and two routine changes (info), split across two
        // people and two days, so every filter has something to exclude.
        Entry(1, Ayesha, AuditAction.Deleted, "Contact", Now.AddHours(-1));
        Entry(2, Ayesha, AuditAction.Updated, "Contact", Now.AddHours(-2));
        Entry(3, Bilal, AuditAction.Deleted, "Campaign", Now.AddDays(-5));
        Entry(4, Bilal, AuditAction.Created, "Tag", Now.AddDays(-6));
    }

    [Fact]
    public async Task Warning_returns_only_deletions()
    {
        var page = await Read(new AuditLogQuery { Severity = AuditSeverity.Warning });

        page.TotalItems.Should().Be(2);
        page.Items.Should().OnlyContain(entry => entry.Severity == AuditSeverity.Warning);
    }

    [Fact]
    public async Task Info_returns_everything_that_is_not_a_deletion()
    {
        var page = await Read(new AuditLogQuery { Severity = AuditSeverity.Info });

        page.TotalItems.Should().Be(2);
        page.Items.Should().OnlyContain(entry => entry.Severity == AuditSeverity.Info);
    }

    [Fact]
    public async Task Critical_matches_nothing_because_no_action_derives_to_it()
    {
        // Deliberate. Severity is derived from the action and nothing derives to Critical yet, so
        // the honest answer is an empty page. Answering with the whole log - or with the warnings,
        // as "closest match" - would tell a reviewer these entries are the serious ones.
        var page = await Read(new AuditLogQuery { Severity = AuditSeverity.Critical });

        page.TotalItems.Should().Be(0);
        page.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task No_severity_returns_every_entry()
    {
        var page = await Read(new AuditLogQuery());

        page.TotalItems.Should().Be(4);
    }

    [Fact]
    public async Task Actor_and_severity_narrow_together()
    {
        var page = await Read(new AuditLogQuery { Actor = "Ayesha Khan", Severity = AuditSeverity.Warning });

        page.TotalItems.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Actor.Should().Be("Ayesha Khan");
    }

    [Fact]
    public async Task From_excludes_everything_older()
    {
        var page = await Read(new AuditLogQuery { From = Now.AddDays(-1) });

        page.TotalItems.Should().Be(2);
    }

    [Fact]
    public async Task Filters_apply_before_the_page_is_cut()
    {
        // The whole point: with a page of one, the rows and the total have to describe the
        // filtered log. A filter applied after the slice would report four.
        var page = await Read(new AuditLogQuery { Severity = AuditSeverity.Warning, PageSize = 1 });

        page.Items.Should().ContainSingle();
        page.TotalItems.Should().Be(2);
        page.TotalPages.Should().Be(2);
    }

    private Task<Common.Responses.PagedResult<AuditLogEntryResponse>> Read(AuditLogQuery query) =>
        CreateService().GetAuditLogAsync(query, CancellationToken.None);

    private void Person(long id, string name) =>
        _people.Add(new User
        {
            Id = id,
            TenantId = Workspace,
            Email = $"{id}@example.test",
            NormalizedEmail = $"{id}@EXAMPLE.TEST",
            DisplayName = name,
            PasswordHash = "hash",
            Status = UserStatus.Active,
        });

    private void Entry(long id, long userId, AuditAction action, string entity, DateTimeOffset when) =>
        _entries.Add(new AuditLog
        {
            Id = id,
            TenantId = Workspace,
            UserId = userId,
            EntityName = entity,
            EntityId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Action = action,
            OccurredOn = when,
        });

    private PlatformService CreateService()
    {
        var auditLogs = Substitute.For<IAuditLogRepository>();
        auditLogs.Query().Returns(_ => _entries.AsQueryable());

        var users = Substitute.For<IUserRepository>();
        users.Query(Arg.Any<bool>()).Returns(_ => _people.AsQueryable());

        var tenants = Substitute.For<IRepository<Tenant>>();
        tenants.Query(Arg.Any<bool>()).Returns(_ => new[]
        {
            new Tenant { Id = Workspace, Name = "Glow Studio", Slug = "glow", ContactEmail = "a@glow.test" },
        }.AsQueryable());

        return new PlatformService(
            tenants,
            users,
            Substitute.For<IRepository<Contact>>(),
            Substitute.For<IRepository<Campaign>>(),
            Substitute.For<IRepository<MessageDailyStat>>(),
            Substitute.For<IRepository<TenantSubscription>>(),
            Substitute.For<IRepository<SubscriptionPlan>>(),
            auditLogs,
            new InMemoryQueryExecutor(),
            new FixedDateTimeProvider(Now));
    }
}
