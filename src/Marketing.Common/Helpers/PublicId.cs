using System.Diagnostics.CodeAnalysis;

namespace Marketing.Common.Helpers;

/// <summary>
/// Converts between internal <see cref="Guid"/> keys and the opaque, prefixed identifiers the
/// client contract requires - <c>cnt_…</c>, <c>cmp_…</c>, <c>tpl_…</c>.
/// <para>
/// The database keeps time-ordered version 7 GUIDs, which index well; the wire keeps prefixed
/// strings, which are self-describing in a log or a support ticket and reveal no ordering or
/// volume. Formatting at the boundary gets both, at the cost of one call in each direction.
/// </para>
/// <para>
/// The prefix is not decoration: <see cref="TryParse"/> checks it, so a campaign id pasted into a
/// contact route is rejected as malformed rather than being looked up and returning "not found",
/// which is a much harder mistake to diagnose.
/// </para>
/// </summary>
public static class PublicId
{
    /// <summary>Prefix for contacts.</summary>
    public const string Contact = "cnt";

    /// <summary>Prefix for campaigns.</summary>
    public const string Campaign = "cmp";

    /// <summary>Prefix for message templates.</summary>
    public const string Template = "tpl";

    /// <summary>Prefix for contact groups.</summary>
    public const string Group = "grp";

    /// <summary>Prefix for tags.</summary>
    public const string Tag = "tag";

    /// <summary>Prefix for employees.</summary>
    public const string Employee = "emp";

    /// <summary>Prefix for subscription plans.</summary>
    public const string Plan = "plan";

    /// <summary>Prefix for invoices.</summary>
    public const string Invoice = "inv";

    /// <summary>Prefix for payments.</summary>
    public const string Payment = "pay";

    /// <summary>Prefix for renewal records.</summary>
    public const string Renewal = "rnw";

    /// <summary>Prefix for platform admin accounts.</summary>
    public const string AdminAccount = "adm";

    /// <summary>Prefix for tenants.</summary>
    public const string Tenant = "tnt";

    /// <summary>Prefix for notifications.</summary>
    public const string Notification = "ntf";

    /// <summary>Prefix for audit-log entries.</summary>
    public const string Audit = "aud";

    /// <summary>Prefix for permission sets.</summary>
    public const string PermissionSet = "pms";

    /// <summary>Prefix for delivery failures.</summary>
    public const string DeliveryFailure = "dlf";

    /// <summary>Prefix for stored payment methods.</summary>
    public const string PaymentMethod = "pm";

    /// <summary>Prefix for contact import batches.</summary>
    public const string ImportBatch = "imp";

    /// <summary>Prefix for generated export jobs.</summary>
    public const string Export = "exp";

    private const char Separator = '_';

    /// <summary>Formats a key as its public identifier.</summary>
    /// <param name="prefix">One of the prefix constants on this type.</param>
    /// <param name="id">Internal key.</param>
    public static string From(string prefix, long id) =>
        $"{prefix}{Separator}{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>Formats a nullable key, returning null when there is none.</summary>
    /// <param name="prefix">One of the prefix constants on this type.</param>
    /// <param name="id">Internal key, or null.</param>
    public static string? FromNullable(string prefix, long? id) =>
        id is { } value ? From(prefix, value) : null;

    /// <summary>Attempts to read a public identifier back into its key.</summary>
    /// <param name="prefix">The prefix the value is required to carry.</param>
    /// <param name="value">Public identifier supplied by the caller.</param>
    /// <param name="id">The parsed key.</param>
    /// <returns>Whether the value was well formed and carried the expected prefix.</returns>
    public static bool TryParse(string prefix, [NotNullWhen(true)] string? value, out long id)
    {
        id = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separatorIndex = value.IndexOf(Separator, StringComparison.Ordinal);

        if (separatorIndex <= 0)
        {
            return false;
        }

        if (!value.AsSpan(0, separatorIndex).SequenceEqual(prefix))
        {
            return false;
        }

        return long.TryParse(
            value[(separatorIndex + 1)..],
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out id) && id > 0;
    }

    /// <summary>Reads a public identifier back into its key, or throws.</summary>
    /// <param name="prefix">The prefix the value is required to carry.</param>
    /// <param name="value">Public identifier supplied by the caller.</param>
    /// <param name="resourceName">Resource name used in the error message.</param>
    /// <exception cref="Exceptions.ValidationException">The value is malformed or misprefixed.</exception>
    public static long Parse(string prefix, string? value, string resourceName)
    {
        if (TryParse(prefix, value, out var id))
        {
            return id;
        }

        throw new Exceptions.ValidationException("id", $"'{value}' is not a valid {resourceName} identifier.");
    }
}
