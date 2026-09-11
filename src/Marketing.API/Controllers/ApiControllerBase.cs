using System.Text;
using Marketing.API.Export;
using Marketing.Common.Helpers;
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

    /// <summary>
    /// Returns 202 with the standard envelope, for work that was queued rather than done.
    /// </summary>
    /// <remarks>
    /// The status code is the honest one for an asynchronous import: the request was accepted, and
    /// the payload says what to poll. A 200 would tell the client the work had finished.
    /// </remarks>
    /// <typeparam name="TData">Payload type.</typeparam>
    /// <param name="data">What was queued.</param>
    /// <param name="message">Optional success message.</param>
    protected AcceptedResult SuccessAccepted<TData>(TData data, string? message = null) =>
        Accepted(ApiResponse.Ok(data, TraceId, message));

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

    /// <summary>
    /// Streams rows to the caller as a CSV download.
    /// </summary>
    /// <remarks>
    /// The shared half of every export. Each endpoint keeps its own route, permission, query and
    /// column list; what is identical everywhere - the quoting, the byte order mark, the headers,
    /// writing as rows arrive - lives here and is written once.
    /// <para>
    /// Nothing about the file is taken from the request. The columns are supplied by the endpoint,
    /// so a caller cannot ask for a field the endpoint did not choose to publish.
    /// </para>
    /// <para>
    /// Rows are written straight to the response body as they arrive from the database. Buffering
    /// would size the request by how much data the workspace happens to hold, which for an export
    /// is exactly the number nobody controls.
    /// </para>
    /// </remarks>
    /// <typeparam name="TRow">Row type.</typeparam>
    /// <param name="fileName">Suggested file name, including the extension.</param>
    /// <param name="rows">Rows to write, streamed.</param>
    /// <param name="columns">Column headings and accessors, in output order.</param>
    protected async Task StreamCsvAsync<TRow>(
        string fileName,
        IAsyncEnumerable<TRow> rows,
        IReadOnlyList<CsvColumn<TRow>> columns)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            throw new ArgumentException("An export needs at least one column.", nameof(columns));
        }

        Response.ContentType = "text/csv; charset=utf-8";

        // Quoted, because a name is allowed to contain a space and an unquoted one would be
        // truncated at it by the browser.
        Response.Headers.ContentDisposition = $"attachment; filename=\"{SanitiseFileName(fileName)}\"";

        // Written by hand rather than by an encoding preamble: UTF8Encoding(false) is deliberate so
        // the mark appears exactly once, at the front, and not again on any later flush.
        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(false));

        await writer.WriteAsync(Csv.ByteOrderMark);
        await writer.WriteAsync(Csv.Row([.. columns.Select(column => column.Heading)]));

        var cells = new string?[columns.Count];

        await foreach (var row in rows)
        {
            for (var index = 0; index < columns.Count; index++)
            {
                cells[index] = columns[index].Value(row);
            }

            await writer.WriteAsync(Csv.Row(cells));
        }
    }

    /// <summary>Characters a download file name may not contain.</summary>
    /// <remarks>
    /// The quote is the one that matters: the name is interpolated into a quoted header
    /// value, so a quote inside it would close that value early and let whatever follows be
    /// read as further header content.
    /// </remarks>
    private static readonly System.Collections.Generic.HashSet<char> InvalidFileNameCharacters =
        ['"', '\\', '/', ':', '*', '?', '<', '>', '|'];

    /// <summary>
    /// Strips anything from a file name that would let it escape the download.
    /// </summary>
    /// <remarks>
    /// Names are literals in our own code today, not user input. Sanitised anyway because the day
    /// one is built from a campaign name is the day a quote or a newline in it rewrites the
    /// response headers, and that is not a change anybody would think to review for this.
    /// </remarks>
    private static string SanitiseFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var cleaned = new string([.. fileName.Where(character =>
            !char.IsControl(character)
            && !InvalidFileNameCharacters.Contains(character))]);

        return cleaned.Length > 0 ? cleaned : "export.csv";
    }
}
