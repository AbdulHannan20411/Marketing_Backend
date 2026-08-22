using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.BusinessDiscovery;
using Marketing.Application.Services.BusinessDiscovery;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Marketing.API.Controllers;

/// <summary>
/// Finding businesses near a point and importing them as contacts.
/// </summary>
/// <remarks>
/// Every route is behind <see cref="Permissions.Contacts.BusinessImport"/>, enforced here and not by
/// the client hiding a tab. The permission is separate from file import on purpose: uploading a
/// spreadsheet the user already holds costs nothing, while each search here spends metered provider
/// credits belonging to the workspace.
/// <para>
/// The provider key never leaves the server, and no route accepts a tenant identifier - the tenant
/// comes from the token, always.
/// </para>
/// </remarks>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/business-discovery")]
[Authorize]
[EnableRateLimiting(AppConstants.RateLimits.Reports)]
public sealed class BusinessDiscoveryController : ApiControllerBase
{
    private readonly IBusinessDiscoveryService _discovery;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public BusinessDiscoveryController(IBusinessDiscoveryService discovery, ITenantScopeResolver scope)
    {
        _discovery = discovery;
        _scope = scope;
    }

    /// <summary>Returns the business categories the picker offers.</summary>
    /// <remarks>
    /// Provider-neutral slugs, not raw provider tokens, so the provider can change without breaking
    /// a saved search. Never returns an empty list to mean "no opinion".
    /// </remarks>
    /// <response code="200">The categories.</response>
    [HttpGet("categories")]
    [RequirePermission(Permissions.Contacts.BusinessImport)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<BusinessCategoryResponse>>), StatusCodes.Status200OK)]
    public IActionResult GetCategories() => Success(_discovery.GetCategories());

    /// <summary>Turns typed text into candidate map points.</summary>
    /// <param name="query">What the user typed. Fewer than three characters returns nothing.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Candidate points.</response>
    [HttpGet("places")]
    [RequirePermission(Permissions.Contacts.BusinessImport)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PlaceSuggestionResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchPlacesAsync(
        [FromQuery] string query,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _discovery.SearchPlacesAsync(query, cancellationToken));
    }

    /// <summary>Names a dropped pin.</summary>
    /// <remarks>
    /// Returns <c>null</c> when nothing sensible is nearby, which is a normal answer for open
    /// country rather than a failure - the search works from the coordinates either way.
    /// </remarks>
    /// <param name="latitude">Degrees north.</param>
    /// <param name="longitude">Degrees east.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The named point, or null.</response>
    [HttpGet("places/reverse")]
    [RequirePermission(Permissions.Contacts.BusinessImport)]
    [ProducesResponseType(typeof(ApiResponse<PlaceSuggestionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReverseGeocodeAsync(
        [FromQuery] double latitude,
        [FromQuery] double longitude,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _discovery.ReverseGeocodeAsync(latitude, longitude, cancellationToken));
    }

    /// <summary>Finds one page of businesses near a point.</summary>
    /// <remarks>
    /// A POST because the query is structured and because a billable search must not be replayed by
    /// a prefetching browser or cached by an intermediary.
    /// <para>
    /// The radius ceiling and page size are enforced here. The client's dropdown is a convenience
    /// for the user, not a constraint on the caller.
    /// </para>
    /// </remarks>
    /// <param name="request">Where, how far, and what kind.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">One page of businesses.</response>
    /// <response code="402">The platform's provider quota is exhausted.</response>
    /// <response code="422">A coordinate, radius or category was not acceptable.</response>
    /// <response code="429">The caller's own search allowance is exhausted.</response>
    [HttpPost("search")]
    [RequirePermission(Permissions.Contacts.BusinessImport)]
    [ProducesResponseType(typeof(ApiResponse<BusinessSearchResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SearchAsync(
        [FromBody] BusinessSearchRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _discovery.SearchAsync(request, cancellationToken));
    }

    /// <summary>Imports selected businesses from a previous search as contacts.</summary>
    /// <remarks>
    /// Takes identifiers, never records. Accepting business details from the client would make this
    /// an unvalidated contact-creation endpoint that bypasses the import rules entirely; the ids are
    /// resolved against the caller's own cached search.
    /// <para>
    /// Duplicates are skipped rather than merged or duplicated, matched on the normalised phone
    /// number - the same key the file importer uses, so both paths agree on what a duplicate is.
    /// </para>
    /// </remarks>
    /// <param name="request">Which businesses, from which search.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Counts and any failures.</response>
    /// <response code="409">The search has expired and must be run again.</response>
    [HttpPost("import")]
    [RequirePermission(Permissions.Contacts.BusinessImport)]
    [ProducesResponseType(typeof(ApiResponse<BusinessImportResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ImportAsync(
        [FromBody] BusinessImportRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var result = await _discovery.ImportAsync(request, cancellationToken);

        return Success(
            result,
            $"{result.Imported} imported, {result.Skipped} already present, {result.Failed} failed.");
    }
}
