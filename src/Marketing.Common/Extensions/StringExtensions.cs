using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Marketing.Common.Extensions;

/// <summary>String helpers used across layers.</summary>
public static partial class StringExtensions
{
    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex NonSlugCharacters();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex ConsecutiveWhitespace();

    /// <summary>Returns <see langword="null"/> when the value is null, empty or whitespace.</summary>
    public static string? NullIfWhiteSpace(this string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Trims and collapses runs of whitespace to a single space.</summary>
    public static string? Normalise(this string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : ConsecutiveWhitespace().Replace(value.Trim(), " ");

    /// <summary>
    /// Produces a lowercase, hyphen-separated slug. Used for tenant slugs, which must be stable,
    /// URL-safe and comparable without collation surprises.
    /// </summary>
    public static string ToSlug(this string value, int maxLength = 64)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        var slug = NonSlugCharacters()
            .Replace(builder.ToString().Normalize(NormalizationForm.FormC), "-")
            .Trim('-');

        return slug.Length <= maxLength ? slug : slug[..maxLength].TrimEnd('-');
    }

    /// <summary>
    /// Masks all but the last <paramref name="visibleSuffixLength"/> characters. Used when a token,
    /// phone number or secret has to appear in a log line or an admin screen.
    /// </summary>
    public static string Mask(this string? value, int visibleSuffixLength = 4)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= visibleSuffixLength)
        {
            return new string('*', value.Length);
        }

        return string.Concat(new string('*', value.Length - visibleSuffixLength), value[^visibleSuffixLength..]);
    }

    /// <summary>Normalises an email address for storage and comparison.</summary>
    public static string ToNormalisedEmail(this string value) => value.Trim().ToLowerInvariant();
}
