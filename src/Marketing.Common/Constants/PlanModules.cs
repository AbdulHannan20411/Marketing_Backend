namespace Marketing.Common.Constants;

/// <summary>
/// Keys of the plan module map.
/// <para>
/// The contract models modules as an object keyed by module name with all eight keys always
/// present, so the client can read <c>modules.whatsapp</c> without a null check. These are the
/// exact key strings; they are not derived from the enum, because the camelCase policy would turn
/// <c>WhatsApp</c> into <c>whatsApp</c>.
/// </para>
/// </summary>
public static class PlanModules
{
    /// <summary>WhatsApp channel.</summary>
    public const string WhatsApp = "whatsapp";

    /// <summary>Email channel.</summary>
    public const string Email = "email";

    /// <summary>Social channels.</summary>
    public const string Social = "social";

    /// <summary>Contact management.</summary>
    public const string Crm = "crm";

    /// <summary>Reporting and analytics.</summary>
    public const string Reporting = "reporting";

    /// <summary>AI assistance.</summary>
    public const string Ai = "ai";

    /// <summary>Public API access.</summary>
    public const string Api = "api";

    /// <summary>Multiple employee seats.</summary>
    public const string Employees = "employees";

    /// <summary>All eight keys, in the order the pricing page renders them.</summary>
    public static readonly IReadOnlyList<string> All =
        [WhatsApp, Email, Social, Crm, Reporting, Ai, Api, Employees];

    /// <summary>
    /// Expands a stored list of enabled modules into the full map.
    /// <para>
    /// Every key is present, absent ones false. A partial map would make the client's feature
    /// checks depend on whether a key happened to be persisted.
    /// </para>
    /// </summary>
    /// <param name="enabled">Module keys switched on for a plan.</param>
    public static IReadOnlyDictionary<string, bool> Expand(IEnumerable<string>? enabled)
    {
        var set = new HashSet<string>(enabled ?? [], StringComparer.OrdinalIgnoreCase);

        return All.ToDictionary(key => key, set.Contains, StringComparer.Ordinal);
    }

    /// <summary>Reduces a module map back to the list of enabled keys, ignoring unknown ones.</summary>
    /// <param name="modules">Module map supplied by a caller.</param>
    public static List<string> Collapse(IReadOnlyDictionary<string, bool>? modules)
    {
        if (modules is null)
        {
            return [];
        }

        return [.. All.Where(key => modules.TryGetValue(key, out var enabled) && enabled)];
    }
}
