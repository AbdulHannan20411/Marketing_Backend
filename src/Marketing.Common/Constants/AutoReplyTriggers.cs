using System.Text.RegularExpressions;

namespace Marketing.Common.Constants;

/// <summary>
/// The three occasions an AI reply can be sent on, and which of them a plan unlocks.
/// </summary>
/// <remarks>
/// Modelled like <see cref="PlanModules"/>: a map with every key always present, so the client can
/// read <c>triggers.greeting</c> without a null check, and a plan can sell the cheap triggers without
/// the expensive one. Greeting and first-message answer a handful of messages a day; unanswered
/// answers everything nobody got to, which is a different order of cost.
/// </remarks>
public static partial class AutoReplyTriggers
{
    /// <summary>The customer said hello and nothing more.</summary>
    public const string Greeting = "greeting";

    /// <summary>The first message of a brand new conversation, whatever it says.</summary>
    public const string FirstMessage = "first_message";

    /// <summary>Any customer message nobody answered within the workspace's chosen wait.</summary>
    public const string Unanswered = "unanswered";

    /// <summary>All three keys, in the order the settings screen renders them.</summary>
    public static readonly IReadOnlyList<string> All = [Greeting, FirstMessage, Unanswered];

    /// <summary>Expands a stored list into the full map, absent keys false.</summary>
    /// <param name="enabled">Trigger keys that are switched on.</param>
    public static IReadOnlyDictionary<string, bool> Expand(IEnumerable<string>? enabled)
    {
        var set = new HashSet<string>(enabled ?? [], StringComparer.OrdinalIgnoreCase);

        return All.ToDictionary(key => key, set.Contains, StringComparer.Ordinal);
    }

    /// <summary>Reduces a map back to the enabled keys, ignoring anything unknown.</summary>
    /// <param name="triggers">Map supplied by a caller.</param>
    public static List<string> Collapse(IReadOnlyDictionary<string, bool>? triggers)
    {
        if (triggers is null)
        {
            return [];
        }

        return [.. All.Where(key => triggers.TryGetValue(key, out var enabled) && enabled)];
    }

    /// <summary>
    /// Whether a message is a greeting and nothing else.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. "Hi" deserves an automatic hello; "hi, where is my order" is a question,
    /// and answering it with a greeting is worse than staying silent. Anything longer than a few words
    /// is therefore not a greeting, whatever it starts with.
    /// <para>
    /// Urdu and Roman Urdu greetings are included because this platform's customers are in Pakistan:
    /// a customer writing "aoa" is saying hello exactly as much as one writing "hello".
    /// </para>
    /// </remarks>
    /// <param name="body">What the customer wrote.</param>
    public static bool IsGreeting(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        // Punctuation and emoji removed first: "Hi!!" and "hello 👋" are greetings, and a literal
        // comparison would miss both.
        var cleaned = NonLetters().Replace(body, " ").Trim().ToLowerInvariant();

        if (cleaned.Length == 0)
        {
            return false;
        }

        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // A greeting is short. Past four words it is a sentence, and a sentence usually wants a person.
        if (words.Length > 4)
        {
            return false;
        }

        return words.All(word => Words.Contains(word));
    }

    /// <summary>
    /// Greeting words, including the Roman Urdu and Urdu forms customers here actually type.
    /// </summary>
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "hi", "hii", "hiii", "hy", "hey", "heyy", "hello", "helo", "hlo", "hallo",
        "good", "morning", "afternoon", "evening", "day", "greetings",
        "salam", "salaam", "asalam", "assalam", "assalamu", "alaikum", "alaykum", "aleikum",
        "alekum", "walaikum", "walekum", "walaikumsalam", "aoa", "slam", "sa",

        // Connectors people write between the two halves of "assalam o alaikum".
        "o", "u", "wa", "w",
        "السلام", "علیکم", "ہیلو", "سلام",
        "there", "sir", "madam", "bro", "dear",
    };

    // Anything that is not a letter or a digit is a separator, which strips punctuation and emoji
    // while leaving Urdu script intact.
    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonLetters();
}
