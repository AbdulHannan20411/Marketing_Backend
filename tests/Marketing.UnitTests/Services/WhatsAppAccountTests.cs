using AwesomeAssertions;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services;
using Marketing.Application.Services.WhatsApp;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>Naming a workspace's numbers.</summary>
public sealed class WhatsAppAccountRulesTests
{
    [Fact]
    public void A_name_the_server_chose_is_made_unique_with_a_suffix()
    {
        WhatsAppAccountRules.UniqueLabel("Acme", ["acme", "Acme 2"], allowSuffix: true).Should().Be("Acme 3");
    }

    [Fact]
    public void A_name_a_person_typed_is_refused_when_taken_rather_than_renamed()
    {
        var rename = () => WhatsAppAccountRules.UniqueLabel("SALES", ["Sales"], allowSuffix: false);

        rename.Should().Throw<BusinessRuleException>().Which.ErrorCode.Should().Be("whatsapp_label_taken");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("This label is far longer than forty characters allow")]
    public void Empty_or_overlong_labels_are_refused(string label)
    {
        var validate = () => WhatsAppAccountRules.ValidateLabel(label);

        validate.Should().Throw<ValidationException>().Which.Errors.Should().ContainKey("label");
    }

    [Fact]
    public void A_suffixed_name_still_fits_the_limit()
    {
        var forty = new string('x', 40);

        WhatsAppAccountRules.UniqueLabel(forty, [forty], allowSuffix: true).Should().HaveLength(40).And.EndWith(" 2");
    }
}

/// <summary>What one person may do on which number.</summary>
public sealed class WhatsAppAccessScopeTests
{
    [Fact]
    public void Reply_without_view_grants_nothing()
    {
        var scope = WhatsAppAccessScope.From(
            [new WhatsAppAccountAccess { WhatsAppConnectionId = 5, CanView = false, CanReply = true }]);

        scope.Allows(5, WhatsAppAccessLevel.Reply).Should().BeFalse();
        scope.ViewableAccountIds.Should().BeEmpty();
    }

    [Fact]
    public void Access_to_one_number_says_nothing_about_another()
    {
        var scope = WhatsAppAccessScope.From(
            [new WhatsAppAccountAccess { WhatsAppConnectionId = 5, CanView = true, CanReply = true }]);

        scope.Allows(5, WhatsAppAccessLevel.Reply).Should().BeTrue();
        scope.Allows(5, WhatsAppAccessLevel.Broadcast).Should().BeFalse();
        scope.Allows(6, WhatsAppAccessLevel.View).Should().BeFalse();
        scope.PermissionsOn(5).Should().Equal(WhatsAppAccessLevel.View, WhatsAppAccessLevel.Reply);
    }

    [Fact]
    public void An_administrator_may_do_everything_everywhere()
    {
        WhatsAppAccessScope.Unrestricted.Allows(123, WhatsAppAccessLevel.Broadcast).Should().BeTrue();
        WhatsAppAccessScope.Unrestricted.PermissionsOn(123).Should().HaveCount(3);
    }
}

/// <summary>Connecting another number: the checks that must run before Meta is asked anything.</summary>
public sealed class WhatsAppConnectLimitTests
{
    private const long TenantId = 88;

    private readonly IWhatsAppConnectionRepository _connections = Substitute.For<IWhatsAppConnectionRepository>();
    private readonly IWhatsAppGateway _gateway = Substitute.For<IWhatsAppGateway>();
    private readonly IPlanGuard _planGuard = Substitute.For<IPlanGuard>();
    private readonly ISecretProtector _protector = Substitute.For<ISecretProtector>();

    public WhatsAppConnectLimitTests()
    {
        _planGuard.CurrentPlanAsync(Arg.Any<CancellationToken>())
            .Returns(new SubscriptionPlan { Name = "Starter", MaxWhatsAppAccounts = 1 });

        _connections.FindAllForTenantAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new WhatsAppConnection
                {
                    Id = 1,
                    TenantId = TenantId,
                    PhoneNumberId = "existing-number",
                    Label = "Sales",
                    IsDefault = true,
                    Status = ConnectionStatus.Disconnected,
                },
            ]);

        _protector.Protect(Arg.Any<string>()).Returns("sealed");
        _gateway.ExchangeCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MetaAccessToken("token", null));
        _gateway.GetPhoneNumberAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new MetaPhoneNumber("new-number", "+92 300 0000000", "Acme", "GREEN"));
    }

    private WhatsAppConnectionService CreateService() =>
        new(
            _connections,
            _gateway,
            _protector,
            Substitute.For<IUnitOfWork>(),
            new StubTenantContext { TenantId = TenantId },
            new FixedDateTimeProvider(DateTimeOffset.UnixEpoch),
            Substitute.For<IWhatsAppAccessService>(),
            _planGuard,
            NullLogger<WhatsAppConnectionService>.Instance);

    [Fact]
    public async Task A_new_number_past_the_plan_is_refused_before_the_code_is_spent()
    {
        var connect = () => CreateService().ConnectAsync(
            new ConnectWhatsAppRequest("code", "waba", "new-number"),
            TestContext.Current.CancellationToken);

        var refusal = (await connect.Should().ThrowAsync<BusinessRuleException>()).Which;

        // A disconnected number still holds its slot.
        refusal.ErrorCode.Should().Be("whatsapp_account_limit_reached");
        refusal.Message.Should().Contain("1 WhatsApp number");
        await _gateway.DidNotReceiveWithAnyArgs().ExchangeCodeAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reconnecting_a_number_the_workspace_already_has_uses_no_new_slot()
    {
        await CreateService().ConnectAsync(
            new ConnectWhatsAppRequest("code", "waba", "existing-number"),
            TestContext.Current.CancellationToken);

        await _gateway.Received(1).ExchangeCodeAsync("code", Arg.Any<CancellationToken>());
        _connections.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Fact]
    public async Task A_number_held_by_another_workspace_is_refused_before_the_code_is_spent()
    {
        _connections.FindByPhoneNumberIdAsync("taken-number", Arg.Any<CancellationToken>())
            .Returns(new WhatsAppConnection { TenantId = 999, PhoneNumberId = "taken-number" });

        var connect = () => CreateService().ConnectAsync(
            new ConnectWhatsAppRequest("code", "waba", "taken-number"),
            TestContext.Current.CancellationToken);

        (await connect.Should().ThrowAsync<BusinessRuleException>()).Which.ErrorCode.Should().Be("whatsapp_number_in_use");
        await _gateway.DidNotReceiveWithAnyArgs().ExchangeCodeAsync(default!, TestContext.Current.CancellationToken);
    }
}
