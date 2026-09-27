using Marketing.Application.DTOs.Platform;
using Marketing.Common.Requests;
using Marketing.Common.Responses;

namespace Marketing.Application.Interfaces;

/// <summary>
/// Platform-wide reads across every tenant.
/// <para>
/// Reserved for <c>SuperAdmin</c>. Every method here deliberately crosses the tenant boundary,
/// which is why the endpoints are gated on role as well as permission.
/// </para>
/// </summary>
public interface IPlatformService
{
    /// <summary>Returns every Admin account with its organisation's counters.</summary>
    public Task<IReadOnlyList<AdminAccount>> GetAdminAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns one page of Admin accounts, filtered and searched.</summary>
    /// <param name="query">Paging, search and status filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<AdminAccount>> GetAdminAccountsAsync(
        AdminAccountQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Returns aggregates across every Admin account.</summary>
    public Task<PlatformOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a page of tenants.</summary>
    public Task<PagedResult<TenantResponse>> GetTenantsAsync(
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a page of audit-log entries, newest first.</summary>
    /// <param name="request">Paging, sorting and the screen's filters. All filters optional.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<AuditLogEntryResponse>> GetAuditLogAsync(
        AuditLogQuery request,
        CancellationToken cancellationToken = default);

    /// <summary>The same entries the page returns, streamed, for an export.</summary>
    /// <remarks>
    /// Streamed rather than paged because an export has no page: the rows go to the response as
    /// they arrive from the database, so the memory it costs is one row and not one audit log.
    /// Same filters and same order as <see cref="GetAuditLogAsync"/>, from the same query.
    /// </remarks>
    /// <param name="request">Filters and sort. Paging is ignored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public IAsyncEnumerable<AuditLogEntryResponse> StreamAuditLogAsync(
        AuditLogQuery request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns infrastructure health, quotas and throughput.</summary>
    public Task<SystemSnapshot> GetSystemSnapshotAsync(CancellationToken cancellationToken = default);
}
