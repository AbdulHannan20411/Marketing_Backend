namespace Marketing.Common.Enums;

/// <summary>The kind of change recorded in an audit-trail entry.</summary>
public enum AuditAction
{
    /// <summary>A new row was inserted.</summary>
    Created = 0,

    /// <summary>An existing row was updated.</summary>
    Updated = 1,

    /// <summary>A row was soft-deleted.</summary>
    Deleted = 2,
}
