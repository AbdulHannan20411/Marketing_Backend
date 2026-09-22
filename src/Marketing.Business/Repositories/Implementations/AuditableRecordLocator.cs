using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Context;
using Microsoft.EntityFrameworkCore;

namespace Marketing.Business.Repositories.Implementations;

/// <inheritdoc cref="IAuditableRecordLocator" />
public sealed class AuditableRecordLocator : IAuditableRecordLocator
{
    private readonly ApplicationDbContext _context;
    private readonly ISqlQueryExecutor _sql;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">Context, read for its model rather than its data.</param>
    /// <param name="sql">Reader, which injects the tenant parameter and refuses writes.</param>
    public AuditableRecordLocator(ApplicationDbContext context, ISqlQueryExecutor sql)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
        _sql = sql;
    }

    /// <inheritdoc />
    public async Task<long?> FindForTenantAsync(
        string entityName,
        CancellationToken cancellationToken = default)
    {
        if (TableFor(entityName) is not { } table)
        {
            return null;
        }

        // Newest wins, for the same reason the unique indexes exist: there should be one row, and
        // if a data fix ever left two, the live one is the one the workspace is actually using.
        var found = await _sql.QueryAsync<long>(
            $"select id from \"{table}\" where tenant_id = @TenantId and is_deleted = false "
            + "order by id desc limit 1",
            cancellationToken: cancellationToken);

        return found.Count > 0 ? found[0] : null;
    }

    /// <inheritdoc />
    public async Task<long?> FindByKeyAsync(
        string entityName,
        string keyColumn,
        string key,
        bool platformOnly,
        CancellationToken cancellationToken = default)
    {
        if (TableFor(entityName) is not { } table || ColumnFor(entityName, keyColumn) is not { } column)
        {
            return null;
        }

        // Both names come from the model, matched against the registry's column - the caller's key
        // travels as a parameter and never as text in the statement.
        var sql = $"select id from \"{table}\" where \"{column}\" = @Key";

        var found = platformOnly
            ? await _sql.QueryPlatformAsync<long>($"{sql} limit 1", new { Key = key }, cancellationToken)
            : await _sql.QueryAsync<long>(
                $"{sql} and tenant_id = @TenantId limit 1", new { Key = key }, cancellationToken);

        return found.Count > 0 ? found[0] : null;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(
        string entityName,
        long id,
        bool platformOnly,
        CancellationToken cancellationToken = default)
    {
        if (TableFor(entityName) is not { } table)
        {
            return false;
        }

        if (platformOnly)
        {
            var anywhere = await _sql.QueryPlatformAsync<int>(
                $"select 1 from \"{table}\" where id = @Id limit 1",
                new { Id = id },
                cancellationToken);

            return anywhere.Count > 0;
        }

        // Tenant-scoped, and the reader refuses a statement that does not name the parameter - so
        // forgetting the predicate is a startup-time mistake rather than a data leak.
        var mine = await _sql.QueryAsync<int>(
            $"select 1 from \"{table}\" where id = @Id and tenant_id = @TenantId limit 1",
            new { Id = id },
            cancellationToken);

        return mine.Count > 0;
    }

    /// <summary>
    /// The table an entity maps to, or null when the name is not part of the model.
    /// </summary>
    /// <remarks>
    /// The one place an entity name becomes SQL. Resolving it through the model rather than
    /// formatting the caller's string is what makes the statements above safe to build.
    /// </remarks>
    private string? TableFor(string entityName) =>
        _context.Model
            .GetEntityTypes()
            .FirstOrDefault(entity => string.Equals(entity.ClrType.Name, entityName, StringComparison.Ordinal))
            ?.GetTableName();

    /// <summary>The column a property maps to, checked against the model for the same reason.</summary>
    private string? ColumnFor(string entityName, string column)
    {
        var entity = _context.Model
            .GetEntityTypes()
            .FirstOrDefault(candidate => string.Equals(candidate.ClrType.Name, entityName, StringComparison.Ordinal));

        var table = entity?.GetTableName();

        if (entity is null || table is null)
        {
            return null;
        }

        var identifier = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier
            .Table(table, entity.GetSchema());

        return entity.GetProperties()
            .Select(property => property.GetColumnName(identifier))
            .FirstOrDefault(name => string.Equals(name, column, StringComparison.OrdinalIgnoreCase));
    }
}
