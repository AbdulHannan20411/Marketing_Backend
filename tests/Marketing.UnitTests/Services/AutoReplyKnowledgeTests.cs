using AwesomeAssertions;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Services.WhatsApp;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using NSubstitute;

namespace Marketing.UnitTests.Services;

/// <summary>The rules an uploaded knowledge file has to meet.</summary>
public sealed class AutoReplyKnowledgeRulesTests
{
    private static KnowledgeEntryDto Entry(
        string kind,
        string title,
        string? answer = "Some answer.",
        string? price = null,
        bool? available = null,
        IReadOnlyList<string>? keywords = null) =>
        new(kind, title, answer, price, available, keywords ?? []);

    private static AutoReplyKnowledgeRequest Upload(
        IReadOnlyList<KnowledgeEntryDto> entries,
        string fallback = "handoff",
        string? message = "We'll get back to you.",
        string? file = "glow.xlsx") =>
        new(entries, fallback, message, file);

    private static IReadOnlyDictionary<string, string[]> ErrorsOf(Action validate) =>
        validate.Should().Throw<ValidationException>().Which.Errors;

    [Fact]
    public void More_than_five_hundred_entries_are_refused()
    {
        var entries = Enumerable.Range(1, 501).Select(index => Entry("faq", $"Question {index}")).ToList();

        ErrorsOf(() => AutoReplyKnowledgeRules.Validate(Upload(entries))).Should().ContainKey("entries");
    }

    [Fact]
    public void A_question_needs_an_answer_but_a_rule_does_not()
    {
        var errors = ErrorsOf(() => AutoReplyKnowledgeRules.Validate(Upload([Entry("faq", "Parking?", answer: " ")])));

        errors.Should().ContainKey("entries[0].answer");
        errors["entries[0].answer"][0].Should().Be("Entry 1 (\"Parking?\"): \"Answer or details\" is empty.");

        AutoReplyKnowledgeRules.Validate(Upload([Entry("rule", "Never offer discounts", answer: null)]))
            .Entries.Should().ContainSingle();
    }

    [Fact]
    public void Price_and_availability_are_dropped_from_anything_but_a_product()
    {
        var stored = AutoReplyKnowledgeRules.Validate(
            Upload([Entry("business", "Business name", "Glow Studio", price: "Rs 5", available: true)])).Entries[0];

        stored.Price.Should().BeNull();
        stored.Available.Should().BeNull();
    }

    [Fact]
    public void The_same_title_twice_is_a_duplicate_whatever_its_spacing_or_case()
    {
        var errors = ErrorsOf(() => AutoReplyKnowledgeRules.Validate(
            Upload([Entry("faq", "Parking?"), Entry("faq", " parking? ")])));

        errors.Should().ContainKey("entries[1].title");
        errors["entries[1].title"][0].Should().Contain("entry 1");
    }

    [Fact]
    public void The_same_title_under_a_different_kind_is_not_a_duplicate()
    {
        AutoReplyKnowledgeRules.Validate(Upload([Entry("faq", "Delivery"), Entry("policy", "Delivery")]))
            .Entries.Should().HaveCount(2);
    }

    [Fact]
    public void A_handoff_needs_a_holding_message_but_silence_does_not()
    {
        ErrorsOf(() => AutoReplyKnowledgeRules.Validate(Upload([], "handoff", message: "")))
            .Should().ContainKey("fallbackMessage");

        AutoReplyKnowledgeRules.Validate(Upload([], "silent", message: "")).Fallback.Should().Be("silent");
    }

    [Fact]
    public void Keywords_are_trimmed_de_duplicated_and_emptied_of_blanks()
    {
        var stored = AutoReplyKnowledgeRules.Validate(
            Upload([Entry("product", "Haircut", keywords: [" trim ", "Trim", "", "cut"])])).Entries[0];

        stored.Keywords.Should().Equal("trim", "cut");
    }

    [Fact]
    public void Markup_control_characters_and_folder_paths_are_removed()
    {
        var validated = AutoReplyKnowledgeRules.Validate(Upload(
            [Entry("faq", "Hours<script>x</script>", "Open\u0007 <b>daily</b>\r\nuntil 8")],
            file: @"C:\Users\ayesha\Desktop\glow.xlsx"));

        validated.Entries[0].Title.Should().Be("Hoursx");
        validated.Entries[0].Answer.Should().Be("Open daily\nuntil 8");
        validated.SourceFileName.Should().Be("glow.xlsx");
    }

    [Fact]
    public void Entries_keep_the_order_they_were_uploaded_in()
    {
        var stored = AutoReplyKnowledgeRules.Validate(
            Upload([Entry("faq", "C"), Entry("faq", "A"), Entry("faq", "B")])).Entries;

        stored.Select(entry => entry.Title).Should().Equal("C", "A", "B");
        stored.Select(entry => entry.SortOrder).Should().Equal(0, 1, 2);
    }
}

/// <summary>The prompt built from the file, and the checks on what the model sends back.</summary>
public sealed class AutoReplyKnowledgePromptTests
{
    private static AutoReplyKnowledgeEntry Row(string kind, string title, string answer, string? price = null) =>
        new() { Kind = kind, Title = title, Answer = answer, Price = price };

    [Fact]
    public void A_price_nobody_quoted_is_caught()
    {
        IReadOnlyList<AutoReplyKnowledgeEntry> included = [Row("product", "Haircut", "Wash and cut.", "Rs 2,500")];

        AutoReplyKnowledgePrompt.HasInventedPrice("A haircut is Rs 999.", included).Should().BeTrue();
        AutoReplyKnowledgePrompt.HasInventedPrice("That costs 999/-", included).Should().BeTrue();
        AutoReplyKnowledgePrompt.HasInventedPrice("A haircut is Rs 2500.", included).Should().BeFalse();
        AutoReplyKnowledgePrompt.HasInventedPrice("A haircut is PKR 2,500.00", included).Should().BeFalse();
        AutoReplyKnowledgePrompt.HasInventedPrice("It takes 45 minutes.", included).Should().BeFalse();
    }

    [Theory]
    [InlineData("<<UNKNOWN>>", true)]
    [InlineData("  <<UNKNOWN>>\n", true)]
    [InlineData("", true)]
    [InlineData("We open at 11.", false)]
    public void An_unknown_answer_is_recognised(string reply, bool unknown) =>
        AutoReplyKnowledgePrompt.IsUnknown(reply).Should().Be(unknown);

    [Fact]
    public void Business_facts_rules_and_every_small_catalogue_row_go_into_the_prompt()
    {
        IReadOnlyList<AutoReplyKnowledgeEntry> entries =
        [
            Row("business", "Business name", "Glow Studio"),
            Row("product", "Haircut", "Wash and cut.", "Rs 2,500"),
            Row("faq", "Do I need an appointment?", "Walk-ins welcome."),
            Row("rule", "Never offer discounts.", string.Empty),
        ];

        var prompt = AutoReplyKnowledgePrompt.Build(entries, "price of haircut?", AutoReplyTriggers.FirstMessage, "Sara", []);

        prompt.Included.Should().HaveCount(4);
        prompt.Text.Should().Contain("BUSINESS FACTS").And.Contain("- Haircut — Rs 2,500. Wash and cut.")
            .And.Contain("RULES (always follow)").And.Contain(AutoReplyKnowledgePrompt.Unknown);
    }

    [Fact]
    public void A_large_catalogue_is_cut_to_the_rows_relevant_to_the_question()
    {
        var filler = new string('x', 300);
        var entries = Enumerable.Range(1, 150)
            .Select(index => Row("faq", $"Question number {index}", filler))
            .Append(Row("faq", "Is parking available?", "Yes, behind the salon."))
            .Append(Row("policy", "Returns", "No returns on services."))
            .ToList();

        var prompt = AutoReplyKnowledgePrompt.Build(entries, "Where can I park?", AutoReplyTriggers.Unanswered, "Sara", []);

        prompt.Included.Should().Contain(entry => entry.Title == "Is parking available?");
        prompt.Included.Should().Contain(entry => entry.Title == "Returns");
        prompt.Included.Count.Should().BeLessThan(30);
    }

    [Fact]
    public void A_greeting_can_be_made_from_the_business_name_alone()
    {
        AutoReplyKnowledgePrompt.Greeting([Row("business", "Business name", "Glow Studio")])
            .Should().Be("Hi! Welcome to Glow Studio — how can we help?");
    }

    [Fact]
    public void A_long_reply_is_cut_at_a_sentence_inside_the_limit()
    {
        var reply = string.Concat(Enumerable.Repeat("This is a sentence. ", 80));

        var tidy = AutoReplyKnowledgePrompt.Tidy(reply);

        tidy.Length.Should().BeLessThanOrEqualTo(AutoReplyKnowledgePrompt.ReplyMaxLength);
        tidy.Should().EndWith(".");
    }
}

/// <summary>Reading and replacing the knowledge file.</summary>
public sealed class AutoReplyKnowledgeServiceTests
{
    private const long TenantId = 61;

    private readonly IAutoReplyKnowledgeRepository _entries = Substitute.For<IAutoReplyKnowledgeRepository>();
    private readonly IRepository<AutoReplySettings> _settings = Substitute.For<IRepository<AutoReplySettings>>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly IQueryExecutor _queries = Substitute.For<IQueryExecutor>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<AutoReplyKnowledgeEntry> _added = [];

    public AutoReplyKnowledgeServiceTests()
    {
        _queries.FirstOrDefaultAsync(Arg.Any<IQueryable<AutoReplySettings>>(), Arg.Any<CancellationToken>())
            .Returns((AutoReplySettings?)null);
        _queries.ToListAsync(Arg.Any<IQueryable<AutoReplyKnowledgeEntry>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _queries.FirstOrDefaultAsync(Arg.Any<IQueryable<string>>(), Arg.Any<CancellationToken>())
            .Returns("Ayesha Khan");

        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<AutoReplySettings>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<AutoReplySettings>>>()!(CancellationToken.None));

        _entries.When(repository => repository.Add(Arg.Any<AutoReplyKnowledgeEntry>()))
            .Do(call => _added.Add(call.Arg<AutoReplyKnowledgeEntry>()!));
    }

    private AutoReplyKnowledgeService CreateService() =>
        new(
            _entries,
            _settings,
            Substitute.For<IRepository<User>>(),
            _audit,
            _queries,
            _unitOfWork,
            new StubCurrentUser { UserId = 5 },
            new StubRequestContext(),
            new StubTenantContext { TenantId = TenantId },
            new FixedDateTimeProvider(new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero)));

    [Fact]
    public async Task A_workspace_that_never_uploaded_gets_empty_defaults_not_a_404()
    {
        var knowledge = await CreateService().GetAsync(TestContext.Current.CancellationToken);

        knowledge.Entries.Should().BeEmpty();
        knowledge.Fallback.Should().Be("handoff");
        knowledge.FallbackMessage.Should().Be(AutoReplyKnowledgeRules.DefaultFallbackMessage);
        knowledge.SourceFileName.Should().BeNull();
        knowledge.UpdatedAt.Should().BeNull();
        knowledge.UpdatedByName.Should().BeNull();
    }

    [Fact]
    public async Task Replacing_deletes_the_old_rows_stores_the_new_in_order_and_says_who()
    {
        var saved = await CreateService().ReplaceAsync(
            new AutoReplyKnowledgeRequest(
                [
                    new KnowledgeEntryDto("business", "Business name", "Glow Studio", null, null, []),
                    new KnowledgeEntryDto("faq", "Parking?", "Behind the salon.", null, null, []),
                    new KnowledgeEntryDto("product", "Haircut", "Wash and cut.", "Rs 2,500", true, ["trim"]),
                ],
                "handoff",
                "We'll get back to you.",
                "glow.xlsx"),
            TestContext.Current.CancellationToken);

        await _entries.Received(1).DeleteAllForTenantAsync(TenantId, Arg.Any<CancellationToken>());
        _added.Select(entry => entry.Title).Should().Equal("Business name", "Parking?", "Haircut");

        saved.Entries.Select(entry => entry.Title).Should().Equal("Business name", "Parking?", "Haircut");
        saved.UpdatedByName.Should().Be("Ayesha Khan");
        saved.UpdatedAt.Should().NotBeNull();
        saved.SourceFileName.Should().Be("glow.xlsx");

        // One audit entry, with the count and the file name - never the content.
        _audit.Received(1).Add(Arg.Is<AuditLog>(entry =>
            entry != null
            && entry.EntityName == "auto_reply.knowledge.replaced"
            && entry.Changes != null
            && entry.Changes.Contains("\"entries\":3", StringComparison.Ordinal)
            && !entry.Changes.Contains("Glow Studio", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_invalid_upload_changes_nothing()
    {
        var replace = () => CreateService().ReplaceAsync(
            new AutoReplyKnowledgeRequest([new KnowledgeEntryDto("faq", "Parking?", "", null, null, [])], "handoff", "x", null),
            TestContext.Current.CancellationToken);

        await replace.Should().ThrowAsync<ValidationException>();
        await _entries.DidNotReceiveWithAnyArgs().DeleteAllForTenantAsync(default, TestContext.Current.CancellationToken);
    }
}
