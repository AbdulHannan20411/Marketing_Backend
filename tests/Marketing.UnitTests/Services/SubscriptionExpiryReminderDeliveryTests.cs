using AwesomeAssertions;
using Marketing.Application.Configurations;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.Billing;
using Marketing.Application.Services.Email;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Which subscriptions are reminded, and who a reminder reaches.
/// </summary>
/// <remarks>
/// Written after reminders were found to skip every subscription with auto-renew on - which is every
/// subscription, since nothing renews one yet - and to put the notification in every employee's bell.
/// The service's real query runs here against an in-memory list, so a filter added back to it is
/// caught, not just the countdown arithmetic the sibling tests cover.
/// </remarks>
public sealed class SubscriptionExpiryReminderDeliveryTests
{
    private const long TenantId = 4101;
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 6, 30, 0, TimeSpan.Zero);

    private readonly IRepository<TenantSubscription> _subscriptions = Substitute.For<IRepository<TenantSubscription>>();
    private readonly IRepository<Notification> _notifications = Substitute.For<IRepository<Notification>>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IQueryExecutor _queries = Substitute.For<IQueryExecutor>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly IEmailTemplateRenderer _templates = Substitute.For<IEmailTemplateRenderer>();

    private readonly List<TenantSubscription> _stored = [];
    private readonly List<Notification> _added = [];
    private readonly List<EmailMessage> _sent = [];

    private readonly List<User> _administrators =
    [
        Administrator(5001, "owner@example.com"),
        Administrator(5002, "second.admin@example.com"),
    ];

    public SubscriptionExpiryReminderDeliveryTests()
    {
        _subscriptions.Query(Arg.Any<bool>()).Returns(_ => _stored.AsQueryable());

        // The query is executed for real, so the filter under test is the one the service builds.
        _queries.ToListAsync(Arg.Any<IQueryable<TenantSubscription>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<TenantSubscription>>(
                [.. call.Arg<IQueryable<TenantSubscription>>()!]));

        _users.GetTenantAdministratorsAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(_administrators);

        _notifications.When(repository => repository.Add(Arg.Any<Notification>()))
            .Do(call => _added.Add(call.Arg<Notification>()!));

        _templates.RenderAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new EmailMessage(call.ArgAt<string>(1), call.ArgAt<string>(2), "Expiring", "<p/>", "text"));

        _email.When(sender => sender.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()))
            .Do(call => _sent.Add(call.Arg<EmailMessage>()!));
    }

    private static User Administrator(long id, string email) =>
        new()
        {
            Id = id,
            TenantId = TenantId,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = email,
            PasswordHash = "hash",
        };

    private static TenantSubscription Subscription(
        int daysLeft,
        bool autoRenew = true,
        SubscriptionStatus status = SubscriptionStatus.Active) =>
        new()
        {
            Id = 6001,
            TenantId = TenantId,
            Status = status,
            AutoRenew = autoRenew,
            ExpiresAt = Now.AddDays(daysLeft),
        };

    private SubscriptionExpiryReminderService CreateService() =>
        new(
            _subscriptions,
            _notifications,
            _users,
            _queries,
            _unitOfWork,
            _email,
            _templates,
            new StubTenantContext(),
            new FixedDateTimeProvider(Now),
            Options.Create(new EmailOptions()),
            NullLogger<SubscriptionExpiryReminderService>.Instance);

    [Fact]
    public async Task A_subscription_set_to_auto_renew_is_still_reminded()
    {
        // The regression: auto-renew is on for every subscription and nothing renews one, so skipping
        // them meant workspaces expired without a single warning.
        var subscription = Subscription(daysLeft: 7, autoRenew: true);
        _stored.Add(subscription);

        var sent = await CreateService().SendDueRemindersAsync(TestContext.Current.CancellationToken);

        sent.Should().Be(1);
        subscription.LastExpiryReminderDay.Should().Be(7);
        _sent.Select(message => message.ToAddress)
            .Should().BeEquivalentTo("owner@example.com", "second.admin@example.com");
    }

    [Fact]
    public async Task Each_administrator_gets_their_own_notification_and_nobody_else_does()
    {
        _stored.Add(Subscription(daysLeft: 3));

        await CreateService().SendDueRemindersAsync(TestContext.Current.CancellationToken);

        // Addressed, never null: a null recipient is shown to every employee in the workspace.
        _added.Select(notification => notification.UserId).Should().BeEquivalentTo(new long?[] { 5001, 5002 });
        _added.Should().AllSatisfy(notification =>
        {
            notification.TenantId.Should().Be(TenantId);
            notification.Kind.Should().Be(NotificationKind.SubscriptionExpiring);
            notification.OccurredOn.Should().Be(Now);
        });
    }

    [Fact]
    public async Task One_email_and_one_notification_per_administrator_per_day()
    {
        _stored.Add(Subscription(daysLeft: 5));
        var service = CreateService();

        await service.SendDueRemindersAsync(TestContext.Current.CancellationToken);
        await service.SendDueRemindersAsync(TestContext.Current.CancellationToken);

        _sent.Should().HaveCount(_administrators.Count);
        _added.Should().HaveCount(_administrators.Count);
    }

    [Theory]
    // The window reaches a day past the seven-day mark on purpose: the countdown is in calendar days,
    // and whether an instant eight days out is "7 days left" depends on the hour. IsDue settles it.
    [InlineData(9, SubscriptionStatus.Active, false)]
    [InlineData(7, SubscriptionStatus.Active, true)]
    [InlineData(1, SubscriptionStatus.Trial, true)]
    [InlineData(-1, SubscriptionStatus.Active, false)]
    [InlineData(3, SubscriptionStatus.Cancelled, false)]
    [InlineData(3, SubscriptionStatus.Expired, false)]
    public void The_reminder_window_ignores_auto_renew(int daysLeft, SubscriptionStatus status, bool expected)
    {
        var inWindow = SubscriptionExpiryReminderService.InReminderWindow(Now).Compile();

        inWindow(Subscription(daysLeft, autoRenew: true, status)).Should().Be(expected);
        inWindow(Subscription(daysLeft, autoRenew: false, status)).Should().Be(expected);
    }
}
