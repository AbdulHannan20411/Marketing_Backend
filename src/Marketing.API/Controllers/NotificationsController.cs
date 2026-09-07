using Asp.Versioning;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>
/// The signed-in user's notifications.
/// </summary>
/// <remarks>
/// Behind no permission. Notifications are addressed to a person, not granted to a role: a user who
/// can sign in can read what was sent to them, and there is nothing here another permission would
/// usefully gate.
/// <para>
/// Every route answers for the caller alone and takes no <c>adminId</c>. Scoping is derived from
/// the token - a platform administrator sees what is addressed to them personally and genuine
/// platform-wide announcements, never a customer workspace's notifications.
/// </para>
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/notifications")]
[Authorize]
public sealed class NotificationsController : ApiControllerBase
{
    private readonly INotificationService _notifications;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="notifications">Notification service.</param>
    public NotificationsController(INotificationService notifications)
    {
        _notifications = notifications;
    }

    /// <summary>Returns the caller's most recent notifications, newest first.</summary>
    /// <remarks>
    /// A bare list rather than a page. The set is capped server-side and backs a dropdown and a
    /// notification centre, neither of which paginates; a paged history would be its own endpoint.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The notifications.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AppNotification>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken) =>
        Success(await _notifications.GetAsync(cancellationToken));

    /// <summary>Marks one notification read.</summary>
    /// <remarks>
    /// Returns the whole list rather than the single row, so the client replaces its state from one
    /// authoritative answer instead of patching a local copy that can drift from the server.
    /// </remarks>
    /// <param name="id">Notification identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated notifications.</response>
    /// <response code="404">No such notification for this user.</response>
    [HttpPost("{id}/read")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AppNotification>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkReadAsync(string id, CancellationToken cancellationToken) =>
        Success(await _notifications.MarkReadAsync(id, cancellationToken));

    /// <summary>Marks every unread notification read.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated notifications.</response>
    [HttpPost("read-all")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AppNotification>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAllReadAsync(CancellationToken cancellationToken) =>
        Success(await _notifications.MarkAllReadAsync(cancellationToken));
}
