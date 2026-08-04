namespace Marketing.Common.Helpers;

/// <summary>
/// Generates primary keys.
/// <para>
/// Random version-4 GUIDs are a real problem as PostgreSQL B-tree keys: inserts land at random
/// leaf pages, which fragments the index and inflates WAL traffic. Version 7 GUIDs embed a
/// millisecond timestamp in the high bits, so they sort in creation order and insert at the right
/// edge of the index the way a sequence would - while staying globally unique, which matters for
/// a multi-tenant system where identifiers cross service boundaries.
/// </para>
/// </summary>
public static class SequentialGuid
{
    /// <summary>Creates a time-ordered (version 7) identifier from the current UTC time.</summary>
    public static Guid Create() => Guid.CreateVersion7();

    /// <summary>Creates a time-ordered identifier anchored to a specific instant.</summary>
    /// <param name="timestamp">Instant to embed. Must be UTC.</param>
    public static Guid Create(DateTimeOffset timestamp) => Guid.CreateVersion7(timestamp);
}
