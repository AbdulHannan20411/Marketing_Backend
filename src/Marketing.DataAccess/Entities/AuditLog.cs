using Marketing.Common.Enums;
using Marketing.Common.Helpers;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// One immutable audit-trail record.
/// <para>
/// Deliberately not a <see cref="BaseEntity"/>: audit rows are append-only. Giving them soft
/// delete, modification stamps or a concurrency token would imply they can be edited, and an audit
/// trail that can be edited is not an audit trail.
/// </para>
/// </summary>
public sealed class AuditLog
{
    /// <summary>Primary key, time-ordered so the table clusters chronologically.</summary>
    public Guid Id { get; set; } = SequentialGuid.Create();

    /// <summary>Tenant the change belongs to, or null for platform-level changes.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>User who made the change, or the system identity for background work.</summary>
    public Guid UserId { get; set; }

    /// <summary>CLR name of the entity that changed.</summary>
    public required string EntityName { get; set; }

    /// <summary>Primary key of the row that changed, rendered as text.</summary>
    public required string EntityId { get; set; }

    /// <summary>Kind of change.</summary>
    public AuditAction Action { get; set; }

    /// <summary>
    /// Changed columns as JSON: <c>{ "column": { "old": ..., "new": ... } }</c>.
    /// Stored as <c>jsonb</c> so it can be queried with PostgreSQL's JSON operators during an
    /// investigation instead of being parsed application-side.
    /// </summary>
    public string? Changes { get; set; }

    /// <summary>Correlation id of the request that produced the change.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Client address that produced the change.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Instant the change was committed, in UTC.</summary>
    public DateTimeOffset OccurredOn { get; set; }
}
