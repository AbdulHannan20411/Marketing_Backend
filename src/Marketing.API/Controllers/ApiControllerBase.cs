using Marketing.Common.Responses;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>
/// Base class for every controller.
/// <para>
/// Controllers in this codebase do three things: bind, delegate to a service, shape the response.
/// They contain no business rules, no validation blocks and no data access - those live in
/// <c>Marketing.Application</c> and <c>Marketing.Business</c> respectively, which is what keeps
/// them unit-testable without an HTTP context.
/// </para>
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>Wraps a payload in the standard success envelope.</summary>
    /// <typeparam name="TData">Payload type.</typeparam>
    /// <param name="data">Payload.</param>
    /// <param name="message">Optional message suitable for a toast notification.</param>
    protected OkObjectResult Success<TData>(TData data, string? message = null) =>
        Ok(ApiResponse.Ok(data, message));

    /// <summary>
    /// Wraps a page in the standard success envelope and mirrors the total count onto a response
    /// header, so a client can read it without parsing the body.
    /// </summary>
    /// <typeparam name="TItem">Item type.</typeparam>
    /// <param name="page">Page to return.</param>
    protected OkObjectResult SuccessPage<TItem>(PagedResult<TItem> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        Response.Headers[Common.Constants.AppConstants.Headers.TotalCount] =
            page.TotalCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return Ok(ApiResponse.OkPage(page));
    }

    /// <summary>Returns 201 with the standard envelope and a Location header.</summary>
    /// <typeparam name="TData">Payload type.</typeparam>
    /// <param name="actionName">Action that reads the created resource.</param>
    /// <param name="routeValues">Route values for that action.</param>
    /// <param name="data">Created resource.</param>
    protected CreatedAtActionResult SuccessCreated<TData>(
        string actionName,
        object routeValues,
        TData data) =>
        CreatedAtAction(actionName, routeValues, ApiResponse.Ok(data));
}
