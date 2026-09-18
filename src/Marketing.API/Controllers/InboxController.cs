using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.WhatsApp;
using Marketing.Common.Constants;
using Marketing.Common.Requests;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>The shared inbox for the resolved tenant.</summary>
/// <remarks>
/// Gated on the WhatsApp plan module as well as on permissions: a workspace whose plan does not
/// include the channel has no conversations to read, and saying so plainly is better than an empty
/// list that looks like a fault.
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/whatsapp/conversations")]
[Authorize]
[RequireModule(PlanModules.WhatsApp)]
public sealed class InboxController : ApiControllerBase
{
    private readonly IInboxService _inbox;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public InboxController(IInboxService inbox, ITenantScopeResolver scope)
    {
        _inbox = inbox;
        _scope = scope;
    }

    /// <summary>Lists conversations, newest activity first.</summary>
    /// <param name="query">Paging and search.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of conversations.</response>
    [HttpGet]
    [RequirePermission(Permissions.WhatsApp.InboxView)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ConversationResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchAsync(
        [FromQuery] ConversationQuery query,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return SuccessPage(await _inbox.SearchAsync(query, cancellationToken));
    }

    /// <summary>Reads one conversation.</summary>
    /// <param name="id">Public conversation identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The conversation.</response>
    /// <response code="404">No such conversation in this workspace.</response>
    [HttpGet("{id}")]
    [RequirePermission(Permissions.WhatsApp.InboxView)]
    [ProducesResponseType(typeof(ApiResponse<ConversationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _inbox.GetAsync(id, cancellationToken));
    }

    /// <summary>Reads a conversation's messages, oldest first.</summary>
    /// <param name="id">Public conversation identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of messages.</response>
    /// <response code="404">No such conversation in this workspace.</response>
    [HttpGet("{id}/messages")]
    [RequirePermission(Permissions.WhatsApp.InboxView)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ConversationMessageResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMessagesAsync(
        string id,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return SuccessPage(await _inbox.GetMessagesAsync(
            id,
            page <= 0 ? 1 : page,
            pageSize <= 0 ? 50 : pageSize,
            cancellationToken));
    }

    /// <summary>Sends a free-form reply.</summary>
    /// <remarks>
    /// Allowed only while the customer's 24-hour window is open. Outside it Meta accepts nothing but
    /// an approved template, which is a campaign rather than a reply.
    /// </remarks>
    /// <param name="id">Public conversation identifier.</param>
    /// <param name="request">What to send.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The sent message.</response>
    /// <response code="409">
    /// The window has closed (<c>window_closed</c>), or no WhatsApp account is connected
    /// (<c>not_connected</c>).
    /// </response>
    /// <response code="422">Nothing to send, or a kind that cannot be sent as a reply.</response>
    [HttpPost("{id}/messages")]
    [RequirePermission(Permissions.WhatsApp.InboxReply)]
    [ProducesResponseType(typeof(ApiResponse<ConversationMessageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SendAsync(
        string id,
        [FromBody] SendConversationMessageRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _inbox.SendAsync(id, request, cancellationToken));
    }

    /// <summary>Marks a conversation as read.</summary>
    /// <param name="id">Public conversation identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated conversation.</response>
    /// <response code="404">No such conversation in this workspace.</response>
    [HttpPost("{id}/read")]
    [RequirePermission(Permissions.WhatsApp.InboxView)]
    [ProducesResponseType(typeof(ApiResponse<ConversationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkReadAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _inbox.MarkReadAsync(id, cancellationToken));
    }
}
