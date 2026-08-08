using AwesomeAssertions;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

public sealed class WhatsAppWebhookServiceTests
{
    private const string PhoneNumberId = "123456789012345";
    private const string MetaMessageId = "wamid.HBgLMTIzNDU2Nzg5MAA=";

    private const long TenantId = 7001;
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly ICampaignMessageRepository _messages = Substitute.For<ICampaignMessageRepository>();
    private readonly IWhatsAppConnectionRepository _connections = Substitute.For<IWhatsAppConnectionRepository>();
    private readonly IRepository<Campaign> _campaigns = Substitute.For<IRepository<Campaign>>();
    private readonly IRepository<DeliveryFailure> _failures = Substitute.For<IRepository<DeliveryFailure>>();
    private readonly IRepository<MessageDailyStat> _stats = Substitute.For<IRepository<MessageDailyStat>>();
    private readonly IRepository<MessageTemplate> _templates = Substitute.For<IRepository<MessageTemplate>>();
    private readonly IQueryExecutor _queries = Substitute.For<IQueryExecutor>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IRealtimeNotifier _realtime = Substitute.For<IRealtimeNotifier>();
    private readonly StubTenantContext _tenantContext = new();

    private readonly Campaign _campaign = new()
    {
        Id = 8001,
        TenantId = TenantId,
        Name = "Loyalty reminder",
        TemplateName = "loyalty_reminder",
        Status = CampaignStatus.Sending,
        AudienceSize = 1,
        SentCount = 1,
    };

    private readonly WhatsAppConnection _connection = new()
    {
        Id = 9001,
        TenantId = TenantId,
        PhoneNumberId = PhoneNumberId,
        Status = ConnectionStatus.Connected,
    };

    public WhatsAppWebhookServiceTests()
    {
        _connections.FindByPhoneNumberIdAsync(PhoneNumberId, Arg.Any<CancellationToken>())
            .Returns(_connection);

        _campaigns.GetForUpdateAsync(_campaign.Id, Arg.Any<CancellationToken>()).Returns(_campaign);

        // No stored daily stat and no matching template: the service creates the stat row itself,
        // and the template branch is exercised by its own test.
        _queries.FirstOrDefaultAsync(Arg.Any<IQueryable<MessageDailyStat>>(), Arg.Any<CancellationToken>())
            .Returns((MessageDailyStat?)null);
    }

    private WhatsAppWebhookService CreateService() =>
        new(
            _messages,
            _connections,
            _campaigns,
            _failures,
            _stats,
            _templates,
            _queries,
            _unitOfWork,
            _realtime,
            _tenantContext,
            new FixedDateTimeProvider(Now),
            NullLogger<WhatsAppWebhookService>.Instance);

    private CampaignMessage GivenMessage(CampaignMessageStatus status)
    {
        var message = new CampaignMessage
        {
            Id = 8101,
            TenantId = TenantId,
            CampaignId = _campaign.Id,
            ContactId = 8201,
            PhoneNumber = "+441234567890",
            MetaMessageId = MetaMessageId,
            Status = status,
            SentOn = Now.AddMinutes(-5),
        };

        _messages.FindByMetaMessageIdAsync(MetaMessageId, Arg.Any<CancellationToken>()).Returns(message);

        return message;
    }

    private static WebhookEnvelope EnvelopeWith(string status, params WebhookError[] errors) =>
        new(
            "whatsapp_business_account",
            [
                new WebhookEntry(
                    "waba-id",
                    [
                        new WebhookChange(
                            "messages",
                            new WebhookValue(
                                new WebhookMetadata("+44 20 7946 0000", PhoneNumberId),
                                [
                                    new WebhookStatus(
                                        MetaMessageId,
                                        status,
                                        Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                                        "441234567890",
                                        errors.Length == 0 ? null : errors),
                                ],
                                TemplateEvent: null,
                                TemplateName: null,
                                TemplateLanguage: null,
                                TemplateRejectionReason: null)),
                    ]),
            ]);

    [Fact]
    public async Task A_delivery_receipt_advances_the_message_and_the_campaign_counter()
    {
        var message = GivenMessage(CampaignMessageStatus.Sent);

        var applied = await CreateService().ProcessAsync(EnvelopeWith("delivered"), TestContext.Current.CancellationToken);

        applied.Should().Be(1);
        message.Status.Should().Be(CampaignMessageStatus.Delivered);
        message.DeliveredOn.Should().Be(Now);
        _campaign.DeliveredCount.Should().Be(1);
    }

    [Fact]
    public async Task A_redelivered_receipt_is_applied_once()
    {
        var message = GivenMessage(CampaignMessageStatus.Sent);
        var service = CreateService();

        await service.ProcessAsync(EnvelopeWith("delivered"), TestContext.Current.CancellationToken);
        var second = await service.ProcessAsync(EnvelopeWith("delivered"), TestContext.Current.CancellationToken);

        // Meta retries until it gets a 2xx, so the same receipt genuinely arrives twice. Counting
        // it twice would report more deliveries than there were recipients.
        second.Should().Be(0);
        _campaign.DeliveredCount.Should().Be(1);
        message.Status.Should().Be(CampaignMessageStatus.Delivered);
    }

    [Fact]
    public async Task A_late_delivered_receipt_does_not_undo_a_read()
    {
        var message = GivenMessage(CampaignMessageStatus.Read);

        var applied = await CreateService().ProcessAsync(EnvelopeWith("delivered"), TestContext.Current.CancellationToken);

        applied.Should().Be(0);
        message.Status.Should().Be(CampaignMessageStatus.Read);
    }

    [Fact]
    public async Task A_read_receipt_that_arrives_before_the_delivered_one_still_counts_the_delivery()
    {
        var message = GivenMessage(CampaignMessageStatus.Sent);

        await CreateService().ProcessAsync(EnvelopeWith("read"), TestContext.Current.CancellationToken);

        message.ReadOn.Should().Be(Now);
        _campaign.ReadCount.Should().Be(1);

        // Meta does not order its receipts. Leaving delivered at zero here would show a campaign
        // with more reads than deliveries, which reads as a bug to anyone looking at the report.
        _campaign.DeliveredCount.Should().Be(1);
    }

    [Fact]
    public async Task A_failure_receipt_records_the_reason_on_the_failures_report()
    {
        var message = GivenMessage(CampaignMessageStatus.Sent);

        await CreateService().ProcessAsync(
            EnvelopeWith("failed", new WebhookError(131026, "Message undeliverable", "Receiver is incapable")),
            TestContext.Current.CancellationToken);

        message.Status.Should().Be(CampaignMessageStatus.Failed);
        message.ErrorCode.Should().Be(131026);
        _campaign.FailedCount.Should().Be(1);

        _failures.Received(1).Add(Arg.Is<DeliveryFailure>(failure =>
            failure != null && failure.ErrorCode == 131026 && failure.CampaignId == _campaign.Id));
    }

    [Fact]
    public async Task A_receipt_for_an_unknown_number_is_dropped_without_writing_anything()
    {
        _connections.FindByPhoneNumberIdAsync(PhoneNumberId, Arg.Any<CancellationToken>())
            .Returns((WhatsAppConnection?)null);

        var applied = await CreateService().ProcessAsync(EnvelopeWith("delivered"), TestContext.Current.CancellationToken);

        applied.Should().Be(0);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Applying_a_receipt_enters_the_owning_tenant_and_leaves_it_again()
    {
        GivenMessage(CampaignMessageStatus.Sent);

        await CreateService().ProcessAsync(EnvelopeWith("delivered"), TestContext.Current.CancellationToken);

        // The webhook is anonymous, so the tenant comes from the resolved number - and must not
        // leak into whatever the same scope handles next.
        _tenantContext.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task Receipts_mark_the_connection_as_healthy()
    {
        GivenMessage(CampaignMessageStatus.Sent);
        _connection.WebhookHealthy = false;

        await CreateService().ProcessAsync(EnvelopeWith("delivered"), TestContext.Current.CancellationToken);

        _connection.WebhookHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task An_unrecognised_status_is_ignored()
    {
        var message = GivenMessage(CampaignMessageStatus.Sent);

        var applied = await CreateService().ProcessAsync(EnvelopeWith("deleted"), TestContext.Current.CancellationToken);

        applied.Should().Be(0);
        message.Status.Should().Be(CampaignMessageStatus.Sent);
    }

    [Fact]
    public async Task An_empty_payload_is_accepted_and_does_nothing()
    {
        var applied = await CreateService().ProcessAsync(
            new WebhookEnvelope("whatsapp_business_account", null),
            TestContext.Current.CancellationToken);

        applied.Should().Be(0);
    }

    [Fact]
    public void The_message_identifier_prefix_stays_opaque_to_the_client()
    {
        // Not webhook behaviour, but the same contract: nothing Meta sends becomes a client-visible
        // identifier without going through the prefix helper.
        PublicId.From(PublicId.Campaign, _campaign.Id).Should().Be("cmp_8001");
    }
}
