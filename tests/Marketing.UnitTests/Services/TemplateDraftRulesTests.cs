using AwesomeAssertions;
using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.Services;
using Marketing.Common.Exceptions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>What a template draft must satisfy before it is sent to Meta, and what Meta is sent.</summary>
public sealed class TemplateDraftRulesTests
{
    private static MessageTemplateDraft Draft(
        string name = "order_update",
        TemplateCategory category = TemplateCategory.Utility,
        string language = "en_US",
        string? headerKind = "text",
        string? headerText = "Order update",
        string body = "Hi {{1}}, your order {{2}} has shipped.",
        string? footer = "Reply STOP to opt out",
        IReadOnlyList<TemplateButtonDraft>? buttons = null) =>
        new(
            name,
            category,
            language,
            headerText,
            body,
            footer,
            Buttons: buttons ?? [new TemplateButtonDraft("quick_reply", "Thanks")],
            HeaderKind: headerKind);

    private static IReadOnlyDictionary<string, string[]> ErrorsFor(MessageTemplateDraft draft)
    {
        var validate = () => TemplateDraftRules.Validate(draft);

        return validate.Should().Throw<ValidationException>().Which.Errors;
    }

    [Fact]
    public void A_complete_draft_passes()
    {
        var validate = () => TemplateDraftRules.Validate(Draft(
            buttons:
            [
                new TemplateButtonDraft("quick_reply", "Thanks"),
                new TemplateButtonDraft("url", "Track order", "https://example.com/track"),
                new TemplateButtonDraft("phone_number", "Call us", "+14155550123"),
            ]));

        validate.Should().NotThrow();
    }

    [Fact]
    public void Placeholders_with_a_gap_are_refused()
    {
        ErrorsFor(Draft(body: "Hi {{1}}, your order {{3}} has shipped.")).Should().ContainKey("bodyText");
    }

    [Fact]
    public void A_body_that_is_only_a_placeholder_is_refused()
    {
        ErrorsFor(Draft(body: "{{1}}")).Should().ContainKey("bodyText");
    }

    [Theory]
    [InlineData("image")]
    [InlineData("video")]
    [InlineData("document")]
    public void A_media_header_needs_its_example_file(string headerKind)
    {
        ErrorsFor(Draft(headerKind: headerKind, headerText: null)).Should().ContainKey("headerSampleId");

        var withSample = () => TemplateDraftRules.Validate(
            Draft(headerKind: headerKind, headerText: null) with { HeaderSampleId = "tsm_7" });

        withSample.Should().NotThrow();
    }

    [Fact]
    public void An_audio_header_is_refused()
    {
        ErrorsFor(Draft(headerKind: "audio", headerText: null)).Should().ContainKey("headerKind");
    }

    [Fact]
    public void Real_examples_are_sent_exactly_as_given()
    {
        var draft = Draft() with { BodyExamples = ["Ayesha", " ORD-1042 "] };

        TemplateDraftRules.Validate(draft);

        TemplateDraftRules.ToDefinition(draft).BodyExamples.Should().Equal("Ayesha", "ORD-1042");
    }

    [Fact]
    public void Without_examples_an_older_client_still_gets_the_labelled_fallback()
    {
        TemplateDraftRules.ToDefinition(Draft()).BodyExamples.Should().Equal("Sample 1", "Sample 2");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void The_wrong_number_of_examples_is_refused(int count)
    {
        ErrorsFor(Draft() with { BodyExamples = [.. Enumerable.Range(1, count).Select(index => $"Example {index}")] })
            .Should().ContainKey("bodyExamples");
    }

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("tab\there")]
    [InlineData("too     many spaces")]
    [InlineData("")]
    public void A_malformed_example_is_refused(string bad)
    {
        ErrorsFor(Draft() with { BodyExamples = ["Ayesha", bad] }).Should().ContainKey("bodyExamples");
    }

    [Fact]
    public void A_header_placeholder_is_allowed_with_its_example_and_refused_without()
    {
        var draft = Draft(headerText: "Order {{1}} is on its way") with { HeaderExample = "ORD-1042" };

        TemplateDraftRules.Validate(draft);
        TemplateDraftRules.ToDefinition(draft).HeaderExample.Should().Be("ORD-1042");

        ErrorsFor(Draft(headerText: "Order {{1}} is on its way")).Should().ContainKey("headerExample");
    }

    [Theory]
    [InlineData("Order {{2}}")]
    [InlineData("{{1}} and {{2}}")]
    [InlineData("*Sale* today")]
    [InlineData("Sale \U0001F389")]
    public void Header_rules_match_metas(string header)
    {
        ErrorsFor(Draft(headerText: header) with { HeaderExample = "x" }).Should().ContainKey("headerText");
    }

    [Theory]
    [InlineData("{{1}} your order has shipped.")]
    [InlineData("Your order has shipped, {{1}}")]
    [InlineData("Hi {{1}}{{2}}, your order has shipped.")]
    public void A_body_cannot_start_or_end_on_a_placeholder_or_put_two_together(string body)
    {
        ErrorsFor(Draft(body: body)).Should().ContainKey("bodyText");
    }

    [Fact]
    public void Authentication_templates_are_left_to_whatsapp_manager()
    {
        ErrorsFor(Draft(category: TemplateCategory.Authentication)).Should().ContainKey("category");
    }

    [Theory]
    [InlineData("url", "example.com/track")]
    [InlineData("url", "ftp://example.com")]
    [InlineData("phone_number", "call me")]
    [InlineData("carrier_pigeon", "")]
    public void A_button_without_a_usable_destination_is_refused(string kind, string value)
    {
        ErrorsFor(Draft(buttons: [new TemplateButtonDraft(kind, "Go", value)])).Should().ContainKey("buttons");
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        // The editor shows each field's message beside the field, so one round trip per mistake would
        // make the form feel broken.
        var errors = ErrorsFor(Draft(name: "Order Update", body: ""));

        errors.Should().ContainKey("name");
        errors.Should().ContainKey("bodyText");
    }

    [Fact]
    public void A_draft_without_a_header_kind_is_read_from_its_header_text()
    {
        // Older clients never sent a header kind; a text header must still go through.
        var definition = TemplateDraftRules.ToDefinition(Draft(headerKind: null, headerText: "Order update"));

        definition.HeaderText.Should().Be("Order update");
    }

    [Fact]
    public void The_definition_carries_what_meta_needs()
    {
        var definition = TemplateDraftRules.ToDefinition(Draft(
            headerKind: "none",
            footer: "  ",
            buttons:
            [
                new TemplateButtonDraft("quick_reply", "Thanks"),
                new TemplateButtonDraft("url", "Track", "https://example.com/track"),
            ]));

        definition.Category.Should().Be("UTILITY");
        definition.HeaderText.Should().BeNull();
        definition.FooterText.Should().BeNull();

        // One example per placeholder, or Meta will not review the template.
        definition.BodyExamples.Should().Equal("Sample 1", "Sample 2");

        definition.Buttons.Select(button => button.Type).Should().Equal("QUICK_REPLY", "URL");
        definition.Buttons[1].Url.Should().Be("https://example.com/track");
    }

    [Fact]
    public void Variables_are_listed_in_number_order_not_text_order()
    {
        TemplateDraftRules.VariablesOf("{{2}} then {{10}} then {{1}} and {{2}} again")
            .Should().Equal("{{1}}", "{{2}}", "{{10}}");
    }
}
