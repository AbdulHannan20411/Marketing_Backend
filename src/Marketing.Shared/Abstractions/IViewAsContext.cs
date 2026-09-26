namespace Marketing.Shared.Abstractions;

/// <summary>
/// The teammate an administrator is previewing, for the duration of one request.
/// </summary>
/// <remarks>
/// <b>What this is not.</b> It is not a second identity. The request is still made by the
/// administrator: their session validates it, their name goes on anything written, and the audit
/// trail records them. All this changes is the answer to "what may the caller see", and it can
/// only ever make that answer smaller.
/// <para>
/// Entered from a query parameter on reads, by <c>ViewAsResolver</c>, after it has checked that
/// the caller administers the workspace the teammate belongs to. Nothing else may enter it: the
/// parameter is a request the caller controls, and the check is the only thing between it and
/// somebody else's data.
/// </para>
/// </remarks>
public interface IViewAsContext
{
    /// <summary>The teammate being previewed, or null when the caller is simply themselves.</summary>
    public ViewAsIdentity? Previewing { get; }

    /// <summary>Whether a preview is in effect.</summary>
    public bool IsPreviewing { get; }

    /// <summary>
    /// Enters a preview for the rest of the request.
    /// </summary>
    /// <remarks>
    /// Held per instance on a scoped service, as the tenant scope is, so it lives exactly as long
    /// as the request that entered it and cannot leak into the next one on the same thread.
    /// </remarks>
    /// <param name="identity">The teammate to preview.</param>
    public IDisposable BeginScope(ViewAsIdentity identity);
}

/// <summary>
/// Who is being previewed, and what they may do.
/// </summary>
/// <remarks>
/// The permissions are resolved the same way the teammate's own token is minted - their roles plus
/// their overrides, through <c>EffectivePermissions</c> - so the preview cannot show a different
/// answer from the one they would get by signing in.
/// </remarks>
/// <param name="UserId">The teammate's internal key.</param>
/// <param name="DisplayName">Their name, for the audit entry and for logs.</param>
/// <param name="Roles">Their roles. Deliberately theirs: an administrator previewing an employee
/// must lose the administrator role, or every "is this person an Admin" shortcut in the codebase
/// would keep answering yes and the preview would show them their own unrestricted view.</param>
/// <param name="Permissions">Their effective permissions.</param>
public sealed record ViewAsIdentity(
    long UserId,
    string DisplayName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions);
