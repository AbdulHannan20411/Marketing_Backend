namespace Marketing.DataAccess.Entities;

/// <summary>
/// A transactional email's wording, editable by platform staff.
/// </summary>
/// <remarks>
/// Platform-wide rather than per workspace: every tenant's users receive the same wording, so the
/// entity is deliberately not tenant-scoped and no tenant filter applies to it.
/// <para>
/// Seeded from the shipped defaults. <see cref="DefaultHash"/> records which default the row was last
/// reconciled against, so a release can improve a default without overwriting a Super Admin's edits.
/// </para>
/// </remarks>
public sealed class EmailTemplate : BaseEntity
{
    /// <summary>Stable identifier the sending code refers to, for example <c>auth.invitation</c>.</summary>
    public required string Key { get; set; }

    /// <summary>Display name. Follows the code; not editable through the API.</summary>
    public required string Name { get; set; }

    /// <summary>What the email is for. Follows the code; not editable through the API.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Grouping in the editor: <c>layout</c>, <c>account</c>, <c>billing</c> or <c>payments</c>.</summary>
    public required string Category { get; set; }

    /// <summary>Subject line template.</summary>
    public required string Subject { get; set; }

    /// <summary>HTML body template.</summary>
    public required string HtmlBody { get; set; }

    /// <summary>Plain-text body template.</summary>
    public required string TextBody { get; set; }

    /// <summary>The template's own variables, as JSON. Follows the code; not editable through the API.</summary>
    public string VariablesJson { get; set; } = "[]";

    /// <summary>
    /// Hash of the shipped default this row was last reconciled against.
    /// </summary>
    /// <remarks>
    /// The stored parts hashing to this value means nobody has edited them. It is what lets seeding
    /// replace an unedited template with an improved default while leaving an edited one alone.
    /// </remarks>
    public string DefaultHash { get; set; } = string.Empty;

    /// <summary>
    /// When a person last saved or reset the template through the editor.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="BaseEntity.ModifiedOn"/>, which the auditing interceptor stamps on
    /// every write - including a release reconciling a default. "Last edited" in the editor has to
    /// mean a person edited it.
    /// </remarks>
    public DateTimeOffset? UpdatedOn { get; set; }

    /// <summary>Who last saved or reset the template through the editor.</summary>
    public long? UpdatedByUserId { get; set; }
}
