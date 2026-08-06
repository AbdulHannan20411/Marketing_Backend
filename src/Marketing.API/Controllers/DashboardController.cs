using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.DTOs.Dashboard;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Requests;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Marketing.API.Controllers;

/// <summary>Dashboard for the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/dashboard")]
[Authorize]
public sealed class DashboardController : ApiControllerBase
{
    private readonly IAnalyticsService _analytics;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public DashboardController(IAnalyticsService analytics, ITenantScopeResolver scope)
    {
        _analytics = analytics;
        _scope = scope;
    }

    /// <summary>Returns the last 30 days for the resolved tenant.</summary>
    /// <param name="adminId">
    /// Super Admin scoping. Honoured only for platform staff; silently ignored for anyone else.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The dashboard snapshot.</response>
    [HttpGet]
    [RequirePermission(Permissions.Dashboard.View)]
    [ProducesResponseType(typeof(ApiResponse<DashboardSnapshot>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _analytics.GetDashboardAsync(cancellationToken));
    }
}

/// <summary>Reporting for the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/reports")]
[Authorize]
[EnableRateLimiting(AppConstants.RateLimits.Reports)]
public sealed class ReportsController : ApiControllerBase
{
    private readonly IAnalyticsService _analytics;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public ReportsController(IAnalyticsService analytics, ITenantScopeResolver scope)
    {
        _analytics = analytics;
        _scope = scope;
    }

    /// <summary>Returns the reporting overview, which is the same shape as the dashboard.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The reporting snapshot.</response>
    [HttpGet("overview")]
    [RequirePermission(Permissions.Reports.View)]
    [ProducesResponseType(typeof(ApiResponse<DashboardSnapshot>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOverviewAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _analytics.GetDashboardAsync(cancellationToken));
    }

    /// <summary>Returns a page of delivery failures, newest first.</summary>
    /// <param name="request">Paging parameters.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of failures.</response>
    [HttpGet("failures")]
    [RequirePermission(Permissions.Reports.View)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<DeliveryFailureResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFailuresAsync(
        [FromQuery] PageRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return SuccessPage(await _analytics.GetFailuresAsync(request, cancellationToken));
    }
}
