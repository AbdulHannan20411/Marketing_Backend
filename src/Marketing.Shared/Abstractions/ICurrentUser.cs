namespace Marketing.Shared.Abstractions;

/// <summary>
/// The authenticated principal for the current unit of work.
/// <para>
/// Implemented over <c>HttpContext</c> for requests and over a fixed system identity for Quartz
/// jobs, so auditing works identically in both. Every member reads from validated JWT claims -
/// nothing here can be influenced by a request body.
/// </para>
/// </summary>
public interface ICurrentUser
{
    /// <summary>Identifier of the signed-in user, or <see langword="null"/> when anonymous.</summary>
    public long? UserId { get; }

    /// <summary>
    /// Session the access token belongs to, from its <c>sid</c> claim.
    /// <para>
    /// Sign-out uses this rather than a token supplied in the request body, so a caller can only
    /// ever end their own session.
    /// </para>
    /// </summary>
    public Guid? SessionId { get; }

    /// <summary>Normalised email address of the signed-in user.</summary>
    public string? Email { get; }

    /// <summary>Display name, used for audit trails and log context.</summary>
    public string? DisplayName { get; }

    /// <summary>Roles carried by the token.</summary>
    public IReadOnlyCollection<string> Roles { get; }

    /// <summary>Fine-grained permissions carried by the token.</summary>
    public IReadOnlyCollection<string> Permissions { get; }

    /// <summary>Whether a user identity is present.</summary>
    public bool IsAuthenticated { get; }

    /// <summary>Whether the principal may act across every tenant.</summary>
    public bool IsSuperAdmin { get; }

    /// <summary>Identifier written to <c>CreatedBy</c> and <c>ModifiedBy</c> audit columns.</summary>
    /// <remarks>Falls back to a well-known system identity for background jobs and seeding.</remarks>
    public long AuditUserId { get; }

    /// <summary>Returns whether the principal holds the given role.</summary>
    public bool IsInRole(string role);

    /// <summary>Returns whether the principal holds the given permission.</summary>
    public bool HasPermission(string permission);
}
