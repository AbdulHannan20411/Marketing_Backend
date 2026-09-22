namespace Marketing.Application.Services.Audit;

/// <summary>
/// Wording for the few property names that do not read well on their own.
/// </summary>
/// <remarks>
/// The client humanises a property name well enough for almost everything - <c>FooterText</c>
/// becomes "Footer text" - so this holds only the exceptions, and a name with no entry is left to
/// it. Applied when history is read rather than when it is written: a label is presentation, and
/// storing it in every audit row would freeze today's wording into rows nobody can edit.
/// <para>
/// Keyed by property name alone, not by entity and property. Two records with the same column name
/// mean the same thing by it here; the day that stops being true, this becomes a pair.
/// </para>
/// </remarks>
public static class AuditFieldLabels
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["BodyText"] = "Message body",
        ["HeaderText"] = "Header",
        ["MaxSearchRadiusKm"] = "Search radius (km)",
    };

    /// <summary>The wording for a property, or null to let the client humanise the name.</summary>
    /// <param name="property">Property name as the audit trail stored it.</param>
    public static string? For(string property) => Labels.GetValueOrDefault(property);
}
