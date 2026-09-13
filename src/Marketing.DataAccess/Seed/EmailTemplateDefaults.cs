using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Marketing.DataAccess.Seed;

/// <summary>A variable a shipped template declares.</summary>
/// <param name="Name">Variable name.</param>
/// <param name="Description">What the value is.</param>
/// <param name="Sample">Used by the live preview and by test sends. Never real customer data.</param>
public sealed record EmailTemplateVariableDefault(string Name, string Description, string Sample);

/// <summary>A template exactly as shipped.</summary>
/// <param name="Key">Stable identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">What the email is for.</param>
/// <param name="Category">Editor grouping.</param>
/// <param name="Subject">Subject line template.</param>
/// <param name="HtmlBody">HTML body template.</param>
/// <param name="TextBody">Plain-text body template.</param>
/// <param name="Variables">The template's own variables.</param>
public sealed record EmailTemplateDefault(
    string Key,
    string Name,
    string Description,
    string Category,
    string Subject,
    string HtmlBody,
    string TextBody,
    IReadOnlyList<EmailTemplateVariableDefault> Variables);

/// <summary>
/// The email templates shipped with the platform.
/// </summary>
/// <remarks>
/// Read from a JSON resource embedded in this assembly, exported from the web client so both sides
/// work from one source. Used to seed the database, and as the fallback when a stored template is
/// missing or cannot be parsed - a broken edit must never stop a password reset going out.
/// </remarks>
public static class EmailTemplateDefaults
{
    /// <summary>Manifest name of the embedded seed.</summary>
    public const string ResourceName = "Marketing.DataAccess.Seed.EmailTemplates.email-templates.seed.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<IReadOnlyList<EmailTemplateDefault>> Loaded = new(Load);

    /// <summary>Every shipped template, in the order the editor lists them.</summary>
    public static IReadOnlyList<EmailTemplateDefault> All => Loaded.Value;

    /// <summary>Returns the shipped default for a key, or null when there is none.</summary>
    /// <param name="key">Template key.</param>
    public static EmailTemplateDefault? Find(string key) =>
        All.FirstOrDefault(template => string.Equals(template.Key, key, StringComparison.Ordinal));

    /// <summary>Position of a key in the shipped order, or <see cref="int.MaxValue"/> when it is not shipped.</summary>
    /// <param name="key">Template key.</param>
    public static int OrderOf(string key)
    {
        for (var index = 0; index < All.Count; index++)
        {
            if (string.Equals(All[index].Key, key, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    /// <summary>
    /// Hashes a template's three editable parts.
    /// </summary>
    /// <remarks>
    /// The parts are joined with a separator no template contains. Without one, text moving from the
    /// end of the subject to the start of the body would hash the same, and an edit could read as
    /// unedited.
    /// </remarks>
    /// <param name="subject">Subject template.</param>
    /// <param name="htmlBody">HTML body template.</param>
    /// <param name="textBody">Plain-text body template.</param>
    public static string Hash(string subject, string htmlBody, string textBody)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(htmlBody);
        ArgumentNullException.ThrowIfNull(textBody);

        var bytes = Encoding.UTF8.GetBytes(string.Join('\0', subject, htmlBody, textBody));

        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Serialises a variable list for storage.</summary>
    /// <param name="variables">The variables.</param>
    public static string SerialiseVariables(IReadOnlyList<EmailTemplateVariableDefault> variables) =>
        JsonSerializer.Serialize(variables, Json);

    /// <summary>Reads a stored variable list back.</summary>
    /// <param name="json">Stored JSON.</param>
    public static IReadOnlyList<EmailTemplateVariableDefault> DeserialiseVariables(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<EmailTemplateVariableDefault>>(json, Json) ?? [];

    private static List<EmailTemplateDefault> Load()
    {
        var assembly = typeof(EmailTemplateDefaults).Assembly;

        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The shipped email templates resource \"{ResourceName}\" is missing from {assembly.GetName().Name}.");

        var file = JsonSerializer.Deserialize<EmailTemplateSeedFile>(stream, Json);

        if (file?.Templates is not { Count: > 0 } templates)
        {
            throw new InvalidOperationException("The shipped email templates resource contains no templates.");
        }

        return templates;
    }
}

/// <summary>Shape of the embedded seed file.</summary>
/// <param name="Version">Format version.</param>
/// <param name="Templates">The templates.</param>
internal sealed record EmailTemplateSeedFile(int Version, List<EmailTemplateDefault> Templates);
