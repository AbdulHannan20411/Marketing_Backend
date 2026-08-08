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

    /// <summary>Validates a phone number and returns its normalised form.</summary>
    /// <param name="phoneNumber">Number as supplied.</param>
    /// <exception cref="ValidationException">Not a parseable number.</exception>
    public static string NormalisePhone(string? phoneNumber)
    {
        if (phoneNumber is null || !PhoneNumbers.IsPlausible(phoneNumber))
        {
            throw new ValidationException("phoneNumber", "Enter a valid phone number.");
        }

        return PhoneNumbers.Normalise(phoneNumber);
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
