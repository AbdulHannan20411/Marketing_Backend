using AwesomeAssertions;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.WhatsApp;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using NSubstitute;

namespace Marketing.UnitTests.Services;

/// <summary>What counts as a greeting worth answering automatically.</summary>
/// <remarks>
/// Narrow on purpose. "Hi" deserves an automatic hello; "hi, where is my order" is a question, and
/// answering a question with a greeting is worse than leaving it for a person.
/// </remarks>
public sealed class GreetingDetectionTests
{
    [Theory]
    [InlineData("hi")]
    [InlineData("Hello")]
    [InlineData("HI!!")]
    [InlineData("hey there")]
    [InlineData("good morning")]
    [InlineData("salam")]
    [InlineData("aoa")]
    [InlineData("Assalam o Alaikum")]
    [InlineData("السلام علیکم")]
    [InlineData("hello sir")]
    public void A_greeting_and_nothing_else_is_a_greeting(string body)
    {
        AutoReplyTriggers.IsGreeting(body).Should().BeTrue();
    }

    [Theory]
    [InlineData("hi, where is my order")]
    [InlineData("hello do you have size 40 in stock")]
    [InlineData("salam bhai order kab ayega")]
    [InlineData("I want a refund")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    public void Anything_that_asks_something_is_not(string body)
    {
        // These need a person, or at least an answer rather than a wave.
        AutoReplyTriggers.IsGreeting(body).Should().BeFalse();
    }

    [Fact]
    public void A_greeting_wrapped_in_emoji_still_counts()
    {
        AutoReplyTriggers.IsGreeting("hello 👋").Should().BeTrue();
    }
}

/// <summary>What a plan entitles a workspace to, and what is left of it.</summary>
public sealed class AutoReplyAllowanceTests
{
    private static AutoReplyAllowance Allowance(int? limit, int used, params string[] triggers) =>
        new("Growth", HasAiModule: true, triggers, limit, used, DateTimeOffset.UtcNow.AddDays(10));

    [Fact]
    public void A_plan_with_no_ceiling_always_has_room()
    {
        var allowance = Allowance(null, 10_000, AutoReplyTriggers.Greeting);

        allowance.Remaining.Should().BeNull();
        allowance.HasHeadroom.Should().BeTrue();
    }

    [Fact]
    public void A_spent_allowance_has_none()
    {
        var allowance = Allowance(500, 500, AutoReplyTriggers.Greeting);

        allowance.Remaining.Should().Be(0);
        allowance.HasHeadroom.Should().BeFalse();
    }

    [Fact]
    public void Usage_past_the_ceiling_never_reports_a_negative_remainder()
    {
        // A burst can overshoot by a reply or two; the screen must not show "-3 left".
        Allowance(500, 503, AutoReplyTriggers.Greeting).Remaining.Should().Be(0);
    }

    [Fact]
    public void Only_the_triggers_the_plan_sells_are_allowed()
    {
        var allowance = Allowance(500, 0, AutoReplyTriggers.Greeting, AutoReplyTriggers.FirstMessage);

        allowance.AllowsTrigger(AutoReplyTriggers.Greeting).Should().BeTrue();
        allowance.AllowsTrigger(AutoReplyTriggers.FirstMessage).Should().BeTrue();
        allowance.AllowsTrigger(AutoReplyTriggers.Unanswered).Should().BeFalse();
    }

    [Fact]
    public void Without_the_ai_module_the_plan_sells_none_of_them()
    {
        // The module says whether the assistant exists at all; the triggers say when it may speak.
        var allowance = new AutoReplyAllowance(
            "Starter",
            HasAiModule: false,
            [AutoReplyTriggers.Greeting],
            500,
            0,
            null);

        allowance.AllowsTrigger(AutoReplyTriggers.Greeting).Should().BeFalse();
    }
}

/// <summary>Saving the rules, and refusing what the plan does not sell.</summary>
public sealed class AutoReplySettingsTests
{
    private const long TenantId = 6101;

    private readonly IRepository<AutoReplySettings> _settings = Substitute.For<IRepository<AutoReplySettings>>();
    private readonly IAutoReplyAllowance _allowance = Substitute.For<IAutoReplyAllowance>();
    private readonly IQueryExecutor _queries = Substitute.For<IQueryExecutor>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAiService _ai = Substitute.For<IAiService>();
    private readonly List<AutoReplySettings> _added = [];

    public AutoReplySettingsTests()
    {
        _ai.IsConfigured.Returns(true);

        _queries.FirstOrDefaultAsync(Arg.Any<IQueryable<AutoReplySettings>>(), Arg.Any<CancellationToken>())
            .Returns((AutoReplySettings?)null);

        _settings.When(repository => repository.Add(Arg.Any<AutoReplySettings>()))
            .Do(call => _added.Add(call.Arg<AutoReplySettings>()!));

        Sells(AutoReplyTriggers.Greeting, AutoReplyTriggers.FirstMessage);
    }

    private void Sells(params string[] triggers) =>
        _allowance.ForTenantAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new AutoReplyAllowance("Growth", true, triggers, 500, 25, DateTimeOffset.UtcNow.AddDays(9)));

    private AutoReplyService CreateService() =>
        new(_settings, _allowance, _queries, _unitOfWork, _ai, new StubTenantContext { TenantId = TenantId });

    private static AutoReplySettingsRequest Request(
        bool greeting = true,
        bool firstMessage = false,
        bool unanswered = false,
        int delaySeconds = 30,
        int unansweredAfterMinutes = 300,
        int maxPerConversationPerDay = 3) =>
        new(
            Enabled: true,
            Triggers: new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [AutoReplyTriggers.Greeting] = greeting,
                [AutoReplyTriggers.FirstMessage] = firstMessage,
                [AutoReplyTriggers.Unanswered] = unanswered,
            },
            DelaySeconds: delaySeconds,
            UnansweredAfterMinutes: unansweredAfterMinutes,
            Instructions: "We are a salon in Lahore. Never quote prices.",
            MaxPerConversationPerDay: maxPerConversationPerDay);

    [Fact]
    public async Task Rules_the_plan_sells_are_saved()
    {
        var saved = await CreateService().UpdateAsync(Request(), TestContext.Current.CancellationToken);

        var stored = _added.Should().ContainSingle().Which;

        stored.Enabled.Should().BeTrue();
        stored.GreetingEnabled.Should().BeTrue();
        stored.UnansweredEnabled.Should().BeFalse();
        stored.DelaySeconds.Should().Be(30);
        stored.Instructions.Should().Contain("salon");

        saved.UsedThisPeriod.Should().Be(25);
        saved.RemainingThisPeriod.Should().Be(475);
        saved.AssistantConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task A_trigger_the_plan_does_not_sell_is_refused_by_name()
    {
        // The starter plans sell greetings; answering everything unanswered is the expensive one.
        var update = () => CreateService().UpdateAsync(
            Request(unanswered: true),
            TestContext.Current.CancellationToken);

        var refusal = (await update.Should().ThrowAsync<BusinessRuleException>()).Which;

        refusal.ErrorCode.Should().Be("auto_reply_trigger_not_in_plan");
        refusal.Message.Should().Contain("unanswered messages");

        _added.Should().BeEmpty();
    }

    [Theory]
    [InlineData(5, 300, 3, "delaySeconds")]
    [InlineData(4000, 300, 3, "delaySeconds")]
    [InlineData(30, 1, 3, "unansweredAfterMinutes")]
    [InlineData(30, 5000, 3, "unansweredAfterMinutes")]
    [InlineData(30, 300, 0, "maxPerConversationPerDay")]
    [InlineData(30, 300, 99, "maxPerConversationPerDay")]
    public async Task Values_outside_the_allowed_range_are_named(
        int delaySeconds,
        int unansweredAfterMinutes,
        int maxPerDay,
        string field)
    {
        var update = () => CreateService().UpdateAsync(
            Request(delaySeconds: delaySeconds, unansweredAfterMinutes: unansweredAfterMinutes, maxPerConversationPerDay: maxPerDay),
            TestContext.Current.CancellationToken);

        (await update.Should().ThrowAsync<ValidationException>()).Which.Errors.Should().ContainKey(field);
    }

    [Fact]
    public async Task A_workspace_that_has_never_configured_anything_reads_as_off()
    {
        var settings = await CreateService().GetAsync(TestContext.Current.CancellationToken);

        settings.Enabled.Should().BeFalse();
        settings.Triggers[AutoReplyTriggers.Greeting].Should().BeFalse();

        // What the plan would allow is still reported, so the screen can show what an upgrade buys.
        settings.AllowedTriggers[AutoReplyTriggers.Greeting].Should().BeTrue();
        settings.AllowedTriggers[AutoReplyTriggers.Unanswered].Should().BeFalse();

        _added.Should().BeEmpty();
    }
}
