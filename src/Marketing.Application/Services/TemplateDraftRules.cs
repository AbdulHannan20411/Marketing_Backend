using System.Globalization;
using System.Text.RegularExpressions;
using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.Interfaces;
using Marketing.Common.Exceptions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <summary>Checks a template draft against Meta's rules, and turns it into what Meta is sent.</summary>
/// <remarks>
/// Checked before anything reaches Meta. Meta enforces the same rules, but its refusal arrives after a
/// round trip, names one problem at a time, and is sometimes worded for developers. The limits match
/// the template editor's, so the client and the server refuse the same drafts.
/// </remarks>
public static partial class TemplateDraftRules
{
    /// <summary>Longest template name the database column holds.</summary>
    public const int NameMaxLength = 120;

    /// <summary>Meta's limit on header text.</summary>
    public const int HeaderMaxLength = 60;

    /// <summary>Meta's limit on body text.</summary>
    public const int BodyMaxLength = 1024;

    /// <summary>Meta's limit on footer text.</summary>
    public const int FooterMaxLength = 60;

    /// <summary>Meta's limit on a button label.</summary>
    public const int ButtonLabelMaxLength = 25;

    /// <summary>Most buttons Meta allows on one template.</summary>
    public const int MaxButtons = 10;

    /// <summary>Most link buttons Meta allows on one template.</summary>
    public const int MaxUrlButtons = 2;

    /// <summary>Most call buttons Meta allows on one template.</summary>
    public const int MaxPhoneButtons = 1;

    /// <summary>Meta's limit on a button's web address.</summary>
    public const int UrlMaxLength = 2000;

    private const string HeaderKindNone = "none";
    private const string HeaderKindText = "text";

    /// <summary>Throws a validation error listing every rule the draft breaks.</summary>
    /// <param name="draft">The draft to check.</param>
    /// <exception cref="ValidationException">One or more rules are broken.</exception>
    public static void Validate(MessageTemplateDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var messages))
            {
                messages = [];
                errors[field] = messages;
            }

            if (!messages.Contains(message, StringComparer.Ordinal))
            {
                messages.Add(message);
            }
        }

        var name = draft.Name?.Trim() ?? string.Empty;

        if (!NamePattern().IsMatch(name))
        {
            Add("name", "Use lowercase letters, numbers and underscores only.");
        }
        else if (name.Length > NameMaxLength)
        {
            Add("name", $"Keep the name to {Invariant(NameMaxLength)} characters or fewer.");
        }

        if (!LanguagePattern().IsMatch(draft.Language?.Trim() ?? string.Empty))
        {
            Add("language", "Use a WhatsApp language code, such as en_US.");
        }

        if (draft.Category == TemplateCategory.Authentication)
        {
            Add(
                "category",
                "Authentication templates use a fixed format that Meta generates. Create them in WhatsApp Manager.");
        }

        switch (HeaderKindOf(draft))
        {
            case HeaderKindNone:
                break;

            case HeaderKindText:
            {
                var header = draft.HeaderText?.Trim() ?? string.Empty;

                if (header.Length == 0)
                {
                    Add("headerText", "Enter the header text, or choose no header.");
                }
                else if (header.Length > HeaderMaxLength)
                {
                    Add("headerText", $"Keep the header to {Invariant(HeaderMaxLength)} characters or fewer.");
                }
                else if (Placeholder().IsMatch(header))
                {
                    Add("headerText", "Placeholders are not supported in the header yet.");
                }

                break;
            }

            default:
                // A media header needs a sample file uploaded to Meta with the template, which this
                // endpoint cannot do yet. Refused plainly rather than submitted without one.
                Add(
                    "headerKind",
                    "Image, video and document headers cannot be submitted from here yet. Use a text header or none.");
                break;
        }

        var body = draft.BodyText?.Trim() ?? string.Empty;

        if (body.Length == 0)
        {
            Add("bodyText", "The body cannot be empty.");
        }
        else
        {
            if (body.Length > BodyMaxLength)
            {
                Add("bodyText", $"Keep the body to {Invariant(BodyMaxLength)} characters or fewer.");
            }

            if (OnlyPlaceholder().IsMatch(body))
            {
                Add("bodyText", "The body cannot be a single placeholder on its own.");
            }

            var numbers = PlaceholderNumbers(body);

            for (var index = 0; index < numbers.Count; index++)
            {
                if (numbers[index] != index + 1)
                {
                    Add("bodyText", "Placeholders must run {{1}} to {{" + Invariant(numbers.Count) + "}} with no gaps.");
                    break;
                }
            }
        }

        var footer = draft.FooterText?.Trim() ?? string.Empty;

        if (footer.Length > FooterMaxLength)
        {
            Add("footerText", $"Keep the footer to {Invariant(FooterMaxLength)} characters or fewer.");
        }
        else if (Placeholder().IsMatch(footer))
        {
            Add("footerText", "Placeholders are not allowed in the footer.");
        }

        ValidateButtons(draft.Buttons ?? [], Add);

        if (errors.Count > 0)
        {
            throw new ValidationException(
                errors.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal));
        }
    }

    /// <summary>What Meta is sent for a draft that has passed <see cref="Validate"/>.</summary>
    /// <param name="draft">A valid draft.</param>
    public static MetaTemplateDefinition ToDefinition(MessageTemplateDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var body = draft.BodyText.Trim();
        var variableCount = PlaceholderNumbers(body).Count;

        return new MetaTemplateDefinition(
            draft.Name.Trim(),
            draft.Language.Trim(),
            draft.Category.ToString().ToUpperInvariant(),
            HeaderKindOf(draft) == HeaderKindText ? draft.HeaderText?.Trim() : null,
            body,

            // Meta wants an example for every placeholder before it will review a template. The editor
            // does not collect examples, so each is labelled plainly rather than invented.
            [.. Enumerable.Range(1, variableCount).Select(number => "Sample " + Invariant(number))],
            string.IsNullOrWhiteSpace(draft.FooterText) ? null : draft.FooterText.Trim(),
            [.. (draft.Buttons ?? []).Select(ToMetaButton)]);
    }

    /// <summary>The placeholders a body uses, as <c>{{1}}</c>, <c>{{2}}</c> and so on, in number order.</summary>
    /// <param name="body">Template body text.</param>
    public static IReadOnlyList<string> VariablesOf(string body) =>
        [.. PlaceholderNumbers(body ?? string.Empty).Select(number => "{{" + Invariant(number) + "}}")];

    private static void ValidateButtons(IReadOnlyList<TemplateButtonDraft> buttons, Action<string, string> add)
    {
        if (buttons.Count > MaxButtons)
        {
            add("buttons", $"Use {Invariant(MaxButtons)} buttons or fewer.");
        }

        var links = 0;
        var calls = 0;

        foreach (var button in buttons)
        {
            if (button is null)
            {
                add("buttons", "Every button needs a label.");
                continue;
            }

            var label = button.Label?.Trim() ?? string.Empty;

            if (label.Length == 0 || label.Length > ButtonLabelMaxLength)
            {
                add("buttons", $"Every button needs a label of 1 to {Invariant(ButtonLabelMaxLength)} characters.");
            }

            switch (KindOf(button))
            {
                case TemplateButtonDraft.QuickReply:
                    break;

                case TemplateButtonDraft.Url:
                    links++;

                    if (!IsWebAddress(button.Value))
                    {
                        add("buttons", $"\"{label}\" needs a full web address starting with https:// or http://.");
                    }

                    break;

                case TemplateButtonDraft.PhoneNumber:
                    calls++;

                    if (!PhonePattern().IsMatch(button.Value?.Trim() ?? string.Empty))
                    {
                        add("buttons", $"\"{label}\" needs a phone number in international format, such as +14155550123.");
                    }

                    break;

                default:
                    add("buttons", "Buttons can be quick replies, links or phone numbers.");
                    break;
            }
        }

        if (links > MaxUrlButtons)
        {
            add("buttons", $"Use {Invariant(MaxUrlButtons)} link buttons or fewer.");
        }

        if (calls > MaxPhoneButtons)
        {
            add("buttons", "Use one call button at most.");
        }
    }

    private static MetaTemplateButton ToMetaButton(TemplateButtonDraft button)
    {
        var kind = KindOf(button);

        return kind switch
        {
            TemplateButtonDraft.Url => new MetaTemplateButton("URL", button.Label.Trim(), Url: button.Value?.Trim()),
            TemplateButtonDraft.PhoneNumber =>
                new MetaTemplateButton("PHONE_NUMBER", button.Label.Trim(), PhoneNumber: button.Value?.Trim()),
            _ => new MetaTemplateButton("QUICK_REPLY", button.Label.Trim()),
        };
    }

    /// <summary>The button's kind in its canonical spelling, or null when it is not one Meta supports.</summary>
    private static string? KindOf(TemplateButtonDraft button)
    {
        var kind = button.Kind?.Trim();

        foreach (var known in (string[])[TemplateButtonDraft.QuickReply, TemplateButtonDraft.Url, TemplateButtonDraft.PhoneNumber])
        {
            if (string.Equals(kind, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return null;
    }

    /// <summary>
    /// What the draft's header carries, as the value stored against the template.
    /// </summary>
    /// <remarks>
    /// Kept so a campaign knows whether the template needs a file attached. A template synced from
    /// Meta has no header kind of its own here - Meta's list call does not return components - so it
    /// stays <c>None</c> until someone edits it through this platform.
    /// </remarks>
    /// <param name="draft">The draft being saved.</param>
    public static TemplateHeaderKind HeaderKindFor(MessageTemplateDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return HeaderKindOf(draft) switch
        {
            HeaderKindText => TemplateHeaderKind.Text,
            "image" => TemplateHeaderKind.Image,
            "video" => TemplateHeaderKind.Video,
            "document" => TemplateHeaderKind.Document,
            _ => TemplateHeaderKind.None,
        };
    }

    /// <summary>The header kind, inferred from the header text when a client does not send one.</summary>
    private static string HeaderKindOf(MessageTemplateDraft draft)
    {
        var kind = draft.HeaderKind?.Trim();

        if (string.IsNullOrEmpty(kind))
        {
            return string.IsNullOrWhiteSpace(draft.HeaderText) ? HeaderKindNone : HeaderKindText;
        }

        if (string.Equals(kind, HeaderKindNone, StringComparison.OrdinalIgnoreCase))
        {
            return HeaderKindNone;
        }

        return string.Equals(kind, HeaderKindText, StringComparison.OrdinalIgnoreCase) ? HeaderKindText : kind;
    }

    private static bool IsWebAddress(string? value) =>
        value?.Trim() is { Length: > 0 and <= UrlMaxLength } trimmed
        && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static List<int> PlaceholderNumbers(string text) =>
        [.. Placeholder().Matches(text)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct()
            .Order()];

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex NamePattern();

    // Meta's codes: a two- or three-letter language, optionally a region - en, en_US, pt_BR, es_LA.
    [GeneratedRegex("^[a-z]{2,3}(_[A-Za-z]{2,4})?$")]
    private static partial Regex LanguagePattern();

    [GeneratedRegex(@"\{\{\s*(\d{1,4})\s*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^\{\{\s*\d{1,4}\s*\}\}$")]
    private static partial Regex OnlyPlaceholder();

    [GeneratedRegex(@"^\+?[1-9]\d{6,19}$")]
    private static partial Regex PhonePattern();
}
