using System.Text.Json;
using AwesomeAssertions;
using Marketing.Application.Interfaces;
using Marketing.Infrastructure.WhatsApp.Models;

namespace Marketing.UnitTests.Infrastructure;

/// <summary>The JSON sent to Meta for media headers, when a template is created and when it is sent.</summary>
public sealed class TemplateMediaPayloadTests
{
    [Fact]
    public void A_document_header_is_sent_by_id_with_its_name()
    {
        var json = JsonSerializer.Serialize(new List<TemplateComponent>
        {
            new("header", [TemplateParameter.ForMedia("document", "meta-77", "menu.pdf")]),
        });

        var parameter = JsonDocument.Parse(json).RootElement[0].GetProperty("parameters")[0];

        parameter.GetProperty("type").GetString().Should().Be("document");
        parameter.GetProperty("document").GetProperty("id").GetString().Should().Be("meta-77");
        parameter.GetProperty("document").GetProperty("filename").GetString().Should().Be("menu.pdf");
        parameter.TryGetProperty("text", out _).Should().BeFalse();
    }

    [Fact]
    public void An_image_header_carries_only_its_id()
    {
        var json = JsonSerializer.Serialize(TemplateParameter.ForMedia("image", "meta-5", "ignored.jpg"));

        json.Should().Be("""{"type":"image","image":{"id":"meta-5"}}""");
    }

    [Fact]
    public void A_text_parameter_is_unchanged()
    {
        JsonSerializer.Serialize(new TemplateParameter("John")).Should().Be("""{"type":"text","text":"John"}""");
    }

    [Fact]
    public void A_template_is_created_with_its_examples_and_header_handle()
    {
        var definition = new MetaTemplateDefinition(
            "summer_sale", "en_US", "MARKETING", null, "Hi {{1}}, the sale is on.", ["Ayesha"], null, [],
            HeaderMediaFormat: "IMAGE", HeaderHandle: "4::aW1h");

        var json = JsonSerializer.Serialize(TemplateDefinitionRequest.ForCreate(definition));
        var components = JsonDocument.Parse(json).RootElement.GetProperty("components");

        components[0].GetProperty("type").GetString().Should().Be("HEADER");
        components[0].GetProperty("format").GetString().Should().Be("IMAGE");
        components[0].GetProperty("example").GetProperty("header_handle")[0].GetString().Should().Be("4::aW1h");
        components[1].GetProperty("example").GetProperty("body_text")[0][0].GetString().Should().Be("Ayesha");
        components[1].GetProperty("example").TryGetProperty("header_handle", out _).Should().BeFalse();
    }

    [Fact]
    public void A_text_header_placeholder_carries_its_example()
    {
        var definition = new MetaTemplateDefinition(
            "order_shipped", "en_US", "UTILITY", "Order {{1}} is on its way", "Your order has shipped.", [], null, [],
            HeaderExample: "ORD-1042");

        var json = JsonSerializer.Serialize(TemplateDefinitionRequest.ForCreate(definition));
        var header = JsonDocument.Parse(json).RootElement.GetProperty("components")[0];

        header.GetProperty("example").GetProperty("header_text")[0].GetString().Should().Be("ORD-1042");
    }
}
