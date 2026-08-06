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

    /// <summary>Returns aggregates across every Admin account.</summary>
    public Task<PlatformOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a page of tenants.</summary>
    public Task<PagedResult<TenantResponse>> GetTenantsAsync(
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a page of audit-log entries, newest first.</summary>
    public Task<PagedResult<AuditLogEntryResponse>> GetAuditLogAsync(
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns infrastructure health, quotas and throughput.</summary>
    public Task<SystemSnapshot> GetSystemSnapshotAsync(CancellationToken cancellationToken = default);
}
