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
    Guid? UserId { get; }

    /// <summary>Normalised email address of the signed-in user.</summary>
    string? Email { get; }

    /// <summary>Display name, used for audit trails and log context.</summary>
    string? DisplayName { get; }

    /// <summary>Roles carried by the token.</summary>
    IReadOnlyCollection<string> Roles { get; }

    /// <summary>Fine-grained permissions carried by the token.</summary>
    IReadOnlyCollection<string> Permissions { get; }

    /// <summary>Whether a user identity is present.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Whether the principal may act across every tenant.</summary>
    bool IsPlatformAdmin { get; }

    /// <summary>Identifier written to <c>CreatedBy</c> and <c>ModifiedBy</c> audit columns.</summary>
    /// <remarks>Falls back to a well-known system identity for background jobs and seeding.</remarks>
    Guid AuditUserId { get; }

    /// <summary>Returns whether the principal holds the given role.</summary>
    bool IsInRole(string role);

    /// <summary>Returns whether the principal holds the given permission.</summary>
    bool HasPermission(string permission);
}
