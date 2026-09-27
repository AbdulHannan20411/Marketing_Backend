using Asp.Versioning;
using Marketing.Application.DTOs.Exports;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.Exports;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>
/// Asynchronous exports of any list view.
/// </summary>
/// <remarks>
/// <b>Nothing here reads the data being exported.</b> Creating an export is a validation, an
/// insert and a publish, so the response time is the same for ten rows and for a million - which
/// is the whole reason the feature exists. The reading, the writing and the storing happen in a
/// worker, and the client hears about it over SignalR.
/// <para>
/// No permission attribute on the class. Each dataset carries its own - a contacts export needs
/// what the contacts list needs - and the service applies it, because one attribute here could
/// only be the union or the intersection of them and both would be wrong.
/// </para>
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/exports")]
[Authorize]
public sealed class ExportsController : ApiControllerBase
{
    private readonly IExportJobService _exports;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="exports">Export job service.</param>
    /// <param name="scope">Resolves <c>?adminId=</c> for platform staff.</param>
    public ExportsController(IExportJobService exports, ITenantScopeResolver scope)
    {
        _exports = exports;
        _scope = scope;
    }

    /// <summary>Queues an export of a list view and returns at once.</summary>
    /// <remarks>
    /// The body is the list view's own state - its search, its filters, its sort and its chosen
    /// columns - so the file is of what the user was looking at rather than of the whole table.
    /// Unknown filter names are ignored; an unknown column name is a 422, because a file quietly
    /// missing a column is discovered after it has been sent to somebody.
    /// <para>
    /// Clicking Export twice inside two minutes returns the export already running, with
    /// <c>reused: true</c>, rather than starting a second one.
    /// </para>
    /// </remarks>
    /// <param name="request">What to export, and the list view's state.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="202">Queued. The body carries the job id.</response>
    /// <response code="403">The caller may not read this list.</response>
    /// <response code="404">No such dataset.</response>
    /// <response code="422">A column that is not on the dataset's allow-list.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<ExportAcceptedResponse>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateExportRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var accepted = await _exports.CreateAsync(request, cancellationToken);

        return SuccessAccepted(accepted);
    }

    /// <summary>Lists the caller's own exports, newest first.</summary>
    /// <remarks>
    /// The caller's, not the workspace's. An export is one person's extract of data they chose,
    /// and a colleague in the same workspace has no reason to see that it happened.
    /// </remarks>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Rows per page, capped at 50.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of exports.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ExportJobResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPageAsync(
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return SuccessPage(await _exports.GetPageAsync(page, pageSize, cancellationToken));
    }

    /// <summary>Lists what this caller can export, and the columns each list offers.</summary>
    /// <remarks>
    /// Filtered to what they hold the permission for, so the export dialog never offers a list
    /// that would answer 403.
    /// </remarks>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The datasets.</response>
    [HttpGet("datasets")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ExportDatasetResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDatasetsAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(_exports.Datasets());
    }

    /// <summary>Returns one export's state.</summary>
    /// <remarks>
    /// For the fallback path. SignalR is the primary mechanism and this exists for a client whose
    /// hub is down, and for the export centre's own refresh.
    /// </remarks>
    /// <param name="id">Prefixed export identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The export.</response>
    /// <response code="404">No such export, or it is not the caller's.</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ApiResponse<ExportJobResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _exports.GetAsync(id, cancellationToken));
    }

    /// <summary>Downloads a finished export.</summary>
    /// <remarks>
    /// Checked again here, not on the strength of having been given the link: the caller must be
    /// signed in, the export must be theirs, it must be in their workspace, it must have
    /// completed, the file must still exist and it must not have expired. Somebody else's
    /// identifier is a 404 rather than a 403, so the answer does not confirm that it names a real
    /// export.
    /// <para>
    /// The storage key never leaves the server. This route resolves it; the client only ever has
    /// the job id.
    /// </para>
    /// </remarks>
    /// <param name="id">Prefixed export identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The file.</response>
    /// <response code="404">No such export, or it is not the caller's.</response>
    /// <response code="409">Not finished, or the file has expired.</response>
    [HttpGet("{id}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DownloadAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var download = await _exports.OpenAsync(id, cancellationToken);

        return File(download.Content, download.ContentType, download.FileName);
    }

    /// <summary>Queues a failed export again.</summary>
    /// <param name="id">Prefixed export identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="202">Queued again.</response>
    /// <response code="404">No such export, or it is not the caller's.</response>
    /// <response code="409">Only a failed export can be retried.</response>
    [HttpPost("{id}/retry")]
    [ProducesResponseType(typeof(ApiResponse<ExportAcceptedResponse>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RetryAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return SuccessAccepted(await _exports.RetryAsync(id, cancellationToken));
    }

    /// <summary>Withdraws an export that has not finished.</summary>
    /// <param name="id">Prefixed export identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The withdrawn export.</response>
    /// <response code="404">No such export, or it is not the caller's.</response>
    /// <response code="409">It has already finished.</response>
    [HttpPost("{id}/cancel")]
    [ProducesResponseType(typeof(ApiResponse<ExportJobResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _exports.CancelAsync(id, cancellationToken));
    }
}
