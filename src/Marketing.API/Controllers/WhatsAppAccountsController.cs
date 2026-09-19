using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.WhatsApp;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>A workspace's WhatsApp numbers.</summary>
/// <remarks>
/// Every route answers only for numbers the caller may view. One they may not is a 404, exactly as
/// one that does not exist, so a number's existence cannot be probed without access to it.
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/whatsapp/accounts")]
[Authorize]
[RequireModule(PlanModules.WhatsApp)]
public sealed class WhatsAppAccountsController : ApiControllerBase
{
    private readonly IWhatsAppAccountService _accounts;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppAccountsController(IWhatsAppAccountService accounts, ITenantScopeResolver scope)
    {
        _accounts = accounts;
        _scope = scope;
    }

    /// <summary>Lists the numbers the caller may view, with the plan's allowance.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The numbers. An employee with access to none gets an empty list.</response>
    [HttpGet]
    [RequirePermission(
        Permissions.WhatsApp.TemplatesView,
        Permissions.WhatsApp.Connect,
        Permissions.WhatsApp.InboxView)]
    [ProducesResponseType(typeof(ApiResponse<WhatsAppAccountListResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAsync([FromQuery] string? adminId, CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _accounts.ListAsync(cancellationToken));
    }

    /// <summary>Returns one number.</summary>
    /// <param name="id">Account id, <c>wa_…</c>.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The number.</response>
    /// <response code="404">No such number, or one the caller may not view.</response>
    [HttpGet("{id}")]
    [RequirePermission(
        Permissions.WhatsApp.TemplatesView,
        Permissions.WhatsApp.Connect,
        Permissions.WhatsApp.InboxView)]
    [ProducesResponseType(typeof(ApiResponse<WhatsAppAccountResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAsync(string id, [FromQuery] string? adminId, CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _accounts.GetAsync(id, cancellationToken));
    }

    /// <summary>Renames a number.</summary>
    /// <param name="id">Account id.</param>
    /// <param name="request">The new label.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The renamed number.</response>
    /// <response code="409">Another number already has that label (<c>whatsapp_label_taken</c>).</response>
    /// <response code="422">The label is empty or longer than 40 characters.</response>
    [HttpPatch("{id}")]
    [RequirePermission(Permissions.WhatsApp.Connect)]
    [ProducesResponseType(typeof(ApiResponse<WhatsAppAccountResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RenameAsync(
        string id,
        [FromBody] UpdateWhatsAppAccountRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _accounts.RenameAsync(id, request, cancellationToken), "Number renamed.");
    }

    /// <summary>Makes a number the workspace default.</summary>
    /// <param name="id">Account id.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Every visible number, since the previous default changed too.</response>
    /// <response code="409">The number is not connected (<c>whatsapp_account_not_connected</c>).</response>
    [HttpPost("{id}/default")]
    [RequirePermission(Permissions.WhatsApp.Connect)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<WhatsAppAccountResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetDefaultAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _accounts.SetDefaultAsync(id, cancellationToken), "Default number changed.");
    }

    /// <summary>Refreshes one number's status, quality, tier and profile from Meta.</summary>
    /// <param name="id">Account id.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The refreshed number.</response>
    [HttpPost("{id}/sync")]
    [RequirePermission(Permissions.WhatsApp.Connect)]
    [ProducesResponseType(typeof(ApiResponse<WhatsAppAccountResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SyncAsync(string id, [FromQuery] string? adminId, CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var connection = await _accounts.SyncAsync(id, cancellationToken)
                         ?? throw new Common.Exceptions.NotFoundException("WhatsApp account", id);

        return Success(await _accounts.ToResponseAsync(connection, cancellationToken), "Number refreshed.");
    }

    /// <summary>Disconnects a number, keeping its conversations and employee access.</summary>
    /// <param name="id">Account id.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The disconnected number.</response>
    [HttpPost("{id}/disconnect")]
    [RequirePermission(Permissions.WhatsApp.Disconnect)]
    [ProducesResponseType(typeof(ApiResponse<WhatsAppAccountResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DisconnectAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _accounts.DisconnectAsync(id, cancellationToken), "Number disconnected.");
    }

    /// <summary>Removes a disconnected number and frees its slot on the plan.</summary>
    /// <param name="id">Account id.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="204">Removed. Its conversation history is kept.</response>
    /// <response code="409">It is still connected (<c>whatsapp_account_connected</c>).</response>
    [HttpDelete("{id}")]
    [RequirePermission(Permissions.WhatsApp.Disconnect)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteAsync(string id, [FromQuery] string? adminId, CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        await _accounts.DeleteAsync(id, cancellationToken);

        return NoContent();
    }
}
