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
/// <param name="Capabilities">What this build of the API can do, for features the client must not
/// assume are present.</param>
public sealed record CurrentUserResponse(
    long Id,
    string Email,
    string DisplayName,
    string? TenantName,
    bool IsSuperAdmin,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    ApiCapabilities Capabilities);

/// <summary>
/// Features whose absence the client cannot detect by trying.
/// </summary>
/// <remarks>
/// Only for the ones where guessing wrong is worse than not having the feature. An API that
/// ignored an unknown <c>viewAsEmployeeId</c> would answer with the administrator's own data while
/// the client showed a banner naming somebody else - a confident wrong answer to a question about
/// access, which is worse than a missing button.
/// <para>
/// A flag rather than a version: a version says what to expect and a capability says what is
/// actually wired up, and only one of those survives a feature being turned off in configuration.
/// </para>
/// </remarks>
/// <param name="ViewAsEmployee">
/// Whether <c>?viewAsEmployeeId=</c> is honoured on tenant-scoped reads. When false the client
/// keeps the permission-only preview: the menu narrows, the data does not.
/// </param>
public sealed record ApiCapabilities(bool ViewAsEmployee);
