using System.Text;
using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Contacts;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>Contact writes, bulk operations, merge, import and export.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/contacts")]
[Authorize]
[RequireModule(PlanModules.Crm)]
public sealed class ContactWriteController : ApiControllerBase
{
    /// <summary>Largest upload accepted, matching the row cap in the import service.</summary>
    private const long MaxUploadBytes = 10 * 1024 * 1024;

    private readonly IContactWriteService _contacts;
    private readonly IContactImportService _import;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public ContactWriteController(
        IContactWriteService contacts,
        IContactImportService import,
        ITenantScopeResolver scope)
    {
        _contacts = contacts;
        _import = import;
        _scope = scope;
    }

    /// <summary>Creates a contact.</summary>
    /// <response code="200">The created contact.</response>
    /// <response code="409">A contact with that phone number already exists.</response>
    /// <response code="422">A field is invalid, or the plan's contact limit has been reached.</response>
    [HttpPost]
    [RequirePermission(Permissions.Contacts.Create)]
    [ProducesResponseType(typeof(ApiResponse<ContactResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateContactRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var contact = await _contacts.CreateAsync(request, cancellationToken);

        return Success(contact, $"{contact.FullName} added.");
    }

    /// <summary>Updates a contact. Omitted fields are left unchanged.</summary>
    /// <remarks>
    /// Patch semantics. Supplying <c>tagIds</c> or <c>groupIds</c> <b>replaces</b> that whole set;
    /// use the bulk endpoints when you want to add without disturbing what is already there.
    /// </remarks>
    /// <response code="200">The updated contact.</response>
    [HttpPut("{id}")]
    [RequirePermission(Permissions.Contacts.Edit)]
    [ProducesResponseType(typeof(ApiResponse<ContactResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateAsync(
        string id,
        [FromBody] UpdateContactRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var contact = await _contacts.UpdateAsync(id, request, cancellationToken);

        return Success(contact, "Contact saved.");
    }

    /// <summary>Deletes a contact.</summary>
    /// <response code="200">The contact was deleted.</response>
    [HttpDelete("{id}")]
    [RequirePermission(Permissions.Contacts.Delete)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        await _contacts.DeleteAsync(id, cancellationToken);

        return SuccessEmpty("Contact deleted.");
    }

    /// <summary>Deletes several contacts.</summary>
    /// <remarks>
    /// Answers 200 even when some identifiers could not be deleted, listing them in
    /// <c>failed</c>. A selection built across several pages routinely contains a row someone else
    /// has since removed, and failing the whole call for it helps nobody.
    /// </remarks>
    /// <response code="200">Which were removed and which were not.</response>
    [HttpPost("bulk-delete")]
    [RequirePermission(Permissions.Contacts.Delete)]
    [ProducesResponseType(typeof(ApiResponse<BulkOperationResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> BulkDeleteAsync(
        [FromBody] BulkContactRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var result = await _contacts.BulkDeleteAsync(request, cancellationToken);

        return Success(result, $"{result.Succeeded} contacts deleted.");
    }

    /// <summary>Adds, removes or replaces tags on several contacts.</summary>
    /// <remarks><c>mode</c> defaults to <c>add</c>, which is what the table's "Add tag" action means.</remarks>
    /// <response code="200">How many were tagged, and which could not be.</response>
    [HttpPost("bulk-tag")]
    [RequirePermission(Permissions.Contacts.Edit)]
    [ProducesResponseType(typeof(ApiResponse<BulkOperationResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> BulkTagAsync(
        [FromBody] BulkTagRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var result = await _contacts.BulkTagAsync(request, cancellationToken);

        return Success(result, $"Tags updated on {result.Succeeded} contacts.");
    }

    /// <summary>Adds, removes or replaces group memberships on several contacts.</summary>
    /// <response code="200">How many were changed, and which could not be.</response>
    [HttpPost("bulk-group")]
    [RequirePermission(Permissions.Contacts.Edit)]
    [ProducesResponseType(typeof(ApiResponse<BulkOperationResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> BulkGroupAsync(
        [FromBody] BulkGroupRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var result = await _contacts.BulkGroupAsync(request, cancellationToken);

        return Success(result, $"Groups updated on {result.Succeeded} contacts.");
    }

    /// <summary>Folds several contacts into one.</summary>
    /// <remarks>
    /// The survivor takes the union of tags and groups, the most recent contact date, and any
    /// explicit overrides. The merged records are deleted, so this needs the delete permission as
    /// well as edit.
    /// </remarks>
    /// <response code="200">The surviving contact.</response>
    [HttpPost("merge")]
    [RequirePermission(Permissions.Contacts.Edit)]
    [RequirePermission(Permissions.Contacts.Delete)]
    [ProducesResponseType(typeof(ApiResponse<ContactResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> MergeAsync(
        [FromBody] MergeContactsRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var contact = await _contacts.MergeAsync(request, cancellationToken);

        return Success(contact, "Contacts merged.");
    }

    /// <summary>Uploads a CSV and returns a preview. Step one of the import wizard.</summary>
    /// <remarks>
    /// Parses and stages the file, detects columns, counts duplicates both within the file and
    /// against stored contacts, and suggests a column mapping. Nothing is created until the commit
    /// call.
    /// </remarks>
    /// <param name="file">The CSV file.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The preview and the upload identifier.</response>
    /// <response code="422">The file is empty, not a CSV, too large, or has no data rows.</response>
    [HttpPost("import/preview")]
    [RequirePermission(Permissions.Contacts.Import)]
    [RequestSizeLimit(MaxUploadBytes)]
    [ProducesResponseType(typeof(ApiResponse<ImportPreview>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> PreviewImportAsync(
        IFormFile? file,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new ValidationException("file", "Choose a CSV file to upload.");
        }

        // Extension rather than content type: browsers report CSV as anything from text/csv to
        // application/vnd.ms-excel depending on what is installed, so the reported type decides
        // nothing useful.
        if (!Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("file", "Upload a .csv file.");
        }

        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        // Read as UTF-8 with BOM detection: spreadsheet exports routinely carry one, and a BOM
        // left in place becomes part of the first column header and breaks the mapping.
        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = await reader.ReadToEndAsync(cancellationToken);

        var preview = await _import.PreviewAsync(file.FileName, content, cancellationToken);

        return Success(
            preview,
            $"{preview.TotalRows} rows read. "
            + $"{preview.DuplicatesExisting + preview.DuplicatesInFile} duplicates and "
            + $"{preview.InvalidRows.Count} invalid rows found.");
    }

    /// <summary>Commits a staged import. Step two of the wizard.</summary>
    /// <remarks>
    /// Runs inline and returns the finished result, which the contract allows. The row cap keeps
    /// the work bounded; the returned <c>jobId</c> can be re-read from the poll endpoint.
    /// </remarks>
    /// <param name="request">Upload identifier, column mapping and options.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">How many were created, updated, skipped and failed.</response>
    [HttpPost("import/commit")]
    [RequirePermission(Permissions.Contacts.Import)]
    [ProducesResponseType(typeof(ApiResponse<ImportResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CommitImportAsync(
        [FromBody] ImportCommitRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var result = await _import.CommitAsync(request, cancellationToken);

        return Success(result, $"{result.Created} contacts imported.");
    }

    /// <summary>Re-reads the outcome of an import. Step three of the wizard.</summary>
    /// <param name="jobId">Identifier from the commit call.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The import's current state.</response>
    [HttpGet("import/{jobId}")]
    [RequirePermission(Permissions.Contacts.Import)]
    [ProducesResponseType(typeof(ApiResponse<ImportResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetImportAsync(
        string jobId,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _import.GetJobAsync(jobId, cancellationToken));
    }

    /// <summary>Exports matching contacts as CSV.</summary>
    /// <remarks>
    /// Accepts every filter the list endpoint does, so "export what I am looking at" returns the
    /// rows the table is showing; supply <c>ids</c> instead to export a selection. Streamed, so a
    /// large contact book is not buffered in memory first.
    /// </remarks>
    /// <param name="query">Filters, or an explicit selection.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A CSV file.</response>
    [HttpGet("export")]
    [RequirePermission(Permissions.Contacts.Export)]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task ExportAsync(
        [FromQuery] ContactExportQuery query,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"contacts-{DateTime.UtcNow:yyyyMMdd}.csv\"";

        await using var writer = new StreamWriter(Response.Body, Encoding.UTF8);

        await foreach (var line in _contacts.ExportAsync(query, cancellationToken))
        {
            await writer.WriteAsync(line);
        }

        await writer.FlushAsync(cancellationToken);
    }
}
