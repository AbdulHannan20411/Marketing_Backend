using System.Text;

namespace Marketing.Common.Helpers;

/// <summary>
/// Normalises phone numbers for storage and comparison.
/// <para>
/// Deliberately not a full E.164 validator - that needs a country-prefix database and belongs in a
/// library. This does the one job the platform depends on: reducing a number to digits so that
/// <c>+44 7700 900123</c>, <c>+447700900123</c> and <c>(+44) 7700-900123</c> are recognised as the
/// same contact. Without it, an import creates three rows and the customer gets three messages.
/// </para>
/// </summary>
public static class PhoneNumbers
{
    /// <summary>Longest number E.164 permits, excluding the plus.</summary>
    public const int MaxDigits = 15;

    /// <summary>Shortest number worth treating as dialable.</summary>
    public const int MinDigits = 7;

    /// <summary>Reduces a number to its digits, dropping spaces, punctuation and the leading plus.</summary>
    /// <param name="phoneNumber">Number as entered.</param>
    /// <returns>Digits only, or an empty string when there are none.</returns>
    public static string Normalise(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return string.Empty;
        }

        var digits = new StringBuilder(phoneNumber.Length);

        foreach (var character in phoneNumber)
        {
            if (char.IsAsciiDigit(character))
            {
                digits.Append(character);
            }
        }

        return digits.ToString();
    }

    /// <summary>Returns whether a number is plausible enough to store and attempt delivery to.</summary>
    /// <param name="phoneNumber">Number as entered.</param>
    public static bool IsPlausible(string? phoneNumber)
    {
        var digits = Normalise(phoneNumber);

        return digits.Length is >= MinDigits and <= MaxDigits;
    }

    /// <summary>Formats a number for display, in the <c>+digits</c> form Meta expects.</summary>
    /// <param name="phoneNumber">Number as entered.</param>
    public static string ToDisplayForm(string? phoneNumber)
    {
        var digits = Normalise(phoneNumber);

        return digits.Length == 0 ? string.Empty : $"+{digits}";
    }
}
