using System.Text.Json;
using AwesomeAssertions;
using Marketing.Infrastructure.WhatsApp.Models;

namespace Marketing.UnitTests.Infrastructure;

/// <summary>
/// Pins the exact JSON sent to Meta for a template message.
/// </summary>
/// <remarks>
/// Written after a campaign failed with Meta's <c>132012</c>, "parameter format does not match
/// format in the created template". The payload carried <c>"components": null</c>; Meta reads the
/// key's presence as a promise that parameters follow, and refuses when none do. The same message
/// sent by hand, omitting the key entirely, was accepted.
/// </remarks>
public sealed class TemplateSendPayloadTests
{
    /// <summary>Refit's default, which writes nulls unless a property says otherwise.</summary>
    private static readonly JsonSerializerOptions RefitDefaults = new();

    private static string Serialise(IReadOnlyList<TemplateComponent>? components) =>
        JsonSerializer.Serialize(
            new SendTemplateMessageRequest(
                "923367890092",
                new TemplateMessagePayload("hello_world", new TemplateLanguage("en_US"), components)),
            RefitDefaults);

    [Fact]
    public void A_template_with_no_parameters_omits_the_components_key()
    {
        // The failure this test exists for. "components": null is not the same as no components,
        // and only one of them sends.
        var json = Serialise(components: null);

        json.Should().NotContain("components");
    }

    [Fact]
    public void The_payload_matches_the_request_meta_accepted_by_hand()
    {
        // Compared field by field against the curl that delivered to a real handset.
        var json = Serialise(components: null);
        var payload = JsonDocument.Parse(json).RootElement;

        payload.GetProperty("messaging_product").GetString().Should().Be("whatsapp");
        payload.GetProperty("to").GetString().Should().Be("923367890092");
        payload.GetProperty("type").GetString().Should().Be("template");

        var template = payload.GetProperty("template");

        template.GetProperty("name").GetString().Should().Be("hello_world");
        template.GetProperty("language").GetProperty("code").GetString().Should().Be("en_US");
        template.TryGetProperty("components", out _).Should().BeFalse();
    }

    [Fact]
    public void A_template_with_parameters_still_carries_them()
    {
        // The other half: omitting the key must depend on there being nothing to send, not on the
        // property being awkward to serialise.
        var json = Serialise([new TemplateComponent("body", [new TemplateParameter("John Doe")])]);

        json.Should().Contain("components").And.Contain("John Doe");
    }
}
