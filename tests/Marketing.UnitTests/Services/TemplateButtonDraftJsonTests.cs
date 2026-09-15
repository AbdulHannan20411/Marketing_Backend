using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Marketing.Application.DTOs.Campaigns;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

/// <summary>Reading template buttons in both shapes a client may send.</summary>
/// <remarks>
/// Written after finding the template editor sends buttons as objects while the contract declared
/// strings, so any template with a button failed to deserialise before reaching the service.
/// </remarks>
public sealed class TemplateButtonDraftJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void The_editors_draft_deserialises_with_its_buttons()
    {
        const string json =
            """
            {"name":"order_update","category":"utility","language":"en_US","headerKind":"text",
             "headerText":"Order update","bodyText":"Hi {{1}}","footerText":"",
             "buttons":[{"kind":"url","label":"Track","value":"https://example.com/track"},
                        {"kind":"quick_reply","label":"Thanks","value":""}]}
            """;

        var draft = JsonSerializer.Deserialize<MessageTemplateDraft>(json, Web)!;

        draft.Category.Should().Be(TemplateCategory.Utility);
        draft.HeaderKind.Should().Be("text");
        draft.Buttons.Should().Equal(
            new TemplateButtonDraft("url", "Track", "https://example.com/track"),
            new TemplateButtonDraft("quick_reply", "Thanks", ""));
    }

    [Fact]
    public void A_bare_label_is_a_quick_reply()
    {
        var buttons = JsonSerializer.Deserialize<List<TemplateButtonDraft>>("""["Thanks"]""", Web);

        buttons.Should().Equal(new TemplateButtonDraft("quick_reply", "Thanks"));
    }
}
