using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Imports;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.Imports;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.API.Controllers;

/// <summary>
/// Importing contacts from a file.
/// <para>
/// Every long-running step returns immediately with a batch to poll. The client drives the wizard —
/// upload, map, commit — and watches <c>status</c> and <c>progressPercent</c> between steps, either
/// by polling the detail endpoint or by listening for <c>importProgress</c> on the realtime hub.
/// </para>
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/contact-imports")]
[Authorize]
[RequireModule(PlanModules.Crm)]
public sealed class ContactImportsController : ApiControllerBase
{
    private readonly IImportHistoryService _history;
    private readonly IImportService _imports;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public ContactImportsController(
        IImportHistoryService history,
        IImportService imports,
        ITenantScopeResolver scope)
    {
        _history = history;
        _imports = imports;
        _scope = scope;
    }

    /// <summary>Downloads the blank CSV an operator fills in.</summary>
    /// <remarks>
    /// Carries one filled-in example row, because the format of the status, tag and group columns
    /// is not obvious from the header alone.
    /// </remarks>
    /// <response code="200">The template.</response>
    [HttpGet("template")]
    [RequirePermission(Permissions.Contacts.Import)]
    [Produces(ContentTypes.Csv)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetTemplate()
    {
        var template = _imports.Template();

        return File(template.Content, template.ContentType, template.FileName);
    }

    /// <summary>Uploads a file and queues it for reading.</summary>
    /// <remarks>
    /// Returns as soon as the file is stored. The rows are read by a worker, so the batch comes
    /// back <c>Pending</c> and reaches <c>AwaitingMapping</c> once the headers are known.
    /// </remarks>
    /// <param name="request">The file and how duplicates should be treated.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="202">The upload was accepted and queued.</response>
    /// <response code="422">The file is empty, too large, or of a type we cannot read.</response>
    [HttpPost]
    [RequirePermission(Permissions.Contacts.Import)]
    [ProducesResponseType(typeof(ApiResponse<ImportUploadResponse>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UploadAsync(
        [FromForm] ImportUploadRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        if (request.File is not { Length: > 0 })
        {
            throw new Common.Exceptions.ValidationException("file", "Choose a file to import.");
        }

        // Opened rather than buffered: the stream goes straight to storage, so a large upload is
        // never held in the API's memory in its entirety.
        await using var content = request.File.OpenReadStream();

        var accepted = await _imports.UploadAsync(
            new ImportUploadCommand(
                request.File.FileName,
                content,
                request.File.Length,
                request.DuplicateStrategy),
            cancellationToken);

        return SuccessAccepted(accepted);
    }

    /// <summary>Returns a filtered, searched page of imports, newest first.</summary>
    /// <remarks>
    /// <c>status</c> accepts <c>all</c> or any batch status; <c>search</c> matches the file name,
    /// case-insensitively; <c>from</c> and <c>to</c> bound the upload date. Sortable columns are
    /// <c>fileName</c>, <c>fileSizeBytes</c>, <c>status</c>, <c>totalRows</c>, <c>failedCount</c>,
    /// <c>uploadedAt</c> and <c>completedAt</c>.
    /// </remarks>
    /// <param name="query">Paging, search and filters.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of imports.</response>
    [HttpGet]
    [RequirePermission(Permissions.Contacts.Import, Permissions.Contacts.View)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ImportBatchListItem>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] ImportBatchQuery query,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return SuccessPage(await _history.GetBatchesAsync(query, cancellationToken));
    }

    /// <summary>Returns one import in full.</summary>
    /// <remarks>
    /// The endpoint the wizard polls. It carries the detected columns, the suggested and saved
    /// mappings, live progress and the grouped failure counts.
    /// </remarks>
    /// <param name="id">Prefixed import identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The import.</response>
    /// <response code="404">No such import, or it belongs to another tenant.</response>
    [HttpGet("{id}")]
    [RequirePermission(Permissions.Contacts.Import, Permissions.Contacts.View)]
    [ProducesResponseType(typeof(ApiResponse<ImportBatchDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIdAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _history.GetBatchAsync(id, cancellationToken));
    }

    /// <summary>Returns a page of an import's staged rows.</summary>
    /// <remarks>
    /// Each row's cells are keyed by the source column header, so the client renders one table
    /// column per detected column without tracking positions. <c>status</c> narrows the page to the
    /// rows the operator is reviewing, and accepts <c>all</c>.
    /// </remarks>
    /// <param name="id">Prefixed import identifier.</param>
    /// <param name="filter">Paging and the row status filter.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of rows.</response>
    [HttpGet("{id}/rows")]
    [RequirePermission(Permissions.Contacts.Import, Permissions.Contacts.View)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ImportRowDetail>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRowsAsync(
        string id,
        [FromQuery] ImportRowFilter filter,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return SuccessPage(await _history.GetRowsAsync(id, filter, cancellationToken));
    }

    /// <summary>Saves which file column feeds which contact field.</summary>
    /// <param name="id">Prefixed import identifier.</param>
    /// <param name="request">The mapping. All seven keys; null means unmapped.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated import.</response>
    /// <response code="409">The import is past the point where its mapping can change.</response>
    /// <response code="422">The mapping omits the number, or names a column the file does not have.</response>
    [HttpPut("{id}/mapping")]
    [RequirePermission(Permissions.Contacts.Import)]
    [ProducesResponseType(typeof(ApiResponse<ImportBatchDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SaveMappingAsync(
        string id,
        [FromBody] SaveImportMappingRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _imports.SaveMappingAsync(id, request.Mapping, cancellationToken));
    }

    /// <summary>Queues the writing of an import's rows as contacts.</summary>
    /// <remarks>
    /// Returns immediately. The contacts are written in chunks by a worker, and the batch reports
    /// its progress as it goes.
    /// </remarks>
    /// <param name="id">Prefixed import identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="202">The commit was queued.</response>
    /// <response code="409">The import has no mapping, or is not waiting to be confirmed.</response>
    [HttpPost("{id}/commit")]
    [RequirePermission(Permissions.Contacts.Import)]
    [ProducesResponseType(typeof(ApiResponse<ImportCommitResponse>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CommitAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var accepted = await _imports.CommitAsync(id, cancellationToken);

        return SuccessAccepted(accepted);
    }

    /// <summary>Abandons an import that has not started writing.</summary>
    /// <param name="id">Prefixed import identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The cancelled import.</response>
    /// <response code="409">The import is already writing contacts, or has finished.</response>
    [HttpPost("{id}/cancel")]
    [RequirePermission(Permissions.Contacts.Import)]
    [ProducesResponseType(typeof(ApiResponse<ImportBatchDetail>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _imports.CancelAsync(id, cancellationToken));
    }

    /// <summary>Queues a workbook of the rows that could not be used.</summary>
    /// <remarks>
    /// Generated rather than returned, because a file with tens of thousands of failures takes long
    /// enough to build that a request would time out. Poll the returned export for its status.
    /// </remarks>
    /// <param name="id">Prefixed import identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="202">The export was queued.</response>
    /// <response code="409">Every row in the import was used; there is nothing to export.</response>
    [HttpPost("{id}/failed-records/export")]
    [RequirePermission(Permissions.Contacts.Import)]
    [ProducesResponseType(typeof(ApiResponse<ImportExportJob>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ExportFailedRecordsAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var accepted = await _imports.RequestErrorExportAsync(id, cancellationToken);

        return SuccessAccepted(accepted);
    }

    /// <summary>Returns an export's progress.</summary>
    /// <param name="exportId">Prefixed export identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The export.</response>
    /// <response code="404">No such export, or it belongs to another tenant.</response>
    [HttpGet("exports/{exportId}")]
    [RequirePermission(Permissions.Contacts.Import, Permissions.Contacts.View)]
    [ProducesResponseType(typeof(ApiResponse<ImportExportJob>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetExportAsync(
        string exportId,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _imports.GetExportAsync(exportId, cancellationToken));
    }

    /// <summary>Downloads a finished export.</summary>
    /// <param name="exportId">Prefixed export identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The workbook.</response>
    /// <response code="409">The export is still being generated, or failed.</response>
    /// <response code="404">No such export, or it belongs to another tenant.</response>
    [HttpGet("exports/{exportId}/download")]
    [RequirePermission(Permissions.Contacts.Import, Permissions.Contacts.View)]
    [Produces(ContentTypes.Xlsx)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadExportAsync(
        string exportId,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var download = await _imports.OpenExportAsync(exportId, cancellationToken);

        return File(download.Content, download.ContentType, download.FileName);
    }
}

/// <summary>The multipart body of an upload.</summary>
public sealed class ImportUploadRequest
{
    /// <summary>The file. CSV or Excel.</summary>
    public IFormFile? File { get; init; }

    /// <summary>
    /// What the commit does with a row whose number already exists.
    /// <para>
    /// Chosen at upload time so the parse can classify duplicates against it, and changeable up to
    /// the moment the commit is queued.
    /// </para>
    /// </summary>
    public ImportDuplicateStrategy DuplicateStrategy { get; init; } = ImportDuplicateStrategy.Skip;
}
