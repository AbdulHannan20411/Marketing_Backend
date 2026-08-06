using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Dapper;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.DataAccess.Context;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Marketing.Business.Repositories.Implementations;

/// <summary>Dapper implementation of <see cref="ISqlQueryExecutor"/>.</summary>
public sealed class SqlQueryExecutor : ISqlQueryExecutor
{
    /// <summary>Parameter every tenant-scoped statement must reference.</summary>
    private const string TenantParameterName = "TenantId";

    private readonly ApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">
    /// EF context, used only for its connection and current transaction. Sharing them means a
    /// Dapper read inside <c>ExecuteInTransactionAsync</c> sees the same uncommitted state as the
    /// EF writes around it, rather than opening a second connection that cannot.
    /// </param>
    /// <param name="tenantContext">Ambient tenant, injected into every statement.</param>
    public SqlQueryExecutor(ApplicationDbContext context, ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);

        _context = context;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TResult>> QueryAsync<TResult>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var command = BuildTenantScopedCommand(sql, parameters, cancellationToken);
        var connection = await OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<TResult>(command);
        return [.. rows];
    }

    /// <inheritdoc />
    public async Task<TResult?> QuerySingleOrDefaultAsync<TResult>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var command = BuildTenantScopedCommand(sql, parameters, cancellationToken);
        var connection = await OpenConnectionAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<TResult>(command);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TResult> StreamAsync<TResult>(
        string sql,
        object? parameters = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var command = BuildTenantScopedCommand(sql, parameters, cancellationToken, buffered: false);
        var connection = await OpenConnectionAsync(cancellationToken);

        // Unbuffered: Dapper hands back rows as the reader produces them, so a million-row export
        // holds one row in memory rather than a million. This overload returns the sequence
        // directly rather than a Task, so there is nothing to await until enumeration begins.
        var rows = connection.QueryUnbufferedAsync<TResult>(
            command.CommandText,
            command.Parameters,
            _context.Database.CurrentTransaction?.GetDbTransaction(),
            command.CommandTimeout,
            command.CommandType);

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            yield return row;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TResult>> QueryPlatformAsync<TResult>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        GuardReadOnly(sql);

        if (!_tenantContext.CanAccessAllTenants)
        {
            throw new ForbiddenException("Cross-tenant queries require platform administration rights.");
        }

        var connection = await OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<TResult>(new CommandDefinition(
            sql,
            parameters,
            _context.Database.CurrentTransaction?.GetDbTransaction(),
            cancellationToken: cancellationToken));

        return [.. rows];
    }

    private CommandDefinition BuildTenantScopedCommand(
        string sql,
        object? parameters,
        CancellationToken cancellationToken,
        bool buffered = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        GuardReadOnly(sql);
        GuardTenantScoped(sql);

        var dynamicParameters = new DynamicParameters(parameters);

        // RequireTenantId throws rather than returning null, so a statement can never run with the
        // tenant predicate silently satisfied by NULL - which in SQL matches nothing, or worse,
        // is mistaken for "no filter" by whoever wrote the query.
        dynamicParameters.Add(TenantParameterName, _tenantContext.RequireTenantId(), DbType.Guid);

        return new CommandDefinition(
            sql,
            dynamicParameters,
            _context.Database.CurrentTransaction?.GetDbTransaction(),
            flags: buffered ? CommandFlags.Buffered : CommandFlags.None,
            cancellationToken: cancellationToken);
    }

    // DbConnection, not IDbConnection: Dapper's unbuffered streaming overload is only defined on
    // the concrete base class, because it needs the async reader API the interface does not expose.
    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await _context.Database.OpenConnectionAsync(cancellationToken);
        }

        return connection;
    }

    private static void GuardTenantScoped(string sql)
    {
        if (sql.Contains($"@{TenantParameterName}", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Deliberately a hard failure at development time rather than a silent cross-tenant read
        // in production. If a query genuinely spans tenants it belongs in QueryPlatformAsync.
        throw new InvalidOperationException(
            $"Tenant-scoped SQL must reference the @{TenantParameterName} parameter. " +
            $"Use {nameof(QueryPlatformAsync)} for deliberate cross-tenant queries.");
    }

    private static void GuardReadOnly(string sql)
    {
        var trimmed = sql.AsSpan().TrimStart();

        var isRead = trimmed.StartsWith("select", StringComparison.OrdinalIgnoreCase)
                     || trimmed.StartsWith("with", StringComparison.OrdinalIgnoreCase);

        if (isRead)
        {
            return;
        }

        // Writes go through EF Core so they are audited, soft-deleted and concurrency-checked.
        // A raw INSERT or UPDATE here would silently skip all three.
        throw new InvalidOperationException(
            "Only SELECT and WITH statements may be executed through the SQL query executor; " +
            "writes must go through Entity Framework so they are audited.");
    }
}
