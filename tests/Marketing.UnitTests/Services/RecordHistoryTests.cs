using System.Text.Json;
using AwesomeAssertions;
using Marketing.Application.DTOs.Audit;
using Marketing.Application.Services.Audit;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using NSubstitute;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.UnitTests.Services;

/// <summary>
/// The registry that decides which records have a history and who may read it.
/// </summary>
public sealed class AuditableEntityRegistryTests
{
    [Fact]
    public void A_name_is_matched_however_it_is_capitalised()
    {
        AuditableEntities.Find("template")!.EntityName.Should().Be(nameof(MessageTemplate));
        AuditableEntities.Find("TEMPLATE")!.EntityName.Should().Be(nameof(MessageTemplate));
    }

    [Fact]
    public void An_unregistered_name_is_nothing_rather_than_something_empty()
    {
        // A typo in a client template should be loud. "No history" is a different answer and would
        // hide the mistake for months.
        AuditableEntities.Find("Departmnet").Should().BeNull();
        AuditableEntities.Find(null).Should().BeNull();
    }

    [Fact]
    public void Every_registered_record_can_be_addressed_somehow_and_can_be_denied()
    {
        foreach (var entry in AuditableEntities.All)
        {
            entry.EntityName.Should().NotBeNullOrWhiteSpace();

            // Three ways of naming a record, and every entry has to offer one of them: a public
            // id, "current" for the one-per-workspace records, or a business key.
            var addressable = !string.IsNullOrWhiteSpace(entry.IdPrefix)
                              || entry.SingleRowPerTenant
                              || !string.IsNullOrWhiteSpace(entry.KeyColumn);

            addressable.Should().BeTrue($"{entry.PublicName} has to be addressable");

            // Null would mean "any signed-in member of the workspace", which none of these are.
            entry.Permission.Should().NotBeNullOrWhiteSpace(
                "every record in the registry today is one somebody can be denied");
        }
    }
}

/// <summary>
/// Reading one record's history: what comes back, in what order, and who is allowed to ask.
/// </summary>
/// <remarks>
/// Authorisation is the part worth the most attention. It is about the record, never about the
/// audit rows - a caller who cannot open the record cannot read what happened to it, and a record
/// in another workspace does not exist as far as this endpoint is concerned.
/// </remarks>
public sealed class RecordHistoryTests
{
    private const long TemplateId = 18;
    private const long AuthorId = 4;
    private const long FormerColleagueId = 5;
    private static readonly DateTimeOffset Noon = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly List<AuditLog> _entries = [];
    private readonly List<User> _people = [];
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IAuditableRecordLocator _records = Substitute.For<IAuditableRecordLocator>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly InMemoryQueryExecutor _queries = new();
    private readonly StubCurrentUser _caller = new()
    {
        UserId = AuthorId,
        Permissions = [Permissions.WhatsApp.TemplatesView],
    };

    public RecordHistoryTests()
    {
        _people.Add(Person(AuthorId, "John Rivera"));
        _people.Add(Person(FormerColleagueId, "Ayesha Khan"));
        _people.Add(Person(Platform.SystemUserId, Platform.SystemDisplayName));

        _audit.Query().Returns(_ => _entries.AsQueryable());
        _users.Query(Arg.Any<bool>()).Returns(_ => _people.AsQueryable());

        _records.ExistsAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private static User Person(long id, string name) => new()
    {
        Id = id,
        DisplayName = name,
        Email = $"{id}@example.test",
        NormalizedEmail = $"{id}@EXAMPLE.TEST",
        PasswordHash = "hash",
    };

    private AuditLog Entry(
        AuditAction action = AuditAction.Updated,
        long userId = AuthorId,
        int minutesAgo = 0,
        string changes = """{"Name":{"old":"Template A","new":"Template B"}}""",
        string entityName = nameof(MessageTemplate),
        long entityId = TemplateId)
    {
        var entry = new AuditLog
        {
            Id = _entries.Count + 10_000,
            TenantId = 15,
            UserId = userId,
            EntityName = entityName,
            EntityId = entityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Action = action,
            Changes = changes,
            OccurredOn = Noon.AddMinutes(-minutesAgo),
        };

        _entries.Add(entry);

        return entry;
    }

    private RecordHistoryService CreateService(long? tenantId = 15) =>
        new(_audit, _records, _users, _queries, _caller, new StubTenantContext { TenantId = tenantId });

    private static JsonElement Changes(RecordHistoryEntry entry) =>
        (JsonElement)entry.Changes;

    [Fact]
    public async Task A_records_history_comes_back_newest_first_with_names_on_it()
    {
        Entry(AuditAction.Created, minutesAgo: 90, changes: """{"Name":{"new":"Template A"}}""");
        Entry(AuditAction.Updated, userId: FormerColleagueId, minutesAgo: 5);

        var page = await CreateService().GetAsync(
            "Template", "tpl_18", new RecordHistoryQuery(), TestContext.Current.CancellationToken);

        page.TotalItems.Should().Be(2);

        var newest = page.Items[0];

        newest.Action.Should().Be(AuditAction.Updated);
        newest.UserId.Should().Be("emp_5");
        newest.UserName.Should().Be("Ayesha Khan");
        newest.EntityName.Should().Be("Template");
        newest.EntityId.Should().Be("tpl_18");

        Changes(newest).GetProperty("Name").GetProperty("old").GetString().Should().Be("Template A");
        Changes(newest).GetProperty("Name").GetProperty("new").GetString().Should().Be("Template B");

        // A create carries no "old", and nothing fills one in on the way out.
        Changes(page.Items[1]).GetProperty("Name").TryGetProperty("old", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Another_records_history_is_not_mixed_in()
    {
        Entry();
        Entry(entityId: 19);
        Entry(entityName: nameof(Contact));

        var page = await CreateService().GetAsync(
            "Template", "tpl_18", new RecordHistoryQuery(), TestContext.Current.CancellationToken);

        page.TotalItems.Should().Be(1);
    }

    [Fact]
    public async Task A_change_by_a_background_job_has_no_name_to_show()
    {
        Entry(userId: Platform.SystemUserId);

        var entry = (await CreateService().GetAsync(
            "Template", "tpl_18", new RecordHistoryQuery(), TestContext.Current.CancellationToken)).Items[0];

        // Not "System": the client shows "Automatic", and a null actor is what tells it to.
        entry.UserId.Should().BeNull();
        entry.UserName.Should().BeNull();
    }

    [Fact]
    public async Task Somebody_who_has_left_still_has_their_name_on_what_they_did()
    {
        Entry(userId: FormerColleagueId);

        _people.Single(person => person.Id == FormerColleagueId).IsDeleted = true;

        var entry = (await CreateService().GetAsync(
            "Template", "tpl_18", new RecordHistoryQuery(), TestContext.Current.CancellationToken)).Items[0];

        // History that reads "by ?" once someone leaves is worse than useless.
        entry.UserName.Should().Be("Ayesha Khan");
    }

    [Fact]
    public async Task Each_filter_narrows_the_total_as_well_as_the_page()
    {
        Entry(AuditAction.Created, minutesAgo: 60 * 24 * 3);
        Entry(AuditAction.Updated, userId: FormerColleagueId);
        Entry(AuditAction.Deleted);

        var service = CreateService();

        var updated = await service.GetAsync(
            "Template", "tpl_18",
            new RecordHistoryQuery { Action = AuditAction.Updated },
            TestContext.Current.CancellationToken);

        updated.TotalItems.Should().Be(1);
        updated.Items[0].UserName.Should().Be("Ayesha Khan");

        var mine = await service.GetAsync(
            "Template", "tpl_18",
            new RecordHistoryQuery { UserId = "emp_4" },
            TestContext.Current.CancellationToken);

        mine.TotalItems.Should().Be(2);

        var today = await service.GetAsync(
            "Template", "tpl_18",
            new RecordHistoryQuery { From = DateOnly.FromDateTime(Noon.UtcDateTime) },
            TestContext.Current.CancellationToken);

        today.TotalItems.Should().Be(2, "the create was three days ago");
    }

    [Fact]
    public async Task The_closing_day_of_a_range_is_included_whole()
    {
        Entry(minutesAgo: 30);

        var page = await CreateService().GetAsync(
            "Template", "tpl_18",
            new RecordHistoryQuery
            {
                From = DateOnly.FromDateTime(Noon.UtcDateTime),
                To = DateOnly.FromDateTime(Noon.UtcDateTime),
            },
            TestContext.Current.CancellationToken);

        // A range that stopped at midnight on the closing day would hide everything that happened
        // during it, which reads as data loss rather than as a filter.
        page.TotalItems.Should().Be(1);
    }

    [Fact]
    public async Task A_page_is_ten_entries_and_never_more_than_fifty()
    {
        for (var index = 0; index < 60; index++)
        {
            Entry(minutesAgo: index);
        }

        var service = CreateService();

        var first = await service.GetAsync(
            "Template", "tpl_18", new RecordHistoryQuery(), TestContext.Current.CancellationToken);

        first.Items.Should().HaveCount(10);
        first.TotalItems.Should().Be(60);

        var big = await service.GetAsync(
            "Template", "tpl_18",
            new RecordHistoryQuery { PageSize = 100 },
            TestContext.Current.CancellationToken);

        // A change set is unbounded in width, so this endpoint's ceiling is lower than the
        // platform's usual hundred.
        big.Items.Should().HaveCount(RecordHistoryQuery.PanelMaxPageSize);
        big.PageSize.Should().Be(RecordHistoryQuery.PanelMaxPageSize);
    }

    [Fact]
    public async Task Platform_staff_outside_every_workspace_read_across_them()
    {
        Entry();

        _caller.Roles = [Roles.SuperAdmin];
        _caller.Permissions = [];

        var page = await CreateService(tenantId: null).GetAsync(
            "Template", "tpl_18", new RecordHistoryQuery(), TestContext.Current.CancellationToken);

        page.TotalItems.Should().Be(1);

        // A tenant-scoped existence check has no tenant to be scoped to when the caller is outside
        // every workspace, and would have thrown rather than answered.
        await _records.Received().ExistsAsync(
            nameof(MessageTemplate), TemplateId, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_one_per_workspace_record_is_asked_for_as_current()
    {
        Entry(entityName: nameof(AutoReplySettings), entityId: 77);

        _caller.Permissions = [Permissions.Ai.AutoReplyManage];
        _records.FindForTenantAsync(nameof(AutoReplySettings), Arg.Any<CancellationToken>()).Returns(77L);

        var page = await CreateService().GetAsync(
            "AutoReplySettings", "current", new RecordHistoryQuery(), TestContext.Current.CancellationToken);

        // A public id for a row a workspace can only have one of tells nobody anything, so the
        // client names it the way it reads in a URL.
        page.TotalItems.Should().Be(1);
        page.Items[0].EntityId.Should().Be("current");
    }

    [Fact]
    public async Task A_workspace_that_has_never_saved_its_settings_has_no_history_to_read()
    {
        _caller.Permissions = [Permissions.Ai.AutoReplyManage];
        _records.FindForTenantAsync(nameof(AutoReplySettings), Arg.Any<CancellationToken>())
            .Returns((long?)null);

        var read = () => CreateService().GetAsync(
            "AutoReplySettings", "current", new RecordHistoryQuery(), TestContext.Current.CancellationToken);

        // The row is written the first time somebody presses Save. Before that there is nothing to
        // have a history, which is a 404 rather than an empty page.
        await read.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task An_email_template_is_asked_for_by_the_key_the_rest_of_the_api_uses()
    {
        Entry(entityName: nameof(EmailTemplate), entityId: 4);

        _caller.Roles = [Roles.SuperAdmin];
        _records.FindByKeyAsync(
                nameof(EmailTemplate), "key", "auth.invitation", true, Arg.Any<CancellationToken>())
            .Returns(4L);

        var page = await CreateService(tenantId: null).GetAsync(
            "EmailTemplate", "auth.invitation", new RecordHistoryQuery(), TestContext.Current.CancellationToken);

        // /superadmin/email-templates/{key} everywhere else, so history is asked for the same way
        // rather than by a number the client has never been given.
        page.TotalItems.Should().Be(1);
    }

    [Fact]
    public async Task A_workspace_reads_its_own_history_and_nobody_else_s()
    {
        Entry(entityName: nameof(Tenant), entityId: 15);

        _caller.Permissions = [Permissions.Settings.Company];

        var service = CreateService();

        // Either way of naming it: "current", or the workspace's own id.
        (await service.GetAsync("Workspace", "current", new RecordHistoryQuery(), TestContext.Current.CancellationToken))
            .TotalItems.Should().Be(1);

        (await service.GetAsync("Workspace", "tnt_15", new RecordHistoryQuery(), TestContext.Current.CancellationToken))
            .TotalItems.Should().Be(1);

        // Another workspace's id is not theirs to ask about - and a tenant row has no tenant column
        // to be scoped by, so this is the check that does it.
        var other = () => service.GetAsync(
            "Workspace", "tnt_16", new RecordHistoryQuery(), TestContext.Current.CancellationToken);

        await other.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_whatsapp_number_history_needs_the_connect_permission()
    {
        Entry(entityName: nameof(WhatsAppConnection), entityId: 1);

        _caller.Permissions = [Permissions.WhatsApp.InboxView];

        var read = () => CreateService().GetAsync(
            "WhatsAppAccount", "wa_1", new RecordHistoryQuery(), TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<ForbiddenException>();

        _caller.Permissions = [Permissions.WhatsApp.Connect];

        (await CreateService().GetAsync(
            "WhatsAppAccount", "wa_1", new RecordHistoryQuery(), TestContext.Current.CancellationToken))
            .TotalItems.Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_record_type_is_a_404_rather_than_an_empty_page()
    {
        var read = () => CreateService().GetAsync(
            "Departmnet", "dep_1", new RecordHistoryQuery(), TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task An_id_of_the_wrong_kind_is_refused()
    {
        // cnt_18 is a contact. Accepting it would read a contact's history under a template's
        // permission check.
        var read = () => CreateService().GetAsync(
            "Template", "cnt_18", new RecordHistoryQuery(), TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Someone_without_the_records_permission_is_refused()
    {
        _caller.Permissions = [Permissions.Contacts.View];

        var read = () => CreateService().GetAsync(
            "Template", "tpl_18", new RecordHistoryQuery(), TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task A_record_in_another_workspace_does_not_exist()
    {
        Entry();

        _records.ExistsAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var read = () => CreateService().GetAsync(
            "Template", "tpl_18", new RecordHistoryQuery(), TestContext.Current.CancellationToken);

        // Checked against the record, not against the audit row's own tenant id - which is the
        // caller's own claim about which workspace they were in when they wrote it.
        await read.Should().ThrowAsync<NotFoundException>();
    }
}
