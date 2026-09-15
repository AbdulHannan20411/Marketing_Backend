using System.Text.Json.Serialization;
using Marketing.Application.Interfaces;

namespace Marketing.Infrastructure.WhatsApp.Models;

/// <summary>Body of a template create or edit call.</summary>
/// <param name="Name">Template name. Sent only on create: Meta never renames a template.</param>
/// <param name="Language">Language code. Sent only on create, for the same reason.</param>
/// <param name="Category"><c>MARKETING</c> or <c>UTILITY</c>; omitted on an edit that does not change it.</param>
/// <param name="Components">Header, body, footer and buttons, in that order.</param>
public sealed record TemplateDefinitionRequest(
    [property: JsonPropertyName("name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Name,
    [property: JsonPropertyName("language")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Language,
    [property: JsonPropertyName("category")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Category,
    [property: JsonPropertyName("components")] IReadOnlyList<TemplateDefinitionComponent> Components)
{
    /// <summary>The body that creates a template and submits it for review.</summary>
    /// <param name="definition">What the template says.</param>
    public static TemplateDefinitionRequest ForCreate(MetaTemplateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new TemplateDefinitionRequest(
            definition.Name,
            definition.Language,
            definition.Category,
            ComponentsOf(definition));
    }

    /// <summary>The body that replaces a submitted template's content, which resubmits it.</summary>
    /// <param name="definition">What the template now says.</param>
    /// <param name="includeCategory">Whether the category changed and must be sent.</param>
    public static TemplateDefinitionRequest ForEdit(MetaTemplateDefinition definition, bool includeCategory)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new TemplateDefinitionRequest(
            Name: null,
            Language: null,
            includeCategory ? definition.Category : null,
            ComponentsOf(definition));
    }

    private static List<TemplateDefinitionComponent> ComponentsOf(MetaTemplateDefinition definition)
    {
        var components = new List<TemplateDefinitionComponent>(4);

        if (definition.HeaderText is { Length: > 0 } header)
        {
            components.Add(new TemplateDefinitionComponent("HEADER", Format: "TEXT", Text: header));
        }

        components.Add(new TemplateDefinitionComponent(
            "BODY",
            Text: definition.BodyText,

            // Only when there are placeholders to illustrate. A body without any needs no example.
            Example: definition.BodyExamples.Count > 0 ? new TemplateBodyExample([definition.BodyExamples]) : null));

        if (definition.FooterText is { Length: > 0 } footer)
        {
            components.Add(new TemplateDefinitionComponent("FOOTER", Text: footer));
        }

        if (definition.Buttons.Count > 0)
        {
            components.Add(new TemplateDefinitionComponent(
                "BUTTONS",
                Buttons: [.. definition.Buttons.Select(button =>
                    new TemplateDefinitionButton(button.Type, button.Text, button.Url, button.PhoneNumber))]));
        }

        return components;
    }
}

/// <summary>One component of a template definition.</summary>
/// <param name="Type"><c>HEADER</c>, <c>BODY</c>, <c>FOOTER</c> or <c>BUTTONS</c>.</param>
/// <param name="Format">Header format; <c>TEXT</c> is the only one submitted.</param>
/// <param name="Text">Component text, with placeholders such as <c>{{1}}</c> verbatim.</param>
/// <param name="Example">Example values for the body's placeholders.</param>
/// <param name="Buttons">The buttons, for a <c>BUTTONS</c> component.</param>
public sealed record TemplateDefinitionComponent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("format")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Format = null,
    [property: JsonPropertyName("text")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Text = null,
    [property: JsonPropertyName("example")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    TemplateBodyExample? Example = null,
    [property: JsonPropertyName("buttons")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<TemplateDefinitionButton>? Buttons = null);

/// <summary>Example values for a body's placeholders.</summary>
/// <param name="BodyText">One set of examples, one value per placeholder in number order.</param>
public sealed record TemplateBodyExample(
    [property: JsonPropertyName("body_text")] IReadOnlyList<IReadOnlyList<string>> BodyText);

/// <summary>One button in a template definition.</summary>
/// <param name="Type"><c>QUICK_REPLY</c>, <c>URL</c> or <c>PHONE_NUMBER</c>.</param>
/// <param name="Text">Button label.</param>
/// <param name="Url">Web address, for a <c>URL</c> button.</param>
/// <param name="PhoneNumber">Phone number, for a <c>PHONE_NUMBER</c> button.</param>
public sealed record TemplateDefinitionButton(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("url")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Url = null,
    [property: JsonPropertyName("phone_number")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PhoneNumber = null);
