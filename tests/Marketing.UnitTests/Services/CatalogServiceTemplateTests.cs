using AwesomeAssertions;
using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.Interfaces;
using Marketing.Application.Services;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Creating a template submits it to Meta on the connected account, and stores it only once Meta
/// accepts it.
/// </summary>
/// <remarks>
/// Written after templates created in the app were found to be saved locally and never sent to Meta:
/// never reviewed, never approved, and never returned by a sync.
/// </remarks>
public sealed class CatalogServiceTemplateTests
{
    private const long TenantId = 4201;
    private const string WabaId = "2949465818718057";

    private readonly IRepository<MessageTemplate> _templates = Substitute.For<IRepository<MessageTemplate>>();
    private readonly IWhatsAppConnectionRepository _connections = Substitute.For<IWhatsAppConnectionRepository>();
    private readonly ISecretProtector _protector = Substitute.For<ISecretProtector>();
    private readonly IWhatsAppGateway _gateway = Substitute.For<IWhatsAppGateway>();
    private readonly IQueryExecutor _queries = Substitute.For<IQueryExecutor>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly Marketing.Application.Services.WhatsApp.ITemplateHeaderSampleService _headerSamples =
        Substitute.For<Marketing.Application.Services.WhatsApp.ITemplateHeaderSampleService>();
    private readonly List<MessageTemplate> _added = [];

    public CatalogServiceTemplateTests()
    {
        _connections.FindForTenantAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new WhatsAppConnection
            {
                TenantId = TenantId,
                Status = ConnectionStatus.Connected,
                WabaId = WabaId,
                EncryptedAccessToken = "sealed",
            });

        _protector.Unprotect("sealed").Returns("token");

        _queries.FirstOrDefaultAsync(Arg.Any<IQueryable<MessageTemplate>>(), Arg.Any<CancellationToken>())
            .Returns((MessageTemplate?)null);

        _templates.Query(Arg.Any<bool>()).Returns(_ => Enumerable.Empty<MessageTemplate>().AsQueryable());

        _templates.When(repository => repository.Add(Arg.Any<MessageTemplate>()))
            .Do(call => _added.Add(call.Arg<MessageTemplate>()!));
    }

    private static MessageTemplateDraft Draft(string? headerKind = "none") =>
        new(
            "order_update",
            TemplateCategory.Utility,
            "en_US",
            HeaderText: null,
            BodyText: "Hi {{1}}, your order has shipped.",
            FooterText: null,
            Buttons:
            [
                new TemplateButtonDraft("quick_reply", "Thanks"),
                new TemplateButtonDraft("url", "Track", "https://example.com/track"),
            ],
            HeaderKind: headerKind);

    private CatalogService CreateService() =>
        new(
            Substitute.For<IRepository<ContactGroup>>(),
            Substitute.For<IRepository<ContactTag>>(),
            _templates,
            Substitute.For<IRepository<Campaign>>(),
            _connections,
            Substitute.For<Marketing.Application.Services.WhatsApp.IWhatsAppAccessService>(),
            _headerSamples,
            _protector,
            _gateway,
            _queries,
            _unitOfWork,
            new StubTenantContext { TenantId = TenantId });

    [Fact]
    public async Task A_new_template_is_submitted_to_the_connected_account_and_stored_with_metas_id()
    {
        _gateway.CreateTemplateAsync(WabaId, Arg.Any<MetaTemplateDefinition>(), "token", Arg.Any<CancellationToken>())
            .Returns(new MetaTemplateSubmission("meta-77", "PENDING", "UTILITY"));

        await CreateService().CreateTemplateAsync(Draft(), cancellationToken: TestContext.Current.CancellationToken);

        await _gateway.Received(1).CreateTemplateAsync(
            WabaId,
            Arg.Is<MetaTemplateDefinition>(definition =>
                definition != null
                && definition.Name == "order_update"
                && definition.BodyExamples.Count == 1
                && definition.Buttons.Count == 2),
            "token",
            Arg.Any<CancellationToken>());

        var stored = _added.Should().ContainSingle().Which;

        stored.MetaTemplateId.Should().Be("meta-77");
        stored.WabaId.Should().Be(WabaId);
        stored.Status.Should().Be(TemplateStatus.Pending);
        stored.Variables.Should().Equal("{{1}}");
        stored.Buttons.Should().Equal("Thanks", "Track");

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Metas_refusal_is_returned_in_its_own_words_and_nothing_is_stored()
    {
        _gateway.CreateTemplateAsync(WabaId, Arg.Any<MetaTemplateDefinition>(), "token", Arg.Any<CancellationToken>())
            .ThrowsAsync(new ExternalServiceException("MetaWhatsAppCloudApi", "Graph API returned 400.")
            {
                ProviderErrorCode = 100,
                ProviderUserMessage = "Content in this language already exists.",
            });

        var create = () => CreateService().CreateTemplateAsync(Draft(), cancellationToken: TestContext.Current.CancellationToken);

        var refusal = (await create.Should().ThrowAsync<BusinessRuleException>()).Which;

        refusal.ErrorCode.Should().Be("template_rejected_by_meta");
        refusal.Message.Should().Be("Content in this language already exists.");

        _added.Should().BeEmpty();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_connected_account_nothing_is_sent_or_stored()
    {
        _connections.FindForTenantAsync(TenantId, Arg.Any<CancellationToken>()).Returns((WhatsAppConnection?)null);

        var create = () => CreateService().CreateTemplateAsync(Draft(), cancellationToken: TestContext.Current.CancellationToken);

        (await create.Should().ThrowAsync<BusinessRuleException>()).Which.ErrorCode.Should().Be("whatsapp_not_connected");

        await _gateway.DidNotReceiveWithAnyArgs().CreateTemplateAsync(
            default!, default!, default!, TestContext.Current.CancellationToken);
        _added.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invalid_draft_never_reaches_meta()
    {
        var create = () => CreateService().CreateTemplateAsync(Draft(headerKind: "image"), cancellationToken: TestContext.Current.CancellationToken);

        await create.Should().ThrowAsync<ValidationException>();

        await _gateway.DidNotReceiveWithAnyArgs().CreateTemplateAsync(
            default!, default!, default!, TestContext.Current.CancellationToken);
    }
}
