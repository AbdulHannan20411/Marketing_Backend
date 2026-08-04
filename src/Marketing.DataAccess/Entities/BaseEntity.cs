using Marketing.Common.Helpers;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// Root of the persistence model. Supplies identity, multi-tenancy, auditing, soft delete and
/// optimistic concurrency to every table in the schema.
/// <para>
/// None of the audit or tenancy members are ever assigned by application code. They are populated
/// by <c>AuditingSaveChangesInterceptor</c>; assigning them by hand is treated as a defect because
/// it is the only way those columns can disagree with the interceptor's view of the world.
/// </para>
/// </summary>
public abstract class BaseEntity
{
    /// <summary>
    /// Primary key. Assigned client-side as a time-ordered version 7 GUID so inserts append to the
    /// right edge of the index rather than fragmenting it - see <see cref="SequentialGuid"/>.
    /// </summary>
    public Guid Id { get; set; } = SequentialGuid.Create();

    /// <summary>
    /// Owning tenant. Null only for platform-level rows (tenants, global roles, platform admins).
    /// Set by the interceptor from <c>ITenantContext</c>; never accepted from a request.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>User who created the row.</summary>
    public Guid CreatedBy { get; set; }

    /// <summary>Instant the row was created, in UTC.</summary>
    public DateTimeOffset CreatedOn { get; set; }

    /// <summary>User who last modified the row.</summary>
    public Guid? ModifiedBy { get; set; }

    /// <summary>Instant the row was last modified, in UTC.</summary>
    public DateTimeOffset? ModifiedOn { get; set; }

    /// <summary>User who soft-deleted the row.</summary>
    public Guid? DeletedBy { get; set; }

    /// <summary>Instant the row was soft-deleted, in UTC.</summary>
    public DateTimeOffset? DeletedOn { get; set; }

    /// <summary>
    /// Soft-delete marker. Deleted rows are excluded by a global query filter, so ordinary queries
    /// never see them and no caller has to remember to filter.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Optimistic concurrency token.
    /// <para>
    /// PostgreSQL has no SQL Server style <c>rowversion</c>. This property is mapped onto the
    /// built-in <c>xmin</c> system column, which holds the id of the transaction that last wrote
    /// the row and therefore changes on every update at zero storage cost.
    /// </para>
    /// </summary>
    public uint RowVersion { get; set; }
}
