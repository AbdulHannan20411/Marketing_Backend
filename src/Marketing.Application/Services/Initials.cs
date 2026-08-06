namespace Marketing.Application.Services;

/// <summary>
/// Derives display initials from a name.
/// <para>
/// Computed server-side rather than in the client so avatars are identical everywhere the name
/// appears - list, detail, audit trail - and so the rule for awkward names lives in one place.
/// </para>
/// </summary>
public static class Initials
{
    /// <summary>Returns up to two uppercase initials.</summary>
    /// <param name="name">Full name.</param>
    public static string From(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length switch
        {
            0 => "?",

            // A single name yields two letters rather than one: "Prince" reads better as PR than P,
            // and a one-letter avatar looks like a rendering bug.
            1 => parts[0].Length >= 2
                ? parts[0][..2].ToUpperInvariant()
                : parts[0].ToUpperInvariant(),

            // First and last, skipping any middle names.
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}",
        };
    }
}
