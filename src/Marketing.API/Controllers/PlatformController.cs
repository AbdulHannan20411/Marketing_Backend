using System.Globalization;
using Asp.Versioning;
using Marketing.API.Export;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Platform;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Requests;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Marketing.API.Controllers;

/// <summary>Platform staff views across every tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/superadmin")]
[Authorize(Policy = AppConstants.Policies.SuperAdminOnly)]
[EnableRateLimiting(AppConstants.RateLimits.Admin)]
public sealed class SuperAdminController : ApiControllerBase
{
    private readonly IPlatformService _platform;

    /// <summary>Initialises a new instance.</summary>
    public SuperAdminController(IPlatformService platform) => _platform = platform;

    /// <summary>Returns Admin accounts with their organisation's counters.</summary>
    /// <remarks>
    /// Two shapes from one route: without <c>page</c> or <c>pageSize</c> it answers with the plain
    /// array it always has, and with either of them it answers with a <c>PagedResult</c>. Search
    /// and the status filter apply to both, so a caller can narrow the list without paging it.
    /// </remarks>
    /// <param name="query">Paging, search and status filter. All optional.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The admin accounts, as an array or as a page.</response>
    [HttpGet("admins")]
    [RequirePermission(Permissions.Platform.Tenants)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AdminAccount>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AdminAccount>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAdminsAsync(
        [FromQuery] AdminAccountQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var accounts = await _platform.GetAdminAccountsAsync(query, cancellationToken);

        return query.WantsPage ? SuccessPage(accounts) : Success(accounts.Items);
    }

    /// <summary>Returns aggregates across every Admin account.</summary>
    /// <response code="200">The platform overview.</response>
    [HttpGet("overview")]
    [RequirePermission(Permissions.Platform.Tenants)]
    [ProducesResponseType(typeof(ApiResponse<PlatformOverview>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOverviewAsync(CancellationToken cancellationToken) =>
        Success(await _platform.GetOverviewAsync(cancellationToken));
}

/// <summary>Tenant administration, audit and monitoring.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/admin")]
[Authorize(Policy = AppConstants.Policies.SuperAdminOnly)]
[EnableRateLimiting(AppConstants.RateLimits.Admin)]
public sealed class PlatformAdminController : ApiControllerBase
{
    private readonly IPlatformService _platform;

    /// <summary>Initialises a new instance.</summary>
    public PlatformAdminController(IPlatformService platform) => _platform = platform;

    /// <summary>Returns a page of tenants.</summary>
    /// <param name="request">Paging parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of tenants.</response>
    [HttpGet("tenants")]
    [RequirePermission(Permissions.Platform.Tenants)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<TenantResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTenantsAsync(
        [FromQuery] PageRequest request,
        CancellationToken cancellationToken) =>
        SuccessPage(await _platform.GetTenantsAsync(request, cancellationToken));

    /// <summary>Returns a page of audit-log entries, newest first.</summary>
    /// <remarks>
    /// Paging, sorting, and the screen's four filters: <c>search</c>, <c>actor</c>,
    /// <c>from</c>/<c>to</c> and <c>severity</c>. Each is optional and each narrows the whole log
    /// rather than the page, so the total underneath the list describes what the filters selected.
    /// </remarks>
    /// <param name="request">Paging, sorting and filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of audit entries.</response>
    [HttpGet("audit")]
    [RequirePermission(Permissions.Platform.Audit)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<AuditLogEntryResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuditAsync(
        [FromQuery] AuditLogQuery request,
        CancellationToken cancellationToken) =>
        SuccessPage(await _platform.GetAuditLogAsync(request, cancellationToken));

    /// <summary>Downloads the audit log the filters select, as CSV.</summary>
    /// <remarks>
    /// One request. The client used to build this file itself by paging the list endpoint at a
    /// hundred rows a time - sixty-eight requests for sixty-eight pages - and gave up at five
    /// thousand rows. Rows are streamed to the response as they are read, so neither the server
    /// nor the browser holds the file.
    /// <para>
    /// Same filters, same sort and the same query as the list, so the file matches the screen it
    /// was exported from.
    /// </para>
    /// </remarks>
    /// <param name="request">The screen's filters and sort. Paging is ignored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A CSV file.</response>
    [HttpGet("audit/export")]
    [RequirePermission(Permissions.Platform.Audit)]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task GetAuditExportAsync(
        [FromQuery] AuditLogQuery request,
        CancellationToken cancellationToken) =>
        StreamCsvAsync(
            "audit-log.csv",
            _platform.StreamAuditLogAsync(request, cancellationToken),
            [
                new CsvColumn<AuditLogEntryResponse>("When", entry => entry.OccurredAt.ToString("O", CultureInfo.InvariantCulture)),
                new CsvColumn<AuditLogEntryResponse>("Who", entry => entry.Actor),
                new CsvColumn<AuditLogEntryResponse>("Action", entry => entry.Action),
                new CsvColumn<AuditLogEntryResponse>("Record", entry => entry.Target),
                new CsvColumn<AuditLogEntryResponse>("Workspace", entry => entry.Workspace),
                new CsvColumn<AuditLogEntryResponse>("Severity", entry => entry.Severity.ToString()),
                new CsvColumn<AuditLogEntryResponse>("IP address", entry => entry.IpAddress),
            ]);

    /// <summary>Returns infrastructure health, quotas and throughput.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The system snapshot.</response>
    [HttpGet("system")]
    [RequirePermission(Permissions.Platform.Monitoring)]
    [ProducesResponseType(typeof(ApiResponse<SystemSnapshot>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSystemAsync(CancellationToken cancellationToken) =>
        Success(await _platform.GetSystemSnapshotAsync(cancellationToken));
}
