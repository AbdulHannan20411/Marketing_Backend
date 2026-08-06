using Marketing.DataAccess.Entities;

namespace Marketing.Business.Repositories.Interfaces;

/// <summary>
/// Read access to the audit trail.
/// <para>
/// Separate from the generic repository because <see cref="AuditLog"/> is deliberately not a
/// <c>BaseEntity</c> - audit rows are append-only, so they have no soft delete, no modification
/// stamps and no concurrency token. Forcing them through a repository that offers Update and
/// Remove would advertise operations that must not exist.
/// </para>
/// </summary>
public interface IAuditLogRepository
{
    /// <summary>
    /// Composable query over audit entries, newest first.
    /// <para>
    /// Read-only by design. Entries are written by the save-changes interceptor and by nothing
    /// else, so there is no Add here either.
    /// </para>
    /// </summary>
    public IQueryable<AuditLog> Query();
}
