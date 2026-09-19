using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Marketing.Common.Constants;
using Marketing.DataAccess.Entities;

namespace Marketing.Application.Services.WhatsApp;

/// <summary>
/// Builds the prompt that answers from a workspace's knowledge file, and checks what comes back.
/// </summary>
/// <remarks>
/// The point of the file is that replies are accurate. The prompt says so, and the checks on the way
/// out enforce the one failure customers complain about most - a price the business never quoted.
/// </remarks>
public static partial class AutoReplyKnowledgePrompt
{
    /// <summary>What the model is told to write when the facts do not answer the question.</summary>
    public const string Unknown = "<<UNKNOWN>>";

    /// <summary>Longest reply sent; well inside WhatsApp's 4,096 characters, and readable on a phone.</summary>
    public const int ReplyMaxLength = 800;

    /// <summary>
    /// Characters of questions and products included whole, about 6,000 tokens. Past this, only the
    /// entries most relevant to the customer's message go in.
    /// </summary>
    private const int FullCatalogueBudget = 24_000;

    /// <summary>Questions and products included when the catalogue is too big to send whole.</summary>
    private const int RelevantEntries = 25;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "is", "are", "do", "does", "you", "your", "i", "me", "my", "we", "our", "to",
        "of", "and", "or", "for", "in", "on", "at", "it", "this", "that", "what", "how", "can", "have",
        "has", "be", "with", "please", "hi", "hello", "kya", "hai", "ka", "ki", "ke", "se", "mein", "aap",
        "ap", "ho", "hain", "koi", "ko",
    };

    /// <summary>The prompt, and the entries it put in front of the model.</summary>
    /// <param name="Text">The prompt.</param>
    /// <param name="Included">The entries in it - the only facts a reply may use.</param>
    public sealed record Prompt(string Text, IReadOnlyList<AutoReplyKnowledgeEntry> Included);

    /// <summary>Builds the prompt for one customer message.</summary>
    /// <param name="entries">The workspace's knowledge, in upload order.</param>
    /// <param name="customerMessage">The message being answered.</param>
    /// <param name="trigger">Which automatic-reply rule fired.</param>
    /// <param name="contactName">The customer's name, if known.</param>
    /// <param name="transcript">The conversation so far, oldest first.</param>
    public static Prompt Build(
        IReadOnlyList<AutoReplyKnowledgeEntry> entries,
        string customerMessage,
        string trigger,
        string contactName,
        IEnumerable<string> transcript)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(transcript);

        var included = Select(entries, customerMessage);
        var prompt = new StringBuilder();

        prompt.AppendLine(
            "You answer WhatsApp messages for a business, using only the facts listed below. Write the reply "
            + "itself and nothing else: no preamble, no quotation marks.");
        prompt.AppendLine();

        Section(prompt, "BUSINESS FACTS", included, AutoReplyKnowledgeRules.Business,
            entry => $"- {entry.Title}: {entry.Answer}");
        Section(prompt, "PRODUCTS AND SERVICES", included, AutoReplyKnowledgeRules.Product, RenderProduct);
        Section(prompt, "FAQ", included, AutoReplyKnowledgeRules.Faq,
            entry => $"- Q: {entry.Title} A: {entry.Answer}");
        Section(prompt, "POLICIES", included, AutoReplyKnowledgeRules.Policy,
            entry => $"- {entry.Title}: {entry.Answer}");
        Section(prompt, "RULES (always follow)", included, AutoReplyKnowledgeRules.Rule,
            entry => entry.Answer.Length > 0 ? $"- {entry.Title} {entry.Answer}" : $"- {entry.Title}");

        // Ours, not the tenant's, and last before the conversation so it is what the model reads most
        // recently.
        prompt.AppendLine("INSTRUCTIONS");
        prompt.AppendLine("- Answer only from the facts above.");
        prompt.AppendLine("- Never invent prices, times, availability, addresses or policies.");
        prompt.AppendLine(
            "- Reply in the customer's language (English, Urdu or Roman Urdu), briefly, the way a person "
            + "would on WhatsApp. No emoji unless the customer used one.");
        prompt.AppendLine(
            "- A product marked unavailable: say it is currently unavailable and do not take an order for it.");
        prompt.AppendLine("- Never claim to be a human, and never say you are an AI unless asked outright.");

        if (trigger == AutoReplyTriggers.Greeting)
        {
            prompt.AppendLine(
                "- The customer has only said hello. Greet them warmly, by the business name if it is listed, "
                + "and ask how you can help.");
        }
        else
        {
            prompt.AppendLine(
                CultureInfo.InvariantCulture,
                $"- If the facts do not answer the question, output exactly {Unknown} and nothing else.");
        }

        prompt.AppendLine();
        prompt.AppendLine(CultureInfo.InvariantCulture, $"Customer's name, if useful: {contactName}");
        prompt.AppendLine();
        prompt.AppendLine("Conversation so far, oldest first:");

        foreach (var line in transcript)
        {
            prompt.AppendLine(line);
        }

        prompt.AppendLine();
        prompt.Append("Reply:");

        return new Prompt(prompt.ToString(), included);
    }

    /// <summary>Whether the model said the facts do not cover the question.</summary>
    /// <param name="reply">What the model returned.</param>
    public static bool IsUnknown(string? reply) =>
        string.IsNullOrWhiteSpace(reply) || reply.Contains(Unknown, StringComparison.Ordinal);

    /// <summary>
    /// Whether the reply quotes a price no included entry mentions.
    /// </summary>
    /// <remarks>
    /// Cheap and deliberately strict: a currency sign or word next to a number must match a number
    /// that appears in an entry's price or answer. A reply that fails is never sent.
    /// </remarks>
    /// <param name="reply">The reply.</param>
    /// <param name="included">The entries the model was given.</param>
    public static bool HasInventedPrice(string reply, IReadOnlyList<AutoReplyKnowledgeEntry> included)
    {
        ArgumentNullException.ThrowIfNull(included);

        var quoted = PriceTokens().Matches(reply)
            .Select(match => Normalise(match.Groups["a"].Success ? match.Groups["a"].Value : match.Groups["b"].Value))
            .Where(number => number.Length > 0)
            .ToList();

        if (quoted.Count == 0)
        {
            return false;
        }

        var known = included
            .SelectMany(entry => Numbers().Matches($"{entry.Price} {entry.Answer} {entry.Title}"))
            .Select(match => Normalise(match.Value))
            .ToHashSet(StringComparer.Ordinal);

        return quoted.Any(number => !known.Contains(number));
    }

    /// <summary>The reply as sent: tidied and kept to a readable length.</summary>
    /// <param name="reply">What the model returned.</param>
    public static string Tidy(string reply)
    {
        var cleaned = AutoReplyKnowledgeRules.Clean(reply, multiline: true).Trim('"');

        if (cleaned.Length <= ReplyMaxLength)
        {
            return cleaned;
        }

        // At the last sentence end inside the limit where there is one, so the message does not stop
        // mid-word.
        var cut = cleaned[..ReplyMaxLength];
        var sentence = cut.LastIndexOfAny(['.', '!', '?', '\n']);

        return (sentence > ReplyMaxLength / 2 ? cut[..(sentence + 1)] : cut).TrimEnd();
    }

    /// <summary>
    /// A greeting that needs no model, from the business name when the file has one.
    /// </summary>
    /// <param name="entries">The workspace's knowledge.</param>
    public static string? Greeting(IReadOnlyList<AutoReplyKnowledgeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var name = entries.FirstOrDefault(entry =>
            entry.Kind == AutoReplyKnowledgeRules.Business
            && entry.Title.Contains("name", StringComparison.OrdinalIgnoreCase)
            && entry.Answer.Length is > 0 and <= 80)?.Answer;

        return name is null ? null : $"Hi! Welcome to {name} — how can we help?";
    }

    /// <summary>
    /// Every business fact, policy and rule; every question and product when they fit, otherwise the
    /// most relevant.
    /// </summary>
    private static List<AutoReplyKnowledgeEntry> Select(
        IReadOnlyList<AutoReplyKnowledgeEntry> entries,
        string customerMessage)
    {
        static bool IsCatalogue(AutoReplyKnowledgeEntry entry) =>
            entry.Kind is AutoReplyKnowledgeRules.Faq or AutoReplyKnowledgeRules.Product;

        var catalogue = entries.Where(IsCatalogue).ToList();
        var size = catalogue.Sum(entry => entry.Title.Length + entry.Answer.Length + (entry.Price?.Length ?? 0) + 16);

        if (size <= FullCatalogueBudget)
        {
            return [.. entries];
        }

        var words = Words(customerMessage);
        var message = customerMessage.ToLowerInvariant();

        var relevant = catalogue
            .Select(entry => (Entry: entry, Score: Score(entry, words, message)))
            .Where(pair => pair.Score > 0)
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Entry.SortOrder)
            .Take(RelevantEntries)
            .Select(pair => pair.Entry)
            .ToHashSet();

        return [.. entries.Where(entry => !IsCatalogue(entry) || relevant.Contains(entry))];
    }

    private static int Score(AutoReplyKnowledgeEntry entry, HashSet<string> words, string message)
    {
        var score = entry.Keywords.Count(keyword => message.Contains(keyword.ToLowerInvariant(), StringComparison.Ordinal)) * 5;

        score += Words(entry.Title).Count(word => Matches(word, words)) * 3;
        score += Words(entry.Answer).Count(word => Matches(word, words));

        return score;
    }

    /// <summary>
    /// The same word, or one the other starts with - "park" and "parking", "deliver" and "delivery" -
    /// once both are long enough that a shared start means a shared meaning.
    /// </summary>
    private static bool Matches(string word, HashSet<string> asked) =>
        asked.Contains(word)
        || asked.Any(candidate =>
            Math.Min(candidate.Length, word.Length) >= 4
            && (word.StartsWith(candidate, StringComparison.Ordinal) || candidate.StartsWith(word, StringComparison.Ordinal)));

    private static HashSet<string> Words(string text) =>
        [.. WordPattern().Matches(text.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(word => word.Length > 1 && !StopWords.Contains(word))];

    private static void Section(
        StringBuilder prompt,
        string heading,
        IEnumerable<AutoReplyKnowledgeEntry> entries,
        string kind,
        Func<AutoReplyKnowledgeEntry, string> render)
    {
        var lines = entries.Where(entry => entry.Kind == kind).Select(render).ToList();

        if (lines.Count == 0)
        {
            return;
        }

        prompt.AppendLine(heading);

        foreach (var line in lines)
        {
            prompt.AppendLine(line.Replace('\n', ' '));
        }

        prompt.AppendLine();
    }

    private static string RenderProduct(AutoReplyKnowledgeEntry entry)
    {
        var parts = new List<string> { entry.Title };

        if (entry.Price is { Length: > 0 } price)
        {
            parts.Add(price);
        }

        if (entry.Available is { } available)
        {
            parts.Add(available ? "available" : "currently unavailable");
        }

        return $"- {string.Join(" — ", parts)}. {entry.Answer}".TrimEnd();
    }

    /// <summary>Digits only, without separators or a trailing zero fraction: "2,500.00" and "2500" are one price.</summary>
    private static string Normalise(string number)
    {
        var digits = number.Replace(",", string.Empty, StringComparison.Ordinal);

        if (digits.Contains('.', StringComparison.Ordinal))
        {
            digits = digits.TrimEnd('0').TrimEnd('.');
        }

        return digits;
    }

    /// <summary>A currency marker before or after a number: Rs 2,500 · PKR2500 · $30 · 2500 rupees.</summary>
    [GeneratedRegex(
        @"(?:\b(?:rs|pkr|rupees?)\.?|\$|₨)\s*(?<a>\d[\d,]*(?:\.\d+)?)|(?<b>\d[\d,]*(?:\.\d+)?)\s*(?:(?:rs|pkr|rupees?)\b|/-)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex PriceTokens();

    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex Numbers();

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex WordPattern();
}
