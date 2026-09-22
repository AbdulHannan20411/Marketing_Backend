namespace Marketing.Business.Repositories.Interfaces;

/// <summary>
/// Answers "is this record one the caller could open", which is what decides whether they may read
/// its history.
/// </summary>
/// <remarks>
/// Deliberately separate from the audit table. An audit row carries a tenant id of its own, and
/// trusting it would mean a caller who guessed a row id could read another workspace's history
/// because the audit row agreed with them about which workspace it belonged to. The record itself
/// is the only honest authority.
/// <para>
/// Lives in the persistence layer because it resolves an entity name to a table through the EF
/// model. The name never reaches SQL: it is looked up in the model, and an unknown one is refused.
/// </para>
/// </remarks>
public interface IAuditableRecordLocator
{
    /// <summary>
    /// Whether a record exists, within the caller's workspace unless it is a platform-level type.
    /// </summary>
    /// <remarks>
    /// Soft-deleted rows count. A record's history outlives the record - the delete itself is the
    /// entry someone most often comes looking for - so filtering them out would make the last thing
    /// that happened the one thing nobody could read.
    /// </remarks>
    /// <param name="entityName">CLR name of the entity, from the auditable registry.</param>
    /// <param name="id">Row identifier.</param>
    /// <param name="platformOnly">True to look across every workspace, for platform-level records.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<bool> ExistsAsync(
        string entityName,
        long id,
        bool platformOnly,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The identifier of the caller's own row of a record a workspace has exactly one of.
    /// </summary>
    /// <remarks>
    /// Its history is asked for as <c>current</c>, so the row has to be found before it can be
    /// read. Null when the workspace has never saved one - an auto-reply settings row is written
    /// the first time somebody presses Save, and before that there is nothing to have a history.
    /// </remarks>
    /// <param name="entityName">CLR name of the entity, from the auditable registry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<long?> FindForTenantAsync(string entityName, CancellationToken cancellationToken = default);

    /// <summary>
    /// The identifier of a record addressed by its business key rather than by a public id.
    /// </summary>
    /// <param name="entityName">CLR name of the entity, from the auditable registry.</param>
    /// <param name="keyColumn">Column holding the key, from the registry - never from the request.</param>
    /// <param name="key">Key the caller sent.</param>
    /// <param name="platformOnly">True to look across every workspace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<long?> FindByKeyAsync(
        string entityName,
        string keyColumn,
        string key,
        bool platformOnly,
        CancellationToken cancellationToken = default);
}
