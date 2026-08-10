using Asp.Versioning;
using Marketing.Common.Responses;
using Marketing.Infrastructure.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>
/// Reads the messages the platform tried to send, so invitation and reset flows can be completed
/// without a mail provider.
/// <para>
/// <b>Development only.</b> Every action returns <c>404</c> outside development — not <c>403</c>,
/// because an endpoint that does not exist should not confirm that it exists. The bodies it returns
/// contain live single-use tokens, which is precisely why it is gated and not merely
/// permission-protected.
/// </para>
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/dev/emails")]
[AllowAnonymous]
public sealed class DevMailboxController : ApiControllerBase
{
    private readonly DevMailbox _mailbox;
    private readonly IHostEnvironment _environment;

    /// <summary>Initialises a new instance.</summary>
    public DevMailboxController(DevMailbox mailbox, IHostEnvironment environment)
    {
        _mailbox = mailbox;
        _environment = environment;
    }

    /// <summary>Returns captured messages, newest first.</summary>
    /// <remarks>
    /// Each entry carries a <c>link</c> — the activation or reset URL with its single-use token.
    /// Take the <c>token</c> query parameter from it and post it to
    /// <c>/auth/accept-invitation</c> or <c>/auth/reset-password</c>.
    /// </remarks>
    /// <param name="to">Optional recipient address to filter by.</param>
    /// <response code="200">The captured messages.</response>
    /// <response code="404">Not running in development.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<CapturedEmail>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Get([FromQuery] string? to)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var messages = _mailbox.All();

        if (to is { Length: > 0 })
        {
            messages = [.. messages.Where(message =>
                string.Equals(message.ToAddress, to, StringComparison.OrdinalIgnoreCase))];
        }

        return Success(messages);
    }

    /// <summary>Empties the mailbox.</summary>
    /// <response code="200">The mailbox was emptied.</response>
    /// <response code="404">Not running in development.</response>
    [HttpDelete]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Clear()
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        _mailbox.Clear();

        return SuccessEmpty("Mailbox emptied.");
    }
}
