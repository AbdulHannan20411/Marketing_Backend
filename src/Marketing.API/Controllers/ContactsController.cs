using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Contacts;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>Contacts for the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/contacts")]
[Authorize]
public sealed class ContactsController : ApiControllerBase
{
    private readonly IContactService _contacts;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public ContactsController(IContactService contacts, ITenantScopeResolver scope)
    {
        _contacts = contacts;
        _scope = scope;
    }

    /// <summary>Returns a filtered, searched page of contacts.</summary>
    /// <remarks>
    /// <c>status</c> accepts <c>all</c>, <c>subscribed</c>, <c>unsubscribed</c> or <c>blocked</c>;
    /// <c>groupId</c> accepts <c>all</c> or a group identifier. Search matches name, phone number
    /// and email, case-insensitively. Both filters default to <c>all</c>.
    /// </remarks>
    /// <param name="query">Paging, search and filter parameters.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of contacts.</response>
    [HttpGet]
    [RequirePermission(Permissions.Contacts.View)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ContactResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] ContactQuery query,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return SuccessPage(await _contacts.GetContactsAsync(query, cancellationToken));
    }
}

/// <summary>Contact groups for the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/groups")]
[Authorize]
public sealed class GroupsController : ApiControllerBase
{
    private readonly IContactService _contacts;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public GroupsController(IContactService contacts, ITenantScopeResolver scope)
    {
        _contacts = contacts;
        _scope = scope;
    }

    /// <summary>Returns every group with a live member count.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The groups.</response>
    [HttpGet]
    [RequirePermission(Permissions.Contacts.GroupsManage)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ContactGroupResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _contacts.GetGroupsAsync(cancellationToken));
    }
}

/// <summary>Tags for the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tags")]
[Authorize]
public sealed class TagsController : ApiControllerBase
{
    private readonly IContactService _contacts;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public TagsController(IContactService contacts, ITenantScopeResolver scope)
    {
        _contacts = contacts;
        _scope = scope;
    }

    /// <summary>Returns every tag with a live contact count.</summary>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The tags.</response>
    [HttpGet]
    [RequirePermission(Permissions.Contacts.TagsManage)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ContactTagResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _contacts.GetTagsAsync(cancellationToken));
    }
}
