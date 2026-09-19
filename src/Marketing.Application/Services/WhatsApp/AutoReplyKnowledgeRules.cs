using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Entities;

namespace Marketing.Application.Services.WhatsApp;

/// <summary>
/// The rules a knowledge upload has to meet. The client applies the same ones first; these are the
/// ones that count.
/// </summary>
/// <remarks>
/// Every string ends up in a model prompt and then in a WhatsApp message, so each is treated as plain
/// text: markup is stripped and so is every control character except a line break.
/// </remarks>
public static partial class AutoReplyKnowledgeRules
{
    /// <summary>A fact about the business.</summary>
    public const string Business = "business";

    /// <summary>A question and its answer.</summary>
    public const string Faq = "faq";

    /// <summary>A product or service.</summary>
    public const string Product = "product";

    /// <summary>A policy.</summary>
    public const string Policy = "policy";

    /// <summary>Something the assistant must always or never do.</summary>
    public const string Rule = "rule";

    /// <summary>Send the holding message when the file has no answer.</summary>
    public const string Handoff = "handoff";

    /// <summary>Send nothing when the file has no answer.</summary>
    public const string Silent = "silent";

    /// <summary>The holding message a workspace starts with.</summary>
    public const string DefaultFallbackMessage =
        "Thanks for your message! Someone from our team will get back to you shortly.";

    /// <summary>Most entries one upload may carry.</summary>
    public const int MaximumEntries = 500;

    private const int TitleLength = 200;
    private const int AnswerLength = 1000;
    private const int PriceLength = 60;
    private const int MaximumKeywords = 10;
    private const int KeywordLength = 60;
    private const int FallbackMessageLength = 300;
    private const int FileNameLength = 255;

    private static readonly string[] Kinds = [Business, Faq, Product, Policy, Rule];

    /// <summary>A validated upload, ready to store.</summary>
    /// <param name="Entries">Entries in upload order, cleaned.</param>
    /// <param name="Fallback">The fallback.</param>
    /// <param name="FallbackMessage">The holding message, cleaned.</param>
    /// <param name="SourceFileName">The file name without any path, or null.</param>
    public sealed record ValidatedKnowledge(
        IReadOnlyList<AutoReplyKnowledgeEntry> Entries,
        string Fallback,
        string FallbackMessage,
        string? SourceFileName);

    /// <summary>Checks and cleans an upload, or refuses it with every problem keyed by field.</summary>
    /// <param name="request">The upload.</param>
    /// <exception cref="ValidationException">Anything is wrong with it.</exception>
    public static ValidatedKnowledge Validate(AutoReplyKnowledgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var incoming = request.Entries ?? [];

        if (incoming.Count > MaximumEntries)
        {
            errors["entries"] =
                [$"The file has {incoming.Count} entries; the most one file can hold is {MaximumEntries}. Split it or remove some rows."];
        }

        var entries = new List<AutoReplyKnowledgeEntry>(Math.Min(incoming.Count, MaximumEntries));
        var seen = new Dictionary<(string Kind, string Title), int>();

        for (var index = 0; index < incoming.Count && incoming.Count <= MaximumEntries; index++)
        {
            var entry = incoming[index] ?? new KnowledgeEntryDto(null, null, null, null, null, null);
            var key = $"entries[{index}]";
            var title = Clean(entry.Title, multiline: false);
            var name = $"Entry {index + 1}{(title.Length > 0 ? $" (\"{Shorten(title)}\")" : string.Empty)}";
            var kind = entry.Kind?.Trim().ToLowerInvariant() ?? string.Empty;

            if (!Kinds.Contains(kind, StringComparer.Ordinal))
            {
                errors[$"{key}.kind"] =
                    [$"{name}: \"Type\" must be business, faq, product, policy or rule."];
                continue;
            }

            if (title.Length == 0)
            {
                errors[$"{key}.title"] = [$"{name}: \"Title\" is empty."];
            }
            else if (title.Length > TitleLength)
            {
                errors[$"{key}.title"] = [$"{name}: \"Title\" is over {TitleLength} characters."];
            }
            else if (seen.TryGetValue((kind, title.ToLowerInvariant()), out var first))
            {
                errors[$"{key}.title"] =
                    [$"{name}: the same {kind} title is already in entry {first + 1}. Keep one of them."];
            }
            else
            {
                seen[(kind, title.ToLowerInvariant())] = index;
            }

            var answer = Clean(entry.Answer, multiline: true);

            if (answer.Length == 0 && kind != Rule)
            {
                errors[$"{key}.answer"] = [$"{name}: \"Answer or details\" is empty."];
            }
            else if (answer.Length > AnswerLength)
            {
                errors[$"{key}.answer"] = [$"{name}: \"Answer or details\" is over {AnswerLength} characters."];
            }

            string? price = null;

            if (kind == Product && Clean(entry.Price, multiline: false) is { Length: > 0 } written)
            {
                if (written.Length > PriceLength)
                {
                    errors[$"{key}.price"] = [$"{name}: \"Price\" is over {PriceLength} characters."];
                }

                price = written;
            }

            var keywords = new List<string>();

            foreach (var raw in entry.Keywords ?? [])
            {
                var keyword = Clean(raw, multiline: false);

                if (keyword.Length > 0 && !keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                {
                    keywords.Add(keyword);
                }
            }

            if (keywords.Count > MaximumKeywords)
            {
                errors[$"{key}.keywords"] = [$"{name}: \"Keywords\" has {keywords.Count}; keep it to {MaximumKeywords}."];
            }
            else if (keywords.FirstOrDefault(keyword => keyword.Length > KeywordLength) is { } tooLong)
            {
                errors[$"{key}.keywords"] =
                    [$"{name}: the keyword \"{Shorten(tooLong)}\" is over {KeywordLength} characters."];
            }

            entries.Add(new AutoReplyKnowledgeEntry
            {
                SortOrder = index,
                Kind = kind,
                Title = title,
                Answer = answer,
                Price = price,
                Available = kind == Product ? entry.Available : null,
                Keywords = keywords,
            });
        }

        var fallback = request.Fallback?.Trim().ToLowerInvariant() ?? Handoff;

        if (fallback is not (Handoff or Silent))
        {
            errors["fallback"] = ["When the file has no answer, choose either to send a holding message or to stay silent."];
        }

        var message = Clean(request.FallbackMessage, multiline: true);

        if (fallback == Handoff && message.Length == 0)
        {
            errors["fallbackMessage"] = ["Write the holding message customers get when the file has no answer."];
        }
        else if (message.Length > FallbackMessageLength)
        {
            errors["fallbackMessage"] = [$"Keep the holding message to {FallbackMessageLength} characters or fewer."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new ValidatedKnowledge(entries, fallback, message, FileName(request.SourceFileName));
    }

    /// <summary>
    /// Plain text only: markup removed, control characters removed except line breaks, trimmed.
    /// </summary>
    /// <param name="value">Text as supplied.</param>
    /// <param name="multiline">Whether line breaks are kept; titles and prices become one line.</param>
    public static string Clean(string? value, bool multiline)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var withoutMarkup = Markup().Replace(value.Replace("\r\n", "\n", StringComparison.Ordinal), string.Empty);
        var builder = new StringBuilder(withoutMarkup.Length);

        foreach (var character in withoutMarkup)
        {
            if (character == '\n')
            {
                builder.Append(multiline ? '\n' : ' ');
            }
            else if (character == '\t')
            {
                builder.Append(' ');
            }
            else if (!char.IsControl(character) && CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.Format)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>The file's own name: no folders, no characters that could not be shown.</summary>
    private static string? FileName(string? value)
    {
        var cleaned = Clean(value, multiline: false);
        var separator = cleaned.LastIndexOfAny(['/', '\\']);
        var name = (separator >= 0 ? cleaned[(separator + 1)..] : cleaned).Trim();

        return name.Length == 0 ? null : name.Length <= FileNameLength ? name : name[..FileNameLength];
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";

    [GeneratedRegex(@"</?[A-Za-z][^<>]*>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Markup();
}
