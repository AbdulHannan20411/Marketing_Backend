using Asp.Versioning;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
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

    /// <summary>Returns the caller's notifications, newest first.</summary>
    /// <remarks>
    /// Two shapes from one route, chosen by whether the caller asked for a page:
    /// <list type="bullet">
    /// <item>no <c>page</c> and no <c>pageSize</c> - the plain array this route has always
    /// returned, capped server-side, which the dropdown and the notification centre still use;</item>
    /// <item>either of them - a <c>PagedResult</c>-shaped answer carrying two extra counters,
    /// <c>unreadCount</c> and <c>criticalCount</c>.</item>
    /// </list>
    /// Those two counters are taken over everything addressed to the caller, ignoring the page and
    /// the filters, because the bell means "unread", not "unread on this page".
    /// <para>
    /// The filters apply to both shapes, so narrowing the list does not force a caller to page it.
    /// </para>
    /// </remarks>
    /// <param name="query">Paging and filters. All optional.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The notifications, as an array or as a page.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AppNotification>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<NotificationFeed>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] NotificationQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.WantsPage)
        {
            var feed = await _notifications.GetPageAsync(query, cancellationToken);

            Response.Headers[AppConstants.Headers.TotalCount] =
                feed.TotalItems.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return Success(feed);
        }

        if (query.UnreadOnly == true || query.Priority is not null)
        {
            // Filtered but unpaged: the same rows, in the shape the caller already handles.
            var filtered = await _notifications.GetPageAsync(
                new NotificationQuery
                {
                    PageSize = NotificationQuery.MaxPageSize,
                    UnreadOnly = query.UnreadOnly,
                    Priority = query.Priority,
                },
                cancellationToken);

            return Success(filtered.Items);
        }

        return Success(await _notifications.GetAsync(cancellationToken));
    }

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
