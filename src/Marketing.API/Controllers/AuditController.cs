using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Audit;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.Audit;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>
/// What happened to one record: created, updated or deleted, by whom, when, and which fields moved.
/// </summary>
/// <remarks>
/// One route for every record type. Which types have a history, and who may read each, is the
/// registry in <see cref="AuditableEntities"/> - so switching history on for another record is a
/// line there rather than an endpoint here.
/// <para>
/// No blanket permission guards this route, because there is no single right one: the answer
/// depends on the record being asked about, and is enforced per request against the permission the
/// registry names.
/// </para>
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/audit")]
[Authorize]
public sealed class AuditController : ApiControllerBase
{
    private readonly IRecordHistoryService _history;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="history">Record history reader.</param>
    /// <param name="scope">Super Admin scoping, so "view as" works unchanged.</param>
    public AuditController(IRecordHistoryService history, ITenantScopeResolver scope)
    {
        _history = history;
        _scope = scope;
    }

    /// <summary>Returns one record's history, newest first.</summary>
    /// <remarks>
    /// Only the fields that changed are reported, each with its old and new value - a create has
    /// no old values, and a redacted field carries <c>{ "redacted": true }</c> instead of either.
    /// </remarks>
    /// <param name="entityName">Record type: <c>Template</c>, <c>Contact</c>, <c>Employee</c>…</param>
    /// <param name="entityId">The record's public id, such as <c>tpl_18</c>.</param>
    /// <param name="query">Paging, and filters for action, actor and date range.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of history, newest first.</response>
    /// <response code="403">The caller may not read the record itself.</response>
    /// <response code="404">Unknown record type, malformed id, or a record in another workspace.</response>
    [HttpGet("{entityName}/{entityId}")]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<RecordHistoryEntry>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAsync(
        string entityName,
        string entityId,
        [FromQuery] RecordHistoryQuery query,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return SuccessPage(await _history.GetAsync(entityName, entityId, query, cancellationToken));
    }
}
