using System.Text.Json;
using Asp.Versioning;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
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

    /// <summary>Returns which groups of notifications the caller still wants.</summary>
    /// <remarks>
    /// Always all six, and always <c>true</c> for security and system. A user who has never
    /// changed anything gets everything on rather than a 404, which would read as "this API has no
    /// preferences at all".
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The switches.</response>
    [HttpGet("preferences")]
    [ProducesResponseType(typeof(ApiResponse<NotificationPreferences>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPreferencesAsync(CancellationToken cancellationToken) =>
        Success(await _notifications.GetPreferencesAsync(cancellationToken));

    /// <summary>Saves the caller's switches.</summary>
    /// <remarks>
    /// Partial: a category the body leaves out keeps what it had. Unknown keys are ignored, so an
    /// older client sending a category that no longer exists - or a newer one sending a category
    /// this build has not added yet - still saves the rest. Security and system are stored as on
    /// whatever the body says.
    /// </remarks>
    /// <param name="body">An object of booleans, keyed by category.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The switches as stored.</response>
    /// <response code="422">The body was not an object of booleans.</response>
    [HttpPut("preferences")]
    [ProducesResponseType(typeof(ApiResponse<NotificationPreferences>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdatePreferencesAsync(
        [FromBody] JsonElement body,
        CancellationToken cancellationToken)
    {
        // Read as raw JSON rather than bound to a type: a model-binding failure is a 400 with the
        // framework's wording, and this contract asks for a 422 that names the offending key.
        return Success(
            await _notifications.UpdatePreferencesAsync(ReadSwitches(body), cancellationToken),
            "Notification settings saved.");
    }

    /// <summary>Reads the booleans out of the body, refusing anything else.</summary>
    /// <param name="body">The request body.</param>
    private static Dictionary<string, bool> ReadSwitches(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ValidationException("preferences", "Send an object of true/false switches.");
        }

        var wanted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in body.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.True:
                case JsonValueKind.False:
                    wanted[property.Name] = property.Value.GetBoolean();
                    break;

                default:
                    throw new ValidationException(
                        property.Name,
                        $"\"{property.Name}\" must be true or false.");
            }
        }

        return wanted;
    }

    /// <summary>Marks every unread notification read.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The updated notifications.</response>
    [HttpPost("read-all")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AppNotification>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAllReadAsync(CancellationToken cancellationToken) =>
        Success(await _notifications.MarkAllReadAsync(cancellationToken));
}
