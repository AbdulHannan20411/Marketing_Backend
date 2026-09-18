using AwesomeAssertions;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.WhatsApp;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
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
    private readonly IQueryExecutor _queries = Substitute.For<IQueryExecutor>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMediaService _media = Substitute.For<IMediaService>();
    private readonly ISecretProtector _protector = Substitute.For<ISecretProtector>();
    private readonly IRealtimeNotifier _realtime = Substitute.For<IRealtimeNotifier>();

    private readonly List<Conversation> _addedConversations = [];
    private readonly List<ConversationMessage> _addedMessages = [];

    private readonly WhatsAppConnection _connection = new()
    {
        TenantId = TenantId,
        Status = ConnectionStatus.Connected,
        PhoneNumberId = "1290479527487598",
        EncryptedAccessToken = "sealed",
    };

    public InboundMessageServiceTests()
    {
        _protector.Unprotect("sealed").Returns("token");

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
    }

    private InboundMessageService CreateService() =>
        new(
            _conversations,
            _messages,
            _contacts,
            _queries,
            _unitOfWork,
            _media,
            _protector,
            _realtime,
            new FixedDateTimeProvider(Now),
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
            TenantId,
            Arg.Is<InboundMessageEvent>(evt => evt != null && evt.UnreadCount == 1 && evt.Preview == "Could you send the receipt?"),
            Arg.Any<CancellationToken>());
    }
}
