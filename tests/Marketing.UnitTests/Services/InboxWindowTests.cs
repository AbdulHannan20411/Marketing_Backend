using AwesomeAssertions;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.WhatsApp;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using NSubstitute;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// The 24-hour window, which decides whether an agent may answer at all.
/// </summary>
/// <remarks>
/// The client blocks a closed window too, but the server owns the clock: a tab left open for an hour
/// will still try, and Meta's own refusal for this is opaque enough that an agent would not know what
/// went wrong.
/// </remarks>
public sealed class InboxWindowTests
{
    private const long TenantId = 5101;
    private const string WaId = "923001234567";
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);

    private readonly IRepository<Conversation> _conversations = Substitute.For<IRepository<Conversation>>();
    private readonly IRepository<ConversationMessage> _messages = Substitute.For<IRepository<ConversationMessage>>();
    private readonly IRepository<MediaAsset> _media = Substitute.For<IRepository<MediaAsset>>();
    private readonly IWhatsAppConnectionRepository _connections = Substitute.For<IWhatsAppConnectionRepository>();
    private readonly IQueryExecutor _queries = Substitute.For<IQueryExecutor>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IWhatsAppGateway _gateway = Substitute.For<IWhatsAppGateway>();
    private readonly IMediaService _mediaService = Substitute.For<IMediaService>();
    private readonly ISecretProtector _protector = Substitute.For<ISecretProtector>();
    private readonly List<ConversationMessage> _added = [];

    private readonly Conversation _conversation = new()
    {
        Id = 9001,
        TenantId = TenantId,
        WaId = WaId,
        ContactName = "Amara Okafor",
        WindowExpiresAt = Now.AddHours(3),
    };

    public InboxWindowTests()
    {
        _queries.FirstOrDefaultAsync(Arg.Any<IQueryable<Conversation>>(), Arg.Any<CancellationToken>())
            .Returns(_conversation);

        _connections.FindForTenantAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new WhatsAppConnection
            {
                TenantId = TenantId,
                Status = ConnectionStatus.Connected,
                PhoneNumberId = "1290479527487598",
                EncryptedAccessToken = "sealed",
            });

        _protector.Unprotect("sealed").Returns("token");

        _gateway.SendTextAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns("wamid.sent");

        _messages.When(repository => repository.Add(Arg.Any<ConversationMessage>()))
            .Do(call => _added.Add(call.Arg<ConversationMessage>()!));
    }

    private InboxService CreateService() =>
        new(
            _conversations,
            _messages,
            _media,
            _connections,
            _queries,
            _unitOfWork,
            _gateway,
            _mediaService,
            _protector,
            new StubTenantContext { TenantId = TenantId },
            new FixedDateTimeProvider(Now));

    private static SendConversationMessageRequest Reply(string body = "Receipt is on its way.") =>
        new("cnv_9001", ConversationMessageKind.Text, body, null);

    [Fact]
    public async Task A_reply_inside_the_window_is_sent_and_recorded()
    {
        var sent = await CreateService().SendAsync("cnv_9001", Reply(), TestContext.Current.CancellationToken);

        sent.Status.Should().Be(InboxMessageStatus.Sent);
        sent.Direction.Should().Be(MessageDirection.Outbound);

        var stored = _added.Should().ContainSingle().Which;

        stored.MetaMessageId.Should().Be("wamid.sent");
        stored.Status.Should().Be(InboxMessageStatus.Sent);

        // The list has to show the reply as the latest activity, or the thread sinks back down it.
        _conversation.LastMessagePreview.Should().Be("Receipt is on its way.");
        _conversation.LastMessageAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_closed_window_is_refused_with_the_code_the_client_handles()
    {
        _conversation.WindowExpiresAt = Now.AddMinutes(-1);

        var send = () => CreateService().SendAsync("cnv_9001", Reply(), TestContext.Current.CancellationToken);

        var refusal = (await send.Should().ThrowAsync<BusinessRuleException>()).Which;

        // The client refreshes on exactly this code and points the agent at templates.
        refusal.ErrorCode.Should().Be("window_closed");

        _added.Should().BeEmpty();

        await _gateway.DidNotReceiveWithAnyArgs().SendTextAsync(
            default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_window_that_never_opened_is_also_closed()
    {
        _conversation.WindowExpiresAt = null;

        var send = () => CreateService().SendAsync("cnv_9001", Reply(), TestContext.Current.CancellationToken);

        (await send.Should().ThrowAsync<BusinessRuleException>()).Which.ErrorCode.Should().Be("window_closed");
    }

    [Fact]
    public async Task Nothing_is_sent_without_a_connected_account()
    {
        _connections.FindForTenantAsync(TenantId, Arg.Any<CancellationToken>()).Returns((WhatsAppConnection?)null);

        var send = () => CreateService().SendAsync("cnv_9001", Reply(), TestContext.Current.CancellationToken);

        (await send.Should().ThrowAsync<BusinessRuleException>()).Which.ErrorCode.Should().Be("not_connected");
    }

    [Fact]
    public async Task A_failed_send_is_kept_and_marked_failed()
    {
        _gateway.SendTextAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new ExternalServiceException("MetaWhatsAppCloudApi", "Graph API returned 400.")
            {
                ProviderErrorCode = 131026,
            });

        var sent = await CreateService().SendAsync("cnv_9001", Reply(), TestContext.Current.CancellationToken);

        // Never lost: an agent who sees their message vanish types it again, and the customer gets it
        // twice as soon as the fault clears.
        sent.Status.Should().Be(InboxMessageStatus.Failed);
        sent.FailureReason.Should().Contain("undeliverable");
        _added.Should().ContainSingle();
    }

    [Fact]
    public async Task A_template_cannot_be_sent_as_a_reply()
    {
        var send = () => CreateService().SendAsync(
            "cnv_9001",
            new SendConversationMessageRequest("cnv_9001", ConversationMessageKind.Template, "hello", null),
            TestContext.Current.CancellationToken);

        await send.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task An_empty_reply_is_refused_before_it_reaches_meta()
    {
        var send = () => CreateService().SendAsync("cnv_9001", Reply("   "), TestContext.Current.CancellationToken);

        await send.Should().ThrowAsync<ValidationException>();

        await _gateway.DidNotReceiveWithAnyArgs().SendTextAsync(
            default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }
}
