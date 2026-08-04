namespace Marketing.Business.Repositories.Interfaces;

/// <summary>
/// Executes hand-written read-only SQL through Dapper, for dashboards, analytics and exports where
/// EF Core's translation is a liability rather than a help - window functions, CTEs, wide
/// aggregations across several tables.
/// <para>
/// <b>Why this is not just "a Dapper connection".</b> Raw SQL bypasses EF Core's global query
/// filters, which is exactly where a multi-tenant system leaks. Every method here therefore refuses
/// to run a statement that does not reference the <c>@TenantId</c> parameter it injects from
/// <c>ITenantContext</c>, and refuses anything that is not a read. Cross-tenant reporting has to go
/// through <see cref="QueryPlatformAsync{TResult}"/>, which is explicit and platform-admin gated.
/// </para>
/// </summary>
public interface ISqlQueryExecutor
{
    /// <summary>Runs a tenant-scoped query and maps every row.</summary>
    /// <typeparam name="TResult">Row shape; always a DTO.</typeparam>
    /// <param name="sql">SQL that must reference <c>@TenantId</c>.</param>
    /// <param name="parameters">Additional parameters. Never string-interpolate into the SQL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<TResult>> QueryAsync<TResult>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Runs a tenant-scoped query expected to return at most one row.</summary>
    /// <typeparam name="TResult">Row shape.</typeparam>
    /// <param name="sql">SQL that must reference <c>@TenantId</c>.</param>
    /// <param name="parameters">Additional parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TResult?> QuerySingleOrDefaultAsync<TResult>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a tenant-scoped query without buffering the result set. Used by CSV and report
    /// exports, where materialising every row would put the whole export on the large object heap.
    /// </summary>
    /// <typeparam name="TResult">Row shape.</typeparam>
    /// <param name="sql">SQL that must reference <c>@TenantId</c>.</param>
    /// <param name="parameters">Additional parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    IAsyncEnumerable<TResult> StreamAsync<TResult>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a query across every tenant. Reserved for platform-level reporting and rejected unless
    /// the caller may access all tenants.
    /// </summary>
    /// <typeparam name="TResult">Row shape.</typeparam>
    /// <param name="sql">SQL to run.</param>
    /// <param name="parameters">Parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Common.Exceptions.ForbiddenException">Caller is not a platform administrator.</exception>
    Task<IReadOnlyList<TResult>> QueryPlatformAsync<TResult>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);
}
