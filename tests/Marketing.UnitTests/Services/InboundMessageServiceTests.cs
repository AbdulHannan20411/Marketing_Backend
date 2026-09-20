using AwesomeAssertions;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.WhatsApp;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// What a customer's message does to a conversation, above all to the window.
/// </summary>
/// <remarks>
/// Without this path the inbox is permanently empty: every inbound message arrives on the webhook and
/// nowhere else, and the reply window opens only here.
/// </remarks>
public sealed class InboundMessageServiceTests
{
    private const long TenantId = 5201;
    private const string WaId = "923001234567";
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Arrived = new(2026, 9, 18, 11, 30, 0, TimeSpan.Zero);

    private readonly IRepository<Conversation> _conversations = Substitute.For<IRepository<Conversation>>();
    private readonly IRepository<ConversationMessage> _messages = Substitute.For<IRepository<ConversationMessage>>();
    private readonly IRepository<Contact> _contacts = Substitute.For<IRepository<Contact>>();
    private readonly IRepository<Notification> _notifications = Substitute.For<IRepository<Notification>>();
    private readonly List<Notification> _raised = [];
    private readonly IQueryExecutor _queries = Substitute.For<IQueryExecutor>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMediaService _media = Substitute.For<IMediaService>();
    private readonly ISecretProtector _protector = Substitute.For<ISecretProtector>();
    private readonly IRealtimeNotifier _realtime = Substitute.For<IRealtimeNotifier>();
    private readonly IWhatsAppAccessService _access = Substitute.For<IWhatsAppAccessService>();

    private readonly List<Conversation> _addedConversations = [];
    private readonly List<ConversationMessage> _addedMessages = [];

    private readonly WhatsAppConnection _connection = new()
    {
        Id = 31,
        Label = "Sales",
        TenantId = TenantId,
        Status = ConnectionStatus.Connected,
        PhoneNumberId = "1290479527487598",
        EncryptedAccessToken = "sealed",
    };

    public InboundMessageServiceTests()
    {
        _protector.Unprotect("sealed").Returns("token");

        // Two people may read this number; nobody else is told.
        _access.UsersWhoMayViewAsync(31, Arg.Any<CancellationToken>()).Returns([7L, 9L]);

        // No message stored under this id yet, no thread for the number, and the number is nobody's
        // saved contact: the first-contact case.
        _queries.FirstOrDefaultAsync(Arg.Any<IQueryable<long>>(), Arg.Any<CancellationToken>()).Returns(0L);
        _queries.FirstOrDefaultAsync(Arg.Any<IQueryable<Conversation>>(), Arg.Any<CancellationToken>())
            .Returns((Conversation?)null);
        _queries.FirstOrDefaultAsync(Arg.Any<IQueryable<Contact>>(), Arg.Any<CancellationToken>())
            .Returns((Contact?)null);

        _conversations.When(repository => repository.Add(Arg.Any<Conversation>()))
            .Do(call => _addedConversations.Add(call.Arg<Conversation>()!));

        _messages.When(repository => repository.Add(Arg.Any<ConversationMessage>()))
            .Do(call => _addedMessages.Add(call.Arg<ConversationMessage>()!));

        _notifications.When(repository => repository.Add(Arg.Any<Notification>()))
            .Do(call => _raised.Add(call.Arg<Notification>()!));
    }

    private InboundMessageService CreateService() =>
        new(
            _conversations,
            _messages,
            _contacts,
            _notifications,
            _queries,
            _unitOfWork,
            _media,
            _protector,
            _realtime,
            new FixedDateTimeProvider(Now),
            _access,
            NullLogger<InboundMessageService>.Instance);

    private static WebhookValue Value(WebhookInboundMessage message, string? profileName = "Amara Okafor") =>
        new(
            new WebhookMetadata("+92 300 1234567", "1290479527487598"),
            Statuses: null,
            TemplateEvent: null,
            TemplateName: null,
            TemplateLanguage: null,
            TemplateRejectionReason: null,
            Messages: [message],
            Contacts: [new WebhookContact(WaId, new WebhookProfile(profileName))]);

    private static WebhookInboundMessage TextMessage(string id = "wamid.in1", string body = "Could you send the receipt?") =>
        new(id, WaId, Arrived.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), "text",
            Text: new WebhookTextPayload(body));

    [Fact]
    public async Task An_inbound_message_opens_the_window_for_24_hours()
    {
        var applied = await CreateService().ApplyAsync(
            Value(TextMessage()),
            _connection,
            TestContext.Current.CancellationToken);

        applied.Should().Be(1);

        var conversation = _addedConversations.Should().ContainSingle().Which;

        // Measured from the customer's message, not from when this platform processed it: a webhook
        // delivered late must not hand the agent extra time Meta will not honour.
        conversation.WindowExpiresAt.Should().Be(Arrived.AddHours(24));
        conversation.UnreadCount.Should().Be(1);
        conversation.LastMessagePreview.Should().Be("Could you send the receipt?");
        conversation.LastMessageAt.Should().Be(Arrived);
        conversation.WaId.Should().Be(WaId);
        conversation.ContactName.Should().Be("Amara Okafor");
        conversation.ContactId.Should().BeNull();
    }

    [Fact]
    public async Task The_message_itself_is_stored_as_delivered_inbound()
    {
        await CreateService().ApplyAsync(Value(TextMessage()), _connection, TestContext.Current.CancellationToken);

        var message = _addedMessages.Should().ContainSingle().Which;

        message.Direction.Should().Be(MessageDirection.Inbound);
        message.Kind.Should().Be(ConversationMessageKind.Text);
        message.MetaMessageId.Should().Be("wamid.in1");
        message.Status.Should().Be(InboxMessageStatus.Delivered);
        message.OccurredAt.Should().Be(Arrived);
    }

    [Fact]
    public async Task Everyone_who_may_read_the_number_is_told_a_customer_wrote_in()
    {
        await CreateService().ApplyAsync(Value(TextMessage()), _connection, TestContext.Current.CancellationToken);

        // The same audience as the realtime push - the workspace's admins and the employees granted
        // this number. A notification is no use to someone who may not open the thread it points at.
        _raised.Should().HaveCount(2);
        _raised.Select(notification => notification.UserId).Should().BeEquivalentTo([7L, 9L]);

        var raised = _raised[0];

        raised.Kind.Should().Be(NotificationKind.InboxMessageReceived);
        raised.Title.Should().Be("New message from Amara Okafor");
        raised.Body.Should().Be("Could you send the receipt?");
        raised.ActionRoute.Should().Be("/inbox");
        raised.TenantId.Should().Be(TenantId);

        // And pushed, so the bell moves without a refresh.
        await _realtime.Received(2).NotifyUserAsync(
            Arg.Any<long>(), Arg.Any<Marketing.Application.DTOs.Workspace.AppNotification>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_thread_already_waiting_does_not_ring_the_bell_again()
    {
        // Two unread after this one: somebody was already told about this conversation, and six
        // bells for one customer typing six lines is how people learn to ignore the bell.
        _queries.FirstOrDefaultAsync(Arg.Any<IQueryable<Conversation>>(), Arg.Any<CancellationToken>())
            .Returns(new Conversation
            {
                Id = 88,
                TenantId = TenantId,
                WaId = WaId,
                ContactName = "Amara Okafor",
                WhatsAppConnectionId = 31,
                UnreadCount = 3,
            });

        await CreateService().ApplyAsync(Value(TextMessage()), _connection, TestContext.Current.CancellationToken);

        _addedMessages.Should().ContainSingle("the message itself is always stored");
        _raised.Should().BeEmpty();
    }

    [Fact]
    public async Task A_notification_that_cannot_be_raised_does_not_cost_the_message()
    {
        _access.UsersWhoMayViewAsync(31, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<long>>(_ => throw new InvalidOperationException("no"));

        var applied = await CreateService().ApplyAsync(
            Value(TextMessage()), _connection, TestContext.Current.CancellationToken);

        // Meta is answered 200 and the thread shows the message; only the bell is missing.
        applied.Should().Be(1);
        _addedMessages.Should().ContainSingle();
    }

    [Fact]
    public async Task A_first_message_points_at_the_conversation_object_not_at_an_id_it_has_not_got_yet()
    {
        await CreateService().ApplyAsync(Value(TextMessage()), _connection, TestContext.Current.CancellationToken);

        var conversation = _addedConversations.Should().ContainSingle().Which;
        var message = _addedMessages.Should().ContainSingle().Which;

        // The whole bug in one assertion. On first contact the conversation is created in this same
        // unit of work, so its identity is still zero; writing that zero into the message's foreign
        // key made PostgreSQL refuse the row - 23503 - and every inbound message from a new number
        // was lost with a 500 that Meta then retried. Through the navigation, Entity Framework
        // inserts the conversation first and fills the key in itself.
        message.Conversation.Should().BeSameAs(conversation);
        conversation.Id.Should().Be(0, "this is exactly the state the old code copied into the key");
    }

    [Fact]
    public async Task A_message_on_an_existing_thread_still_lands_on_that_thread()
    {
        var existing = new Conversation
        {
            Id = 88,
            TenantId = TenantId,
            WaId = WaId,
            ContactName = "Amara Okafor",
            WhatsAppConnectionId = 31,
        };

        _queries.FirstOrDefaultAsync(Arg.Any<IQueryable<Conversation>>(), Arg.Any<CancellationToken>())
            .Returns(existing);

        await CreateService().ApplyAsync(Value(TextMessage()), _connection, TestContext.Current.CancellationToken);

        var message = _addedMessages.Should().ContainSingle().Which;

        _addedConversations.Should().BeEmpty("the thread already exists");
        message.Conversation.Should().BeSameAs(existing);
        existing.UnreadCount.Should().Be(1);
    }

    [Fact]
    public async Task A_row_the_database_refuses_is_dropped_rather_than_left_staged()
    {
        var refused = new DbUpdateException(
            "refused",
            new InvalidOperationException("no matching conversation"));

        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns<int>(_ => throw refused);

        var apply = () => CreateService().ApplyAsync(
            Value(TextMessage()), _connection, TestContext.Current.CancellationToken);

        // Not swallowed as a redelivery: a failure that is not a duplicate is this platform's, and
        // Meta redelivering a 5xx is what gets the customer's message a second chance.
        await apply.Should().ThrowAsync<DbUpdateException>();

        // And nothing it staged is left behind for the webhook's own save to trip over.
        _messages.Received(1).Detach(Arg.Any<ConversationMessage>());
        _conversations.Received(1).Detach(Arg.Any<Conversation>());
    }

    [Fact]
    public async Task A_redelivered_message_changes_nothing()
    {
        // Meta redelivers on any non-200, and out of order. The id it already carries is what makes
        // the second copy a no-op rather than a second bubble in the thread.
        _queries.FirstOrDefaultAsync(Arg.Any<IQueryable<long>>(), Arg.Any<CancellationToken>()).Returns(77L);

        var applied = await CreateService().ApplyAsync(
            Value(TextMessage()),
            _connection,
            TestContext.Current.CancellationToken);

        applied.Should().Be(0);
        _addedMessages.Should().BeEmpty();
        _addedConversations.Should().BeEmpty();
    }

    [Fact]
    public async Task An_attachment_is_copied_into_this_platforms_storage()
    {
        _media.StoreInboundAsync("media-1", "invoice.pdf", "token", Arg.Any<CancellationToken>())
            .Returns(new MediaAsset { Id = 42, TenantId = TenantId, Kind = MediaKind.Document });

        var message = new WebhookInboundMessage(
            "wamid.in2",
            WaId,
            Arrived.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            "document",
            Document: new WebhookMediaPayload("media-1", "application/pdf", "Here it is", "invoice.pdf"));

        await CreateService().ApplyAsync(Value(message), _connection, TestContext.Current.CancellationToken);

        var stored = _addedMessages.Should().ContainSingle().Which;

        stored.Kind.Should().Be(ConversationMessageKind.Document);
        stored.Body.Should().Be("Here it is");
        stored.MediaId.Should().Be(42);
    }

    [Theory]
    [InlineData("location", "Shared a location")]
    [InlineData("contacts", "Shared a contact")]
    [InlineData("sticker", "Sent a sticker")]
    [InlineData("hologram", "Sent a message this app cannot show")]
    public async Task Message_types_this_app_cannot_render_are_described_rather_than_dropped(
        string type,
        string expected)
    {
        var message = new WebhookInboundMessage(
            "wamid.in3",
            WaId,
            Arrived.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            type);

        await CreateService().ApplyAsync(Value(message), _connection, TestContext.Current.CancellationToken);

        var stored = _addedMessages.Should().ContainSingle().Which;

        // A thread with a gap in it is worse than one that says plainly what arrived: the agent can
        // still open WhatsApp on their phone and look.
        stored.Kind.Should().Be(ConversationMessageKind.System);
        stored.Body.Should().Be(expected);
    }

    [Fact]
    public async Task A_tapped_template_button_reads_as_what_the_customer_chose()
    {
        var message = new WebhookInboundMessage(
            "wamid.in4",
            WaId,
            Arrived.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            "button",
            Button: new WebhookButtonPayload("Track order", "track"));

        await CreateService().ApplyAsync(Value(message), _connection, TestContext.Current.CancellationToken);

        _addedMessages.Should().ContainSingle().Which.Body.Should().Be("Track order");
    }

    [Fact]
    public async Task The_open_inbox_is_told_about_it()
    {
        await CreateService().ApplyAsync(Value(TextMessage()), _connection, TestContext.Current.CancellationToken);

        await _realtime.Received(1).PublishInboundMessageAsync(
            Arg.Is<IReadOnlyCollection<long>>(users => users != null && users.SequenceEqual(new[] { 7L, 9L })),
            Arg.Is<InboundMessageEvent>(evt =>
                evt != null
                && evt.UnreadCount == 1
                && evt.Preview == "Could you send the receipt?"
                && evt.AccountId == "wa_31"
                && evt.AccountLabel == "Sales"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_thread_belongs_to_the_number_the_customer_wrote_to()
    {
        await CreateService().ApplyAsync(Value(TextMessage()), _connection, TestContext.Current.CancellationToken);

        _addedConversations.Should().ContainSingle().Which.WhatsAppConnectionId.Should().Be(31);
        _connection.LastMessageReceivedAt.Should().NotBeNull();
    }
}
