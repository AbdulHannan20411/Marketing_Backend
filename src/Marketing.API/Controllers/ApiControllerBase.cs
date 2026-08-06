using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Marketing.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>
/// Base class for every controller.
/// <para>
/// Controllers do three things: bind, delegate to a service, shape the response. No business
/// rules, no validation blocks, no data access - those live in <c>Marketing.Application</c> and
/// <c>Marketing.Business</c>, which is what keeps them unit-testable without an HTTP context.
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
    /// <summary>
    /// Correlation id for the current request, echoed in the response envelope so a user reporting
    /// a problem can quote a value that appears in the logs.
    /// </summary>
    protected string TraceId =>
        HttpContext.Items[AppConstants.Headers.CorrelationId] as string ?? HttpContext.TraceIdentifier;

    /// <summary>Wraps a payload in the standard success envelope.</summary>
    /// <typeparam name="TData">Payload type.</typeparam>
    /// <param name="data">Payload.</param>
    /// <param name="message">
    /// Optional success toast. Set it on writes; leave it null on reads, or the user gets a toast
    /// every time a list refreshes.
    /// </param>
    protected OkObjectResult Success<TData>(TData data, string? message = null) =>
        Ok(ApiResponse.Ok(data, TraceId, message));

    /// <summary>
    /// Wraps a page. The paged envelope goes in <c>data</c> whole, because the client's
    /// <c>PagedResult&lt;T&gt;</c> expects <c>items</c> and the counters together in one object.
    /// </summary>
    /// <typeparam name="TItem">Item type.</typeparam>
    /// <param name="page">Page to return.</param>
    /// <param name="message">Optional success message.</param>
    protected OkObjectResult SuccessPage<TItem>(PagedResult<TItem> page, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        Response.Headers[AppConstants.Headers.TotalCount] =
            page.TotalItems.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return Ok(ApiResponse.Ok(page, TraceId, message));
    }

    /// <summary>
    /// Returns a success envelope with a null payload, for endpoints the contract defines as
    /// returning <c>data: null</c>.
    /// </summary>
    /// <param name="message">Optional success message.</param>
    protected OkObjectResult SuccessEmpty(string? message = null) =>
        Ok(ApiResponse.Empty(TraceId, message));

    /// <summary>Returns 201 with the standard envelope and a Location header.</summary>
    /// <typeparam name="TData">Payload type.</typeparam>
    /// <param name="actionName">Action that reads the created resource.</param>
    /// <param name="routeValues">Route values for that action.</param>
    /// <param name="data">Created resource.</param>
    /// <param name="message">Optional success message.</param>
    protected CreatedAtActionResult SuccessCreated<TData>(
        string actionName,
        object routeValues,
        TData data,
        string? message = null) =>
        CreatedAtAction(actionName, routeValues, ApiResponse.Ok(data, TraceId, message));

    /// <summary>
    /// Resolves the Super Admin scoping parameter.
    /// <para>
    /// Honoured only when the caller is a Super Admin. For any other role it is <b>ignored rather
    /// than rejected</b>, exactly as the contract requires, so a forged query parameter is inert
    /// instead of being a probe that tells the caller the parameter means something.
    /// </para>
    /// </summary>
    /// <param name="adminId">Value supplied on the query string, if any.</param>
    /// <param name="currentUser">The authenticated principal.</param>
    /// <returns>The admin account to scope to, or null to use the caller's own tenant.</returns>
    protected static string? ResolveScope(string? adminId, ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(currentUser);

        return currentUser.IsSuperAdmin ? adminId : null;
    }
}
