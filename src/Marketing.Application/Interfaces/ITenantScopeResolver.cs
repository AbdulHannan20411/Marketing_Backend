namespace Marketing.Application.Interfaces;

/// <summary>
/// Resolves the tenant a request operates on, honouring the Super Admin scoping parameter.
/// <para>
/// This is the one place in the platform where a tenant can be chosen by a caller, so the rules
/// live here rather than being repeated in every controller.
/// </para>
/// </summary>
public interface ITenantScopeResolver
{
    /// <summary>
    /// Enters the scope implied by an optional <c>adminId</c>.
    /// <para>
    /// Honoured only when the caller is a Super Admin. For anyone else it is <b>ignored, not
    /// rejected</b>: a forged parameter is then inert rather than being a probe that confirms the
    /// parameter means something. An unknown or unusable account is a 403.
    /// </para>
    /// </summary>
    /// <param name="adminId">Public admin-account identifier, prefixed <c>adm_</c>, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A scope to dispose when the operation finishes.</returns>
    /// <exception cref="Common.Exceptions.ForbiddenException">
    /// The caller is a Super Admin but the account is unknown or has no tenant.
    /// </exception>
    public Task<IDisposable> EnterAsync(string? adminId, CancellationToken cancellationToken = default);
}
