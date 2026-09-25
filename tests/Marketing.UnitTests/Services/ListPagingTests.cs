using AwesomeAssertions;
using Marketing.Application.DTOs.Platform;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Marketing.Application.Services;
using Marketing.Application.Services.WhatsApp;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Requests;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using NSubstitute;
using static Marketing.Common.Constants.ContractEnums;
using RoleCatalog = Marketing.Common.Constants.Roles;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Reading a long thread from the end of it.
/// </summary>
/// <remarks>
/// The thread is stored oldest-first, which is how it must be rendered, and read newest-first,
/// which is what someone opening a conversation wants. Before this the client asked for page one,
/// read the total, worked out the last page and asked again - two requests to open any thread
/// longer than a page.
/// </remarks>
public sealed class ConversationPagingTests
{
    private const long TenantId = 4001;
    private const long ConversationId = 9100;
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly List<ConversationMessage> _thread = [];
    private readonly IRepository<Conversation> _conversations = Substitute.For<IRepository<Conversation>>();
    private readonly IRepository<ConversationMessage> _messages = Substitute.For<IRepository<ConversationMessage>>();
    private readonly IWhatsAppAccessService _access = Substitute.For<IWhatsAppAccessService>();
    private readonly InMemoryQueryExecutor _queries = new();

    public ConversationPagingTests()
    {
        for (var index = 1; index <= 200; index++)
        {
            _thread.Add(Message(index));
        }

        _conversations.Query(Arg.Any<bool>()).Returns(_ => new[]
        {
            new Conversation
            {
                Id = ConversationId,
                TenantId = TenantId,
                WaId = "923001234567",
                ContactName = "Amara Okafor",
                WhatsAppConnectionId = 55,
            },
        }.AsQueryable());

        _messages.Query(Arg.Any<bool>()).Returns(_ => _thread.AsQueryable());

        _access.GetCallerScopeAsync(Arg.Any<CancellationToken>()).Returns(WhatsAppAccessScope.Unrestricted);
        _access.LabelsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string> { [55] = "Support" });
    }

    /// <summary>Message <paramref name="index"/>, a minute after the one before it.</summary>
    private static ConversationMessage Message(int index) =>
        new()
        {
            Id = index,
            TenantId = TenantId,
            ConversationId = ConversationId,
            Direction = MessageDirection.Inbound,
            Kind = ConversationMessageKind.Text,
            Body = $"Message {index}",
            Status = InboxMessageStatus.Delivered,
            OccurredAt = Start.AddMinutes(index),
        };

    private InboxService CreateService() =>
        new(
            _conversations,
            _messages,
            Substitute.For<IRepository<MediaAsset>>(),
            Substitute.For<IRepository<User>>(),
            Substitute.For<IWhatsAppConnectionRepository>(),
            _queries,
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IWhatsAppGateway>(),
            Substitute.For<IMediaService>(),
            _access,
            Substitute.For<IRealtimeNotifier>(),
            Substitute.For<ISecretProtector>(),
            Substitute.For<ICurrentUser>(),
            new StubTenantContext { TenantId = TenantId },
            new FixedDateTimeProvider(Start.AddDays(1)));

    [Fact]
    public async Task The_newest_page_comes_back_oldest_first_inside_itself()
    {
        var page = await CreateService().GetMessagesAsync(
            "cnv_9100", page: 1, pageSize: 30, latest: true, before: null, TestContext.Current.CancellationToken);

        page.TotalItems.Should().Be(200);
        page.Items.Should().HaveCount(30);

        // The last thirty, in the order they were sent: this is what the inbox renders directly.
        page.Items[0].Body.Should().Be("Message 171");
        page.Items[^1].Body.Should().Be("Message 200");
    }

    [Fact]
    public async Task The_second_page_from_the_end_is_the_thirty_before_those()
    {
        var page = await CreateService().GetMessagesAsync(
            "cnv_9100", page: 2, pageSize: 30, latest: true, before: null, TestContext.Current.CancellationToken);

        page.Items[0].Body.Should().Be("Message 141");
        page.Items[^1].Body.Should().Be("Message 170");

        // Counted from the end, so "page * pageSize < totalItems" still answers "is there more".
        page.Page.Should().Be(2);
        (page.Page * page.PageSize).Should().BeLessThan(page.TotalItems);
    }

    [Fact]
    public async Task Paging_from_the_oldest_end_still_works_exactly_as_it_did()
    {
        var service = CreateService();

        var first = await service.GetMessagesAsync(
            "cnv_9100", page: 1, pageSize: 30, cancellationToken: TestContext.Current.CancellationToken);

        first.Items[0].Body.Should().Be("Message 1");
        first.Items[^1].Body.Should().Be("Message 30");

        var seventh = await service.GetMessagesAsync(
            "cnv_9100", page: 7, pageSize: 30, cancellationToken: TestContext.Current.CancellationToken);

        // 200 messages is six full pages and a short one; the old contract returned the remainder.
        seventh.Items.Should().HaveCount(20);
        seventh.Items[^1].Body.Should().Be("Message 200");

        var past = await service.GetMessagesAsync(
            "cnv_9100", page: 8, pageSize: 30, cancellationToken: TestContext.Current.CancellationToken);

        past.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_arriving_mid_read_neither_repeats_nor_skips_with_a_cursor()
    {
        var service = CreateService();

        var newest = await service.GetMessagesAsync(
            "cnv_9100", page: 1, pageSize: 30, latest: true, before: null, TestContext.Current.CancellationToken);

        // Someone writes in while the thread is being read. With page numbers this shifts every
        // boundary by one, and "load earlier" hands back a message already on screen.
        _thread.Add(Message(201));

        var earlier = await service.GetMessagesAsync(
            "cnv_9100",
            page: 1,
            pageSize: 30,
            latest: false,
            before: newest.Items[0].Id,
            TestContext.Current.CancellationToken);

        earlier.Items[^1].Body.Should().Be("Message 170");
        earlier.Items[0].Body.Should().Be("Message 141");

        var seen = newest.Items.Concat(earlier.Items).Select(message => message.Id).ToList();

        seen.Should().OnlyHaveUniqueItems();
        earlier.TotalItems.Should().Be(201, "the thread really did grow; only the window is anchored");
    }

    [Fact]
    public async Task The_oldest_page_reached_by_cursor_is_short_rather_than_negative()
    {
        var service = CreateService();

        var page = await service.GetMessagesAsync(
            "cnv_9100",
            page: 1,
            pageSize: 30,
            latest: false,
            before: $"msg_{20}",
            TestContext.Current.CancellationToken);

        page.Items.Should().HaveCount(19);
        page.Items[0].Body.Should().Be("Message 1");
        page.Items[^1].Body.Should().Be("Message 19");
    }
}

/// <summary>
/// Notifications, which grow for as long as an account exists.
/// </summary>
/// <remarks>
/// The bell's counts are the interesting part: they are about the account, not about the page or
/// the filter, so they have to be read separately from the rows.
/// </remarks>
public sealed class NotificationPagingTests
{
    private const long TenantId = 7001;
    private const long UserId = 77;
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly List<Notification> _rows = [];
    private readonly IRepository<Notification> _notifications = Substitute.For<IRepository<Notification>>();
    private readonly IRepository<NotificationDismissal> _dismissals =
        Substitute.For<IRepository<NotificationDismissal>>();
    private readonly InMemoryQueryExecutor _queries = new();

    public NotificationPagingTests()
    {
        // 45 notifications: every third unread, and every tenth of those critical.
        for (var index = 1; index <= 45; index++)
        {
            _rows.Add(new Notification
            {
                Id = index,
                TenantId = TenantId,
                UserId = index % 5 == 0 ? null : UserId,
                Kind = NotificationKind.CampaignCompleted,
                Title = $"Notification {index}",
                Body = "Something happened.",
                Priority = index % 10 == 0 ? NotificationPriority.Critical : NotificationPriority.Info,
                Icon = "megaphone",
                Read = index % 3 != 0,
                OccurredOn = Start.AddMinutes(index),
            });
        }

        _notifications.Query(Arg.Any<bool>()).Returns(_ => _rows.AsQueryable());
        _dismissals.Query(Arg.Any<bool>()).Returns(_ => Array.Empty<NotificationDismissal>().AsQueryable());
    }

    private NotificationService CreateService() =>
        new(
            _notifications,
            Substitute.For<IRepository<UserNotificationPreference>>(),
            _dismissals,
            _queries,
            Substitute.For<IUnitOfWork>(),
            new StubCurrentUser { UserId = UserId },
            new StubTenantContext { TenantId = TenantId });

    private static int Unread(IEnumerable<Notification> rows) => rows.Count(row => !row.Read);

    [Fact]
    public async Task The_second_page_is_the_second_twenty_of_the_whole_list()
    {
        var service = CreateService();

        var first = await service.GetPageAsync(
            new NotificationQuery { Page = 1, PageSize = 20 }, TestContext.Current.CancellationToken);

        var second = await service.GetPageAsync(
            new NotificationQuery { Page = 2, PageSize = 20 }, TestContext.Current.CancellationToken);

        first.Items.Should().HaveCount(20);
        second.Items.Should().HaveCount(20);
        second.TotalItems.Should().Be(45);
        second.TotalPages.Should().Be(3);

        // Newest first, and no row appears on both pages.
        first.Items[0].Title.Should().Be("Notification 45");
        second.Items[0].Title.Should().Be("Notification 25");
        first.Items.Select(row => row.Id).Should().NotIntersectWith(second.Items.Select(row => row.Id));
    }

    [Fact]
    public async Task Filtering_by_unread_counts_only_the_unread()
    {
        var page = await CreateService().GetPageAsync(
            new NotificationQuery { Page = 1, UnreadOnly = true }, TestContext.Current.CancellationToken);

        page.Items.Should().OnlyContain(row => !row.Read);
        page.TotalItems.Should().Be(Unread(_rows));
        page.TotalItems.Should().BeLessThan(_rows.Count);
    }

    [Fact]
    public async Task The_bell_counts_are_the_same_whichever_page_or_filter_is_asked_for()
    {
        var service = CreateService();

        var unread = Unread(_rows);
        var critical = _rows.Count(row => !row.Read && row.Priority == NotificationPriority.Critical);

        var first = await service.GetPageAsync(
            new NotificationQuery { Page = 1, PageSize = 20 }, TestContext.Current.CancellationToken);

        var third = await service.GetPageAsync(
            new NotificationQuery { Page = 3, PageSize = 20 }, TestContext.Current.CancellationToken);

        var filtered = await service.GetPageAsync(
            new NotificationQuery { Page = 1, PageSize = 5, Priority = NotificationPriority.Critical },
            TestContext.Current.CancellationToken);

        // The bell says "unread", never "unread on this page" and never "unread among criticals".
        first.UnreadCount.Should().Be(unread);
        third.UnreadCount.Should().Be(unread);
        filtered.UnreadCount.Should().Be(unread);

        first.CriticalCount.Should().Be(critical);
        third.CriticalCount.Should().Be(critical);
        filtered.CriticalCount.Should().Be(critical);
    }

    [Fact]
    public async Task Asking_without_paging_returns_the_list_this_route_always_returned()
    {
        var all = await CreateService().GetAsync(TestContext.Current.CancellationToken);

        all.Should().HaveCount(45);
        all[0].Title.Should().Be("Notification 45");
    }
}

/// <summary>
/// The platform's list of customer administrators, which grows with the business.
/// </summary>
public sealed class AdminAccountPagingTests
{
    private readonly List<User> _people = [];
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly InMemoryQueryExecutor _queries = new();

    public AdminAccountPagingTests()
    {
        var admin = new Role { Id = 1, Name = RoleCatalog.Admin, NormalizedName = "ADMIN", Description = string.Empty };

        for (var index = 1; index <= 30; index++)
        {
            var user = new User
            {
                Id = index,
                TenantId = index,
                Email = $"owner{index}@example.test",
                NormalizedEmail = $"OWNER{index}@EXAMPLE.TEST",
                DisplayName = $"Owner {index:D2}",
                PasswordHash = "hash",
                Tenant = new Tenant
                {
                    Id = index,
                    Name = $"Workspace {index:D2}",
                    Slug = $"workspace-{index}",
                    ContactEmail = $"hello{index}@example.test",
                    Status = index % 3 == 0
                        ? Marketing.Common.Constants.AppConstants.TenantStatus.Suspended
                        : Marketing.Common.Constants.AppConstants.TenantStatus.Active,
                },
            };

            user.UserRoles.Add(new UserRole { UserId = index, RoleId = admin.Id, Role = admin, User = user });
            _people.Add(user);
        }

        _users.Query(Arg.Any<bool>()).Returns(_ => _people.AsQueryable());
    }

    private PlatformService CreateService() =>
        new(
            Substitute.For<IRepository<Tenant>>(),
            _users,
            Empty<Contact>(),
            Empty<Campaign>(),
            Empty<MessageDailyStat>(),
            Empty<TenantSubscription>(),
            Empty<SubscriptionPlan>(),
            Substitute.For<IAuditLogRepository>(),
            _queries,
            new FixedDateTimeProvider(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero)));

    private static IRepository<TEntity> Empty<TEntity>()
        where TEntity : BaseEntity
    {
        var repository = Substitute.For<IRepository<TEntity>>();

        repository.Query(Arg.Any<bool>()).Returns(_ => Enumerable.Empty<TEntity>().AsQueryable());

        return repository;
    }

    [Fact]
    public async Task A_page_is_twelve_rows_out_of_every_customer()
    {
        var page = await CreateService().GetAdminAccountsAsync(
            new AdminAccountQuery { Page = 1, PageSize = 12 }, TestContext.Current.CancellationToken);

        page.Items.Should().HaveCount(12);
        page.TotalItems.Should().Be(30);
        page.Items[0].Name.Should().Be("Owner 01");
    }

    [Fact]
    public async Task The_status_filter_narrows_the_total_as_well_as_the_page()
    {
        var page = await CreateService().GetAdminAccountsAsync(
            new AdminAccountQuery { Page = 1, PageSize = 5, Status = TenantAccountStatus.Suspended },
            TestContext.Current.CancellationToken);

        // Every third workspace is suspended, so a filtered total must not be the unfiltered one.
        page.TotalItems.Should().Be(10);
        page.Items.Should().HaveCount(5);
        page.Items.Should().OnlyContain(account => account.Status == TenantAccountStatus.Suspended);
    }

    [Fact]
    public async Task Asking_without_paging_returns_every_customer_as_before()
    {
        var accounts = await CreateService().GetAdminAccountsAsync(TestContext.Current.CancellationToken);

        accounts.Should().HaveCount(30);
    }
}
