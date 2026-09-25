using AwesomeAssertions;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Services;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using NSubstitute;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Clearing notifications, and the one thing that must not happen while doing it.
/// </summary>
/// <remarks>
/// Deleting is per person. Most notifications are one person's row and deleting one is deleting
/// the row, but a workspace-wide notification is a single row the whole team reads - and treating
/// that the same way would clear a colleague's screen because somebody else was finished with it.
/// </remarks>
public sealed class NotificationDeleteTests
{
    private const long TenantId = 7001;
    private const long UserId = 71;
    private const long ColleagueId = 72;
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);

    private readonly List<Notification> _rows = [];
    private readonly List<NotificationDismissal> _dismissed = [];
    private readonly List<UserNotificationPreference> _stored = [];

    private readonly IRepository<Notification> _notifications = Substitute.For<IRepository<Notification>>();
    private readonly IRepository<UserNotificationPreference> _preferences =
        Substitute.For<IRepository<UserNotificationPreference>>();
    private readonly IRepository<NotificationDismissal> _dismissals =
        Substitute.For<IRepository<NotificationDismissal>>();
    private readonly InMemoryQueryExecutor _queries = new();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public NotificationDeleteTests()
    {
        // The live queries never see soft-deleted rows, because a global filter removes them. The
        // substitute has to behave the same way or every assertion below would pass by accident.
        _notifications.Query(Arg.Any<bool>()).Returns(_ => _rows.Where(row => !row.IsDeleted).AsQueryable());
        _preferences.Query(Arg.Any<bool>()).Returns(_ => _stored.AsQueryable());
        _dismissals.Query(Arg.Any<bool>()).Returns(_ => _dismissed.Where(row => !row.IsDeleted).AsQueryable());

        _notifications.When(repository => repository.Remove(Arg.Any<Notification>()))
            .Do(call => call.Arg<Notification>()!.IsDeleted = true);

        _dismissals.When(repository => repository.Add(Arg.Any<NotificationDismissal>()))
            .Do(call => _dismissed.Add(call.Arg<NotificationDismissal>()!));
    }

    private NotificationService CreateService(long userId = UserId) =>
        new(
            _notifications,
            _preferences,
            _dismissals,
            _queries,
            _unitOfWork,
            new StubCurrentUser { UserId = userId },
            new StubTenantContext { TenantId = TenantId });

    private Notification Raised(long? userId = UserId, bool read = false, NotificationKind kind = NotificationKind.CampaignFailed)
    {
        var notification = new Notification
        {
            Id = _rows.Count + 1,
            TenantId = TenantId,
            UserId = userId,
            Kind = kind,
            Title = kind.ToString(),
            Body = "Something happened.",
            Priority = NotificationPriority.Info,
            Icon = "bell",
            Read = read,
            OccurredOn = Now.AddMinutes(_rows.Count),
        };

        _rows.Add(notification);

        return notification;
    }

    private static string Public(Notification notification) =>
        PublicId.From(PublicId.Notification, notification.Id);

    private async Task<IReadOnlyList<AppNotification>> FeedOf(long userId) =>
        await CreateService(userId).GetAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Deleting_my_own_notification_takes_it_out_of_my_list()
    {
        var mine = Raised();
        Raised();

        var result = await CreateService().DeleteAsync(Public(mine), TestContext.Current.CancellationToken);

        result.Deleted.Should().Be(1);
        (await FeedOf(UserId)).Should().ContainSingle().Which.Id.Should().NotBe(Public(mine));
    }

    [Fact]
    public async Task Deleting_the_same_notification_twice_is_not_an_error()
    {
        var mine = Raised();

        var service = CreateService();

        await service.DeleteAsync(Public(mine), TestContext.Current.CancellationToken);

        // Two tabs, or a retry. The row is gone either way, which is what the caller asked for -
        // and a refusal the second time would put the row back on a client that handles errors by
        // rolling its optimistic delete back.
        var again = await service.DeleteAsync(Public(mine), TestContext.Current.CancellationToken);

        again.Deleted.Should().Be(0);
    }

    [Fact]
    public async Task An_identifier_that_cannot_be_read_answers_zero_rather_than_refusing()
    {
        var service = CreateService();

        (await service.DeleteAsync("cmp_18", TestContext.Current.CancellationToken)).Deleted.Should().Be(0);
        (await service.DeleteAsync("nonsense", TestContext.Current.CancellationToken)).Deleted.Should().Be(0);
    }

    [Fact]
    public async Task A_colleagues_notification_is_not_mine_to_delete()
    {
        var theirs = Raised(userId: ColleagueId);

        var result = await CreateService().DeleteAsync(Public(theirs), TestContext.Current.CancellationToken);

        // Zero, and not a 403: a refusal that distinguished "not yours" from "already gone" would
        // confirm that the identifier names something real.
        result.Deleted.Should().Be(0);
        (await FeedOf(ColleagueId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Clearing_a_workspace_wide_notification_clears_it_for_me_alone()
    {
        var shared = Raised(userId: null);

        var result = await CreateService().DeleteAsync(Public(shared), TestContext.Current.CancellationToken);

        result.Deleted.Should().Be(1);

        // One row, several readers. Deleting it would have taken a plan change off the whole
        // team's screens because one person had finished reading it.
        _rows.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
        (await FeedOf(UserId)).Should().BeEmpty();
        (await FeedOf(ColleagueId)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_dismissed_workspace_notification_is_not_dismissed_twice()
    {
        var shared = Raised(userId: null);

        var service = CreateService();

        await service.DeleteAsync(Public(shared), TestContext.Current.CancellationToken);
        var again = await service.DeleteAsync(Public(shared), TestContext.Current.CancellationToken);

        // A second row would break the unique index, and would make the count wrong on a bulk
        // delete that happened to include it.
        again.Deleted.Should().Be(0);
        _dismissed.Should().ContainSingle();
    }

    [Fact]
    public async Task The_ticked_rows_are_the_ones_that_go()
    {
        var first = Raised();
        var second = Raised();
        var third = Raised();

        var result = await CreateService().DeleteManyAsync(
            new DeleteNotificationsRequest([Public(first), Public(third)], null),
            TestContext.Current.CancellationToken);

        result.Deleted.Should().Be(2);
        (await FeedOf(UserId)).Should().ContainSingle().Which.Id.Should().Be(Public(second));
    }

    [Fact]
    public async Task Nine_good_identifiers_and_one_stale_one_clear_nine()
    {
        var first = Raised();
        var second = Raised();

        var result = await CreateService().DeleteManyAsync(
            new DeleteNotificationsRequest([Public(first), "ntf_999999", Public(second)], null),
            TestContext.Current.CancellationToken);

        result.Deleted.Should().Be(2);
        (await FeedOf(UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_all_clears_everything_the_caller_has_and_nothing_anybody_else_has()
    {
        Raised();
        Raised(read: true);
        Raised(userId: null);
        Raised(userId: ColleagueId);

        var result = await CreateService().DeleteManyAsync(
            new DeleteNotificationsRequest(null, NotificationDeleteScope.All),
            TestContext.Current.CancellationToken);

        result.Deleted.Should().Be(3);
        (await FeedOf(UserId)).Should().BeEmpty();

        // The colleague keeps their own row and the workspace-wide one.
        (await FeedOf(ColleagueId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_read_leaves_the_unread_alone()
    {
        Raised(read: true);
        var unread = Raised();
        Raised(read: true);

        var result = await CreateService().DeleteManyAsync(
            new DeleteNotificationsRequest(null, NotificationDeleteScope.Read),
            TestContext.Current.CancellationToken);

        result.Deleted.Should().Be(2);
        (await FeedOf(UserId)).Should().ContainSingle().Which.Id.Should().Be(Public(unread));
    }

    [Fact]
    public async Task A_scope_covers_what_arrived_after_the_page_loaded()
    {
        // The client can only ever name the rows it is holding. Evaluated here, "delete all" also
        // takes the one raised thirty seconds ago - which would otherwise reappear on the next
        // refresh and read as a broken delete rather than as new mail.
        Raised();
        Raised();

        var result = await CreateService().DeleteManyAsync(
            new DeleteNotificationsRequest(null, NotificationDeleteScope.All),
            TestContext.Current.CancellationToken);

        result.Deleted.Should().Be(2);
    }

    [Fact]
    public async Task A_silenced_category_is_not_swept_up_by_delete_all()
    {
        Raised(kind: NotificationKind.InboxMessageReceived);
        Raised(kind: NotificationKind.CampaignFailed);

        _stored.Add(new UserNotificationPreference
        {
            Id = 1,
            UserId = UserId,
            Category = NotificationCategory.Messages,
            Enabled = false,
        });

        var result = await CreateService().DeleteManyAsync(
            new DeleteNotificationsRequest(null, NotificationDeleteScope.All),
            TestContext.Current.CancellationToken);

        // A scope clears the list in front of the person. A category switched off for a month
        // should still have its history when it is switched back on.
        result.Deleted.Should().Be(1);
        _rows.Should().Contain(row => row.Kind == NotificationKind.InboxMessageReceived && !row.IsDeleted);
    }

    [Fact]
    public async Task Asking_for_both_or_neither_is_refused()
    {
        var service = CreateService();

        var both = async () => await service.DeleteManyAsync(
            new DeleteNotificationsRequest(["ntf_1"], NotificationDeleteScope.All),
            TestContext.Current.CancellationToken);

        var neither = async () => await service.DeleteManyAsync(
            new DeleteNotificationsRequest(null, null),
            TestContext.Current.CancellationToken);

        // A scope that disagrees with the list has no obvious winner, and an empty request is not
        // an instruction to clear everything.
        await both.Should().ThrowAsync<RequestRejectedException>();
        await neither.Should().ThrowAsync<RequestRejectedException>();
    }

    [Fact]
    public async Task An_empty_list_of_identifiers_clears_nothing_and_says_so()
    {
        Raised();

        var result = await CreateService().DeleteManyAsync(
            new DeleteNotificationsRequest([], null),
            TestContext.Current.CancellationToken);

        // "ids present but empty" is a request to delete these zero rows, which is not the same
        // mistake as sending neither field.
        result.Deleted.Should().Be(0);
        (await FeedOf(UserId)).Should().ContainSingle();
    }

    [Fact]
    public async Task More_identifiers_than_any_screen_can_select_is_refused()
    {
        var tooMany = Enumerable.Range(1, DeleteNotificationsRequest.MaxIds + 1)
            .Select(index => $"ntf_{index}")
            .ToList();

        var call = async () => await CreateService().DeleteManyAsync(
            new DeleteNotificationsRequest(tooMany, null),
            TestContext.Current.CancellationToken);

        // A bound on the IN clause, not a limit anybody will meet by hand: past this, the request
        // is a scope in disguise.
        await call.Should().ThrowAsync<RequestRejectedException>();
    }

    [Fact]
    public async Task A_deleted_notification_stops_counting_towards_the_bell()
    {
        Raised();
        Raised();
        var read = Raised(read: true);

        await CreateService().DeleteManyAsync(
            new DeleteNotificationsRequest([Public(read)], null),
            TestContext.Current.CancellationToken);

        var page = await CreateService().GetPageAsync(
            new NotificationQuery { Page = 1, PageSize = 20 }, TestContext.Current.CancellationToken);

        page.TotalItems.Should().Be(2);
        page.UnreadCount.Should().Be(2);
    }

    [Fact]
    public async Task Marking_all_read_does_not_resurrect_a_dismissed_workspace_notification()
    {
        var shared = Raised(userId: null);

        var service = CreateService();

        await service.DeleteAsync(Public(shared), TestContext.Current.CancellationToken);
        await service.MarkAllReadAsync(TestContext.Current.CancellationToken);

        // Every route that touches the feed goes through the same scoping, so a dismissal holds
        // everywhere rather than only on the read it was written for.
        (await FeedOf(UserId)).Should().BeEmpty();
        _rows.Should().ContainSingle().Which.Read.Should().BeFalse("it is not in this caller's feed to be read");
    }
}
