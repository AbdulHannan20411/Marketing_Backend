using System.Net.Mail;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <summary>
/// Field rules for a contact, in one place so the create endpoint, the update endpoint and the CSV
/// import cannot disagree about what a valid contact is.
/// </summary>
internal static class ContactRules
{
    /// <summary>Longest accepted name.</summary>
    public const int MaxNameLength = 120;

    /// <summary>Longest accepted email address.</summary>
    public const int MaxEmailLength = 254;

    /// <summary>Validates and trims a full name.</summary>
    /// <param name="fullName">Name as supplied.</param>
    /// <exception cref="ValidationException">Empty, whitespace-only, or too long.</exception>
    public static string NormaliseName(string? fullName)
    {
        var trimmed = fullName?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ValidationException("fullName", "Enter a name.");
        }

        return trimmed.Length > MaxNameLength
            ? throw new ValidationException("fullName", $"Name must be {MaxNameLength} characters or fewer.")
            : trimmed;
    }

    /// <summary>
    /// Validates and trims an email address, allowing absence.
    /// </summary>
    /// <remarks>
    /// Deliberately not unique: households and small businesses share one address, and enforcing
    /// uniqueness here would reject legitimate contacts.
    /// </remarks>
    /// <param name="email">Address as supplied.</param>
    /// <exception cref="ValidationException">Present but not a valid address.</exception>
    public static string? NormaliseEmail(string? email)
    {
        var trimmed = email?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxEmailLength)
        {
            throw new ValidationException("email", $"Email must be {MaxEmailLength} characters or fewer.");
        }

        return MailAddress.TryCreate(trimmed, out _)
            ? trimmed
            : throw new ValidationException("email", "Enter a valid email address.");
    }

    /// <summary>
    /// Validates a phone number and returns it in international form, ready to send to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A leading zero is a national trunk prefix and never appears in an international number, so
    /// it is the reliable signal that a number was typed the way people write it locally. When it
    /// is present and the country is known, the zero is dropped and the dialling code put in front:
    /// <c>0336 7890092</c> with Pakistan becomes <c>923367890092</c>.
    /// </para>
    /// <para>
    /// This used to strip punctuation and stop. The number stored looked plausible, passed
    /// validation, rendered fine in the table, and was rejected by Meta at send time with an opaque
    /// provider code - which is the worst place to discover it, because the campaign has already
    /// been built and scheduled by then.
    /// </para>
    /// </remarks>
    /// <param name="phoneNumber">Number as supplied.</param>
    /// <param name="countryCode">
    /// ISO alpha-2 code for the number's country, used only to expand a national number. Optional:
    /// a number already in international form needs no country.
    /// </param>
    /// <exception cref="ValidationException">
    /// Not a parseable number, or written nationally with no country to expand it against.
    /// </exception>
    public static string NormalisePhone(string? phoneNumber, string? countryCode = null)
    {
        if (phoneNumber is null || !PhoneNumbers.IsPlausible(phoneNumber))
        {
            throw new ValidationException("phoneNumber", "Enter a valid phone number.");
        }

        var digits = PhoneNumbers.Normalise(phoneNumber);

        // A leading 00 is the international *exit* prefix, not a trunk prefix: 0092336... is how
        // somebody dials +92 336... from most of the world. The country code is already there, so
        // the 00 comes off and nothing goes on. Treating it as a trunk prefix doubles the country
        // code and produces a number that reaches nobody.
        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            return digits[2..];
        }

        if (!digits.StartsWith('0'))
        {
            // Already international, or as close as we can tell. Left alone rather than second
            // guessed: prefixing a country code onto a number that already carries one produces a
            // number that reaches nobody.
            return digits;
        }

        var diallingCode = Countries.ToDiallingCode(countryCode);

        if (diallingCode is null)
        {
            // Refused rather than stored. A national number with no country cannot be dialled from
            // anywhere else, and storing it defers the failure to the moment a campaign runs.
            throw new ValidationException(
                "phoneNumber",
                "That number is written in national format. Include the country code, or choose a country.");
        }

        // Exactly one zero, which is what a trunk prefix is. Stripping every leading zero would
        // silently swallow a digit from any number that legitimately begins with one after it.
        return diallingCode + digits[1..];
    }

    /// <summary>
    /// Resolves the country to store, from an explicit value or from the number's dialling prefix.
    /// </summary>
    /// <remarks>
    /// Refuses rather than guesses. A wrong country is invisible until someone segments a campaign
    /// by it and messages the wrong market.
    /// </remarks>
    /// <param name="country">Country as supplied, a code or a name.</param>
    /// <param name="phoneNumber">Number to fall back to.</param>
    /// <exception cref="ValidationException">Supplied but unrecognised, or absent and not inferable.</exception>
    public static string ResolveCountry(string? country, string phoneNumber)
    {
        if (!string.IsNullOrWhiteSpace(country))
        {
            return Countries.ToStorageCode(country)
                   ?? throw new ValidationException("country", $"\"{country.Trim()}\" is not a country we recognise.");
        }

        return Countries.FromPhoneNumber(phoneNumber)
               ?? throw new ValidationException(
                   "country",
                   "The country could not be worked out from that number. Select one.");
    }

    /// <summary>
    /// Refuses a consent transition that needs a fresh opt-in.
    /// </summary>
    /// <param name="current">Current state.</param>
    /// <param name="target">Requested state.</param>
    /// <exception cref="BusinessRuleException">Moving straight from blocked to subscribed.</exception>
    public static void EnsureTransitionAllowed(ContactStatus current, ContactStatus target)
    {
        if (current == ContactStatus.Blocked && target == ContactStatus.Subscribed)
        {
            // The one transition that cannot be an administrative correction. Someone blocked has
            // either opted out forcefully or been blocked by Meta; putting them back into campaign
            // audiences on an operator's say-so is exactly what consent rules exist to prevent.
            throw new BusinessRuleException(
                "reopt_in_required",
                "A blocked contact must opt in again before they can be subscribed.");
        }
    }

    /// <summary>
    /// Returns the consent timestamp for a status.
    /// <para>
    /// Subscribed carries a stamp; everything else carries none. Leaving a stale opt-in date on an
    /// unsubscribed contact would make an audit read as though consent were still held.
    /// </para>
    /// </summary>
    /// <param name="status">Status being applied.</param>
    /// <param name="utcNow">Current instant.</param>
    /// <param name="existing">Stamp already recorded, kept when the status is unchanged.</param>
    public static DateTimeOffset? ConsentStampFor(
        ContactStatus status,
        DateTimeOffset utcNow,
        DateTimeOffset? existing = null) =>
        status == ContactStatus.Subscribed ? existing ?? utcNow : null;
}
