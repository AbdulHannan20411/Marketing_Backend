using System.Text;
using Asp.Versioning;
using Marketing.API.Export;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.DTOs.Dashboard;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Helpers;
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

    /// <summary>Streams every delivery failure as a CSV file.</summary>
    /// <remarks>
    /// The whole result set, not a page: a partial failure log that looks complete is a worse
    /// artefact than none, which is also why the client only falls back to building the file itself
    /// on a 404 and never on an error.
    /// <para>
    /// Gated on <see cref="Permissions.Reports.Export"/> rather than the CSV-specific permission.
    /// The sensitive act is extracting the whole log - which carries every recipient's number - and
    /// that is the same act whatever the file extension.
    /// </para>
    /// <para>
    /// Written straight to the response as rows arrive. Buffering it would hold a workspace's
    /// entire failure history in memory to produce a file that is streamed anyway.
    /// </para>
    /// </remarks>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The CSV file.</response>
    [HttpGet("failures/export")]
    [RequirePermission(Permissions.Reports.Export)]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task ExportFailuresAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        await StreamCsvAsync(
            "delivery-failures.csv",
            _analytics.StreamFailuresAsync(cancellationToken),
            [
                new CsvColumn<DeliveryFailureResponse>("Recipient", row => row.PhoneNumber),
                new CsvColumn<DeliveryFailureResponse>("Contact", row => row.ContactName),
                new CsvColumn<DeliveryFailureResponse>("Campaign", row => row.CampaignName),
                new CsvColumn<DeliveryFailureResponse>("Reason", row => row.Reason),

                // Its own column although the reason text already contains it. Buried in prose it
                // cannot be sorted or grouped, and grouping by code is the first thing anyone does
                // with a failure log.
                new CsvColumn<DeliveryFailureResponse>("Error code", row => Csv.Number(row.ErrorCode)),
                new CsvColumn<DeliveryFailureResponse>("Occurred at", row => Csv.Instant(row.OccurredAt)),
            ]);
    }
}
