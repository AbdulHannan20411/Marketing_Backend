using System.Text.Json;
using AwesomeAssertions;
using Marketing.Application.Interfaces;
using Marketing.Infrastructure.WhatsApp.Models;

namespace Marketing.UnitTests.Infrastructure;

/// <summary>Pins the JSON sent to Meta to create or edit a message template.</summary>
public sealed class TemplateDefinitionPayloadTests
{
    /// <summary>Refit's default, which writes nulls unless a property says otherwise.</summary>
    private static readonly JsonSerializerOptions RefitDefaults = new();

    private static MetaTemplateDefinition Definition(
        IReadOnlyList<string>? examples = null,
        string body = "Hi {{1}}, order {{2}} has shipped.") =>
        new(
            "order_update",
            "en_US",
            "UTILITY",
            "Order update",
            body,
            examples ?? ["Sample 1", "Sample 2"],
            "Reply STOP to opt out",
            [
                new MetaTemplateButton("QUICK_REPLY", "Thanks"),
                new MetaTemplateButton("URL", "Track", Url: "https://example.com/track"),
            ]);

    [Fact]
    public void Create_sends_the_shape_meta_documents()
    {
        var json = JsonSerializer.Serialize(TemplateDefinitionRequest.ForCreate(Definition()), RefitDefaults);

        json.Should().Be(
            """{"name":"order_update","language":"en_US","category":"UTILITY","components":["""
            + """{"type":"HEADER","format":"TEXT","text":"Order update"},"""
            + """{"type":"BODY","text":"Hi {{1}}, order {{2}} has shipped.","example":{"body_text":[["Sample 1","Sample 2"]]}},"""
            + """{"type":"FOOTER","text":"Reply STOP to opt out"},"""
            + """{"type":"BUTTONS","buttons":[{"type":"QUICK_REPLY","text":"Thanks"},{"type":"URL","text":"Track","url":"https://example.com/track"}]}"""
            + "]}");
    }

    [Fact]
    public void A_body_without_placeholders_carries_no_example()
    {
        var json = JsonSerializer.Serialize(
            TemplateDefinitionRequest.ForCreate(Definition(examples: [], body: "Your order has shipped.")),
            RefitDefaults);

        // The key, quoted: the button's address is example.com, which a bare word would also match.
        json.Should().NotContain("\"example\"");
    }

    [Fact]
    public void An_edit_never_sends_name_or_language_and_sends_category_only_when_it_changed()
    {
        var unchanged = JsonSerializer.Serialize(
            TemplateDefinitionRequest.ForEdit(Definition(), includeCategory: false),
            RefitDefaults);

        unchanged.Should().NotContain("\"name\"");
        unchanged.Should().NotContain("\"language\"");
        unchanged.Should().NotContain("\"category\"");
        unchanged.Should().Contain("\"components\"");

        var recategorised = JsonSerializer.Serialize(
            TemplateDefinitionRequest.ForEdit(Definition(), includeCategory: true),
            RefitDefaults);

        recategorised.Should().Contain("\"category\":\"UTILITY\"");
    }
}
