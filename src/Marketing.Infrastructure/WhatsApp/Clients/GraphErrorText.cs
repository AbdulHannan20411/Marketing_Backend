using System.Text;
using System.Text.RegularExpressions;

namespace Marketing.Infrastructure.WhatsApp.Clients;

/// <summary>Makes a Graph API error message safe to write to a log.</summary>
/// <remarks>
/// Meta's error text routinely echoes the request back - a recipient's phone number, sometimes a
/// token. Phone-like digit runs keep only their last two digits and tokens are replaced, so the
/// reason survives and the personal data does not. Short numbers, such as Meta's own error codes,
/// are left readable.
/// </remarks>
public static partial class GraphErrorText
{
    private const int MaximumLength = 300;
    private const int MinimumPhoneDigits = 8;
    private const int VisibleTrailingDigits = 2;

    /// <summary>Masks phone numbers and access tokens, and caps the length.</summary>
    /// <param name="message">Graph's error message, or null.</param>
    /// <returns>The masked message, or empty when there was none.</returns>
    public static string Mask(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var masked = AccessToken().Replace(message, "[token]");
        masked = PhoneLike().Replace(masked, match => MaskDigits(match.Value));

        return masked.Length <= MaximumLength ? masked : masked[..MaximumLength];
    }

    private static string MaskDigits(string value)
    {
        var digits = value.Count(char.IsAsciiDigit);

        if (digits < MinimumPhoneDigits)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var seen = 0;

        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                builder.Append(character);
                continue;
            }

            seen++;
            builder.Append(seen > digits - VisibleTrailingDigits ? character : '*');
        }

        return builder.ToString();
    }

    // Meta user and system-user tokens all begin "EAA".
    [GeneratedRegex(@"\bEAA[A-Za-z0-9]{20,}")]
    private static partial Regex AccessToken();

    // A digit, then digits and the separators people write numbers with, ending on a digit.
    [GeneratedRegex(@"\+?\d[\d \-().]{6,}\d")]
    private static partial Regex PhoneLike();
}
