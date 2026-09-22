using AwesomeAssertions;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Services;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.DataAccess.Entities;
using NSubstitute;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Which group each kind of notification belongs to.
/// </summary>
/// <remarks>
/// The client can infer this from the kind's prefix, and did. The mapping lives here so the two
/// sides cannot disagree, and so a kind whose name does not follow the convention is still filed
/// where a person would expect to find it.
/// </remarks>
public sealed class NotificationCategoryTests
{
    [Theory]
    [InlineData(NotificationKind.InboxMessageReceived, NotificationCategory.Messages)]
    [InlineData(NotificationKind.CampaignFailed, NotificationCategory.Campaigns)]
    [InlineData(NotificationKind.EmployeeInvited, NotificationCategory.Team)]
    [InlineData(NotificationKind.PaymentApproved, NotificationCategory.Billing)]
    [InlineData(NotificationKind.SubscriptionExpiring, NotificationCategory.Billing)]
    [InlineData(NotificationKind.SecurityNewLogin, NotificationCategory.Security)]
    [InlineData(NotificationKind.MetaDisconnected, NotificationCategory.System)]
    [InlineData(NotificationKind.AiRepliesExhausted, NotificationCategory.System)]
    public void Each_kind_is_filed_where_a_person_would_look_for_it(
        NotificationKind kind,
        NotificationCategory category)
    {
        NotificationCategories.Of(kind).Should().Be(category);
    }

    [Fact]
    public void Every_kind_has_a_category()
    {
        // Not an assertion about the mapping so much as about the fallback: whatever is added next
        // lands somewhere, rather than throwing on the first notification of a new kind.
        foreach (var kind in Enum.GetValues<NotificationKind>())
        {
            NotificationCategories.All.Should().Contain(NotificationCategories.Of(kind));
        }
    }

    [Theory]
    [InlineData(NotificationCategory.Messages, true)]
    [InlineData(NotificationCategory.Campaigns, true)]
    [InlineData(NotificationCategory.Team, true)]
    [InlineData(NotificationCategory.Billing, true)]
    [InlineData(NotificationCategory.Security, false)]
    [InlineData(NotificationCategory.System, false)]
    public void Only_four_of_the_six_may_be_switched_off(NotificationCategory category, bool silenceable)
    {
        // A stolen sign-in and a broken WhatsApp connection are what a user least expects. A switch
        // set months ago must not be the reason nobody noticed.
        NotificationCategories.CanBeSilenced(category).Should().Be(silenceable);
    }
}

/// <summary>
/// The switches themselves: reading them, saving them, and honouring them.
/// </summary>
public sealed class NotificationPreferenceTests
{
    private const long TenantId = 6001;
    private const long UserId = 61;
    private const long ColleagueId = 62;
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly List<Notification> _rows = [];
    private readonly List<UserNotificationPreference> _stored = [];
    private readonly IRepository<Notification> _notifications = Substitute.For<IRepository<Notification>>();
    private readonly IRepository<UserNotificationPreference> _preferences =
        Substitute.For<IRepository<UserNotificationPreference>>();
    private readonly InMemoryQueryExecutor _queries = new();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public NotificationPreferenceTests()
    {
        _notifications.Query(Arg.Any<bool>()).Returns(_ => _rows.AsQueryable());
        _preferences.Query(Arg.Any<bool>()).Returns(_ => _stored.AsQueryable());

        _preferences.When(repository => repository.Add(Arg.Any<UserNotificationPreference>()))
            .Do(call => _stored.Add(call.Arg<UserNotificationPreference>()!));
    }

    private NotificationService CreateService(long userId = UserId) =>
        new(
            _notifications,
            _preferences,
            _queries,
            _unitOfWork,
            new StubCurrentUser { UserId = userId },
            new StubTenantContext { TenantId = TenantId });

    private void Silence(long userId, NotificationCategory category) =>
        _stored.Add(new UserNotificationPreference
        {
            Id = _stored.Count + 1,
            UserId = userId,
            Category = category,
            Enabled = false,
        });

    private Notification Raised(NotificationKind kind, long? userId = UserId, bool read = false)
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

    [Fact]
    public async Task A_user_who_has_never_saved_anything_wants_everything()
    {
        var preferences = await CreateService().GetPreferencesAsync(TestContext.Current.CancellationToken);

        // Not a 404: "no preferences stored" and "this API has no preferences" are different
        // answers, and a client cannot tell them apart from a missing route.
        preferences.Should().Be(new NotificationPreferences(true, true, true, true, true, true));
        _stored.Should().BeEmpty("nothing is written until somebody actually turns something off");
    }

    [Fact]
    public async Task Saving_one_switch_leaves_the_others_alone()
    {
        var service = CreateService();

        var saved = await service.UpdatePreferencesAsync(
            new Dictionary<string, bool> { ["messages"] = false },
            TestContext.Current.CancellationToken);

        saved.Messages.Should().BeFalse();
        saved.Campaigns.Should().BeTrue();
        saved.Team.Should().BeTrue();
        saved.Billing.Should().BeTrue();

        // And it survives a read.
        (await service.GetPreferencesAsync(TestContext.Current.CancellationToken)).Messages.Should().BeFalse();
    }

    [Fact]
    public async Task Security_and_system_come_back_on_however_they_are_sent()
    {
        var saved = await CreateService().UpdatePreferencesAsync(
            new Dictionary<string, bool> { ["security"] = false, ["system"] = false },
            TestContext.Current.CancellationToken);

        saved.Security.Should().BeTrue();
        saved.System.Should().BeTrue();

        // Nor is a row left behind that a later change of rules might start honouring.
        _stored.Where(row => !row.Enabled).Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_this_build_has_never_heard_of_is_ignored_rather_than_refused()
    {
        // An older client may send a category that no longer exists; a newer one may send a
        // category this build has not added yet. Neither is worth failing a settings save over.
        var saved = await CreateService().UpdatePreferencesAsync(
            new Dictionary<string, bool> { ["nonsense"] = false, ["campaigns"] = false },
            TestContext.Current.CancellationToken);

        saved.Campaigns.Should().BeFalse();
        saved.Messages.Should().BeTrue();
    }

    [Fact]
    public async Task A_silenced_category_never_reaches_the_person_who_silenced_it()
    {
        Silence(UserId, NotificationCategory.Messages);

        var mine = await CreateService().WhoWantsAsync(
            [UserId, ColleagueId], NotificationKind.InboxMessageReceived, TestContext.Current.CancellationToken);

        // Filtered before the rows are written, and one muted recipient does not silence the rest.
        mine.Should().BeEquivalentTo([ColleagueId]);
    }

    [Fact]
    public async Task Security_reaches_everyone_whatever_they_have_stored()
    {
        Silence(UserId, NotificationCategory.Security);
        Silence(UserId, NotificationCategory.System);

        var service = CreateService();

        (await service.WhoWantsAsync([UserId], NotificationKind.SecurityAlert, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo([UserId]);

        (await service.WhoWantsAsync([UserId], NotificationKind.MetaDisconnected, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo([UserId]);
    }

    [Fact]
    public async Task A_silenced_category_does_not_count_towards_the_bell()
    {
        Raised(NotificationKind.InboxMessageReceived);
        Raised(NotificationKind.InboxMessageReceived);
        Raised(NotificationKind.SecurityNewLogin);
        Raised(NotificationKind.CampaignFailed);

        Silence(UserId, NotificationCategory.Messages);

        var page = await CreateService().GetPageAsync(
            new NotificationQuery { Page = 1, PageSize = 20 }, TestContext.Current.CancellationToken);

        // The client cannot correct these two numbers on its own: they are computed here, over
        // everything rather than over the page. A hidden row that still counted would be the
        // switch's most obvious lie.
        page.TotalItems.Should().Be(2);
        page.UnreadCount.Should().Be(2);
        page.Items.Should().NotContain(row => row.Kind == NotificationKind.InboxMessageReceived);
    }

    [Fact]
    public async Task Two_people_in_one_workspace_each_get_what_they_asked_for()
    {
        // Tenant-wide rows: the recipient is nobody in particular, so the filter has to hold on the
        // way out as well as on the way in.
        Raised(NotificationKind.CampaignFailed, userId: null);
        Raised(NotificationKind.SecurityAlert, userId: null);

        Silence(UserId, NotificationCategory.Campaigns);

        var mine = await CreateService().GetAsync(TestContext.Current.CancellationToken);
        var theirs = await CreateService(ColleagueId).GetAsync(TestContext.Current.CancellationToken);

        mine.Select(row => row.Kind).Should().BeEquivalentTo([NotificationKind.SecurityAlert]);
        theirs.Select(row => row.Kind).Should()
            .BeEquivalentTo([NotificationKind.SecurityAlert, NotificationKind.CampaignFailed]);
    }

    [Fact]
    public async Task Every_notification_carries_its_category()
    {
        Raised(NotificationKind.InboxMessageReceived);
        Raised(NotificationKind.SecurityNewLogin);

        var all = await CreateService().GetAsync(TestContext.Current.CancellationToken);

        all.Should().Contain(row =>
            row.Kind == NotificationKind.InboxMessageReceived && row.Category == NotificationCategory.Messages);
        all.Should().Contain(row =>
            row.Kind == NotificationKind.SecurityNewLogin && row.Category == NotificationCategory.Security);
    }

    [Fact]
    public async Task The_page_can_be_narrowed_to_one_group()
    {
        Raised(NotificationKind.InboxMessageReceived);
        Raised(NotificationKind.CampaignFailed);
        Raised(NotificationKind.MetaDisconnected);

        var page = await CreateService().GetPageAsync(
            new NotificationQuery { Page = 1, PageSize = 20, Category = NotificationCategory.System },
            TestContext.Current.CancellationToken);

        // System is the fallback category, so it is everything unmapped as well as everything
        // mapped to it - which is why it is expressed as an exclusion rather than a list.
        page.Items.Should().ContainSingle().Which.Kind.Should().Be(NotificationKind.MetaDisconnected);
    }
}
