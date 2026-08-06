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

/// <summary>Contact writes, bulk operations, import and export.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/contacts")]
[Authorize]
public sealed class ContactWriteController : ApiControllerBase
{
    /// <summary>Largest upload accepted, matching the row cap in the import service.</summary>
    private const long MaxUploadBytes = 20 * 1024 * 1024;

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
    [HttpPost]
    [RequirePermission(Permissions.Contacts.Create)]
    [ProducesResponseType(typeof(ApiResponse<ContactResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
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
    /// <response code="200">How many were removed and how many were skipped.</response>
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

        return Success(result, $"{result.Affected} contacts deleted.");
    }

    /// <summary>Applies tags to several contacts, leaving existing tags in place.</summary>
    /// <response code="200">How many assignments were added.</response>
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

        return Success(result, $"{result.Affected} tags applied.");
    }

    /// <summary>Adds several contacts to groups, leaving existing memberships in place.</summary>
    /// <response code="200">How many memberships were added.</response>
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

        return Success(result, $"{result.Affected} contacts added to groups.");
    }

    /// <summary>Uploads a CSV and returns a preview. Step one of the import wizard.</summary>
    /// <remarks>
    /// Parses and stages the file, detects columns, counts duplicates by phone number, and
    /// suggests a column mapping. Nothing is created until the commit call.
    /// </remarks>
    /// <param name="file">The CSV file.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The preview and the batch identifier.</response>
    /// <response code="422">The file is empty, too large, or has no data rows.</response>
    [HttpPost("import")]
    [RequirePermission(Permissions.Contacts.Import)]
    [RequestSizeLimit(MaxUploadBytes)]
    [ProducesResponseType(typeof(ApiResponse<ImportPreview>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ImportAsync(
        IFormFile? file,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new ValidationException("file", "Choose a CSV file to upload.");
        }

        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        // Read as UTF-8 with BOM detection: spreadsheet exports routinely carry one, and a BOM
        // left in place becomes part of the first column header and breaks the mapping.
        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = await reader.ReadToEndAsync(cancellationToken);

        var preview = await _import.PreviewAsync(file.FileName, content, cancellationToken);

        return Success(
            preview,
            $"{preview.TotalRows} rows read. {preview.DuplicateRows} duplicates and {preview.InvalidRows} invalid rows found.");
    }

    /// <summary>Commits a staged import. Step two of the wizard.</summary>
    /// <param name="batchId">Batch identifier from the preview call.</param>
    /// <param name="request">Column mapping and options.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">How many were imported, skipped and failed.</response>
    [HttpPost("import/{batchId}/commit")]
    [RequirePermission(Permissions.Contacts.Import)]
    [ProducesResponseType(typeof(ApiResponse<ImportResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CommitImportAsync(
        string batchId,
        [FromBody] CommitImportRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var result = await _import.CommitAsync(batchId, request, cancellationToken);

        return Success(result, $"{result.Imported} contacts imported.");
    }

    /// <summary>Exports every contact as CSV.</summary>
    /// <remarks>Streamed, so a large contact book does not have to be buffered in memory first.</remarks>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A CSV file.</response>
    [HttpGet("export")]
    [RequirePermission(Permissions.Contacts.Export)]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task ExportAsync([FromQuery] string? adminId, CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"contacts-{DateTime.UtcNow:yyyyMMdd}.csv\"";

        await using var writer = new StreamWriter(Response.Body, Encoding.UTF8);

        await foreach (var line in _contacts.ExportAsync(cancellationToken))
        {
            await writer.WriteAsync(line);
        }

        await writer.FlushAsync(cancellationToken);
    }
}
