using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marketing.Application.DTOs.Campaigns;

/// <summary>One button on a template being created or edited.</summary>
/// <param name="Kind"><c>quick_reply</c>, <c>url</c> or <c>phone_number</c>.</param>
/// <param name="Label">Text shown on the button.</param>
/// <param name="Value">The link or phone number the button uses; empty for a quick reply.</param>
[JsonConverter(typeof(TemplateButtonDraftJsonConverter))]
public sealed record TemplateButtonDraft(string Kind, string Label, string? Value = null)
{
    /// <summary>A button that replies with its own label.</summary>
    public const string QuickReply = "quick_reply";

    /// <summary>A button that opens a web address.</summary>
    public const string Url = "url";

    /// <summary>A button that calls a phone number.</summary>
    public const string PhoneNumber = "phone_number";
}

/// <summary>Reads a template button as the editor's object, or as a bare label.</summary>
/// <remarks>
/// The template editor sends <c>{ kind, label, value }</c>, while the contract once declared buttons
/// as plain strings - so every template with a button was refused before it reached any code. Both
/// forms are accepted; a bare string is a quick reply.
/// </remarks>
public sealed class TemplateButtonDraftJsonConverter : JsonConverter<TemplateButtonDraft>
{
    /// <inheritdoc />
    public override TemplateButtonDraft? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new TemplateButtonDraft(TemplateButtonDraft.QuickReply, reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A template button must be a label, or an object with kind, label and value.");
        }

        var kind = TemplateButtonDraft.QuickReply;
        var label = string.Empty;
        string? value = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new TemplateButtonDraft(kind, label, value);
            }

            var property = reader.GetString();
            reader.Read();

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
                continue;
            }

            var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

            if (string.Equals(property, "kind", StringComparison.OrdinalIgnoreCase))
            {
                kind = text ?? kind;
            }
            else if (string.Equals(property, "label", StringComparison.OrdinalIgnoreCase))
            {
                label = text ?? label;
            }
            else if (string.Equals(property, "value", StringComparison.OrdinalIgnoreCase))
            {
                value = text;
            }
        }

        throw new JsonException("A template button object was not closed.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TemplateButtonDraft value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        writer.WriteString("label", value.Label);

        if (value.Value is null)
        {
            writer.WriteNull("value");
        }
        else
        {
            writer.WriteString("value", value.Value);
        }

        writer.WriteEndObject();
    }
}
