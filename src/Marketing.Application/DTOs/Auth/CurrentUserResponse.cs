namespace Marketing.Application.DTOs.Auth;

/// <summary>
/// Profile of the signed-in user, as the client is allowed to see it.
/// <para>
/// There is deliberately no tenant identifier on this type, and there must never be one. The client
/// is told which organisation it is looking at by name, never by key; every tenant-scoped decision
/// is made server-side from the JWT claim. A tenant id that never reaches the browser cannot be
/// replayed, tampered with, or accidentally sent back as a filter parameter.
/// </para>
/// </summary>
/// <param name="Id">Identifier of the user.</param>
/// <param name="Email">Email address.</param>
/// <param name="DisplayName">Full name.</param>
/// <param name="TenantName">Display name of the owning organisation, or null for platform staff.</param>
/// <param name="IsSuperAdmin">Whether the user operates the platform rather than a tenant.</param>
/// <param name="Roles">Role names held by the user.</param>
/// <param name="Permissions">Fine-grained permissions granted by those roles.</param>
public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string? TenantName,
    bool IsSuperAdmin,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
