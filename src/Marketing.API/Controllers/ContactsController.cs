using Asp.Versioning;
using Marketing.API.Filters;
using Marketing.Application.DTOs.Contacts;
using Marketing.Application.Interfaces;
using Marketing.Common.Constants;
using Marketing.Common.Requests;
using Marketing.Common.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Marketing.API.Controllers;

/// <summary>Contacts for the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/contacts")]
[Authorize]
[RequireModule(PlanModules.Crm)]
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
    /// <c>groupId</c> and <c>tagId</c> accept <c>all</c> or an identifier. Search matches name,
    /// phone number and email, case-insensitively. Every filter defaults to <c>all</c>, and the
    /// literal <c>all</c> the client sends when a filter is cleared means "no filter".
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

    /// <summary>Returns sets of contacts that look like the same person.</summary>
    /// <remarks>
    /// Declared before the <c>{id}</c> route so <c>/contacts/duplicates</c> is not swallowed by it.
    /// </remarks>
    /// <param name="query">Strategy and paging.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of duplicate sets.</response>
    [HttpGet("duplicates")]
    [RequirePermission(Permissions.Contacts.View)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<DuplicateGroupResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDuplicatesAsync(
        [FromQuery] DuplicateQuery query,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return SuccessPage(await _contacts.GetDuplicatesAsync(query, cancellationToken));
    }

    /// <summary>Returns one contact.</summary>
    /// <param name="id">Prefixed contact identifier.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">The contact.</response>
    /// <response code="404">No such contact, or it belongs to another tenant.</response>
    [HttpGet("{id}")]
    [RequirePermission(Permissions.Contacts.View)]
    [ProducesResponseType(typeof(ApiResponse<ContactResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIdAsync(
        string id,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return Success(await _contacts.GetContactAsync(id, cancellationToken));
    }
}

/// <summary>Contact groups for the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/groups")]
[Authorize]
[RequireModule(PlanModules.Crm)]
public sealed class GroupsController : ApiControllerBase
{
    private readonly IContactService _contacts;
    private readonly IContactWriteService _writes;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public GroupsController(
        IContactService contacts,
        IContactWriteService writes,
        ITenantScopeResolver scope)
    {
        _contacts = contacts;
        _writes = writes;
        _scope = scope;
    }

    /// <summary>Returns every group with a live member count.</summary>
    /// <remarks>
    /// Unpaged: the screen is a card grid. The count each group reports is the same number
    /// <c>GET /contacts?groupId=</c> returns as <c>totalItems</c>, because both are computed from
    /// the same non-deleted membership rows.
    /// </remarks>
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

    /// <summary>Returns a page of a group's members.</summary>
    /// <param name="id">Prefixed group identifier.</param>
    /// <param name="request">Paging and search.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of members.</response>
    [HttpGet("{id}/contacts")]
    [RequirePermission(Permissions.Contacts.GroupsManage)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ContactResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMembersAsync(
        string id,
        [FromQuery] PageRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return SuccessPage(await _contacts.GetGroupMembersAsync(id, request, cancellationToken));
    }

    /// <summary>Adds contacts to a group.</summary>
    /// <param name="id">Prefixed group identifier.</param>
    /// <param name="request">Contacts to add.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">How many were added, and which could not be.</response>
    [HttpPost("{id}/contacts")]
    [RequirePermission(Permissions.Contacts.GroupsManage)]
    [ProducesResponseType(typeof(ApiResponse<BulkOperationResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> AddMembersAsync(
        string id,
        [FromBody] MembershipRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var result = await _writes.SetGroupMembershipAsync(id, request, BulkMode.Add, cancellationToken);

        return Success(result, $"{result.Succeeded} contacts added to the group.");
    }

    /// <summary>Removes contacts from a group. The contacts themselves are untouched.</summary>
    /// <param name="id">Prefixed group identifier.</param>
    /// <param name="request">Contacts to remove.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">How many were removed.</response>
    [HttpDelete("{id}/contacts")]
    [RequirePermission(Permissions.Contacts.GroupsManage)]
    [ProducesResponseType(typeof(ApiResponse<BulkOperationResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveMembersAsync(
        string id,
        [FromBody] MembershipRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var result = await _writes.SetGroupMembershipAsync(id, request, BulkMode.Remove, cancellationToken);

        return Success(result, $"{result.Succeeded} contacts removed from the group.");
    }
}

/// <summary>Tags for the resolved tenant.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tags")]
[Authorize]
[RequireModule(PlanModules.Crm)]
public sealed class TagsController : ApiControllerBase
{
    private readonly IContactService _contacts;
    private readonly IContactWriteService _writes;
    private readonly ITenantScopeResolver _scope;

    /// <summary>Initialises a new instance.</summary>
    public TagsController(
        IContactService contacts,
        IContactWriteService writes,
        ITenantScopeResolver scope)
    {
        _contacts = contacts;
        _writes = writes;
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

    /// <summary>Returns a page of the contacts carrying a tag.</summary>
    /// <param name="id">Prefixed tag identifier.</param>
    /// <param name="request">Paging and search.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">A page of tagged contacts.</response>
    [HttpGet("{id}/contacts")]
    [RequirePermission(Permissions.Contacts.TagsManage)]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ContactResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTaggedAsync(
        string id,
        [FromQuery] PageRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        return SuccessPage(await _contacts.GetTagMembersAsync(id, request, cancellationToken));
    }

    /// <summary>Applies a tag to contacts.</summary>
    /// <param name="id">Prefixed tag identifier.</param>
    /// <param name="request">Contacts to tag.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">How many were tagged.</response>
    [HttpPost("{id}/contacts")]
    [RequirePermission(Permissions.Contacts.TagsManage)]
    [ProducesResponseType(typeof(ApiResponse<BulkOperationResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> TagAsync(
        string id,
        [FromBody] MembershipRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var result = await _writes.SetTagMembershipAsync(id, request, BulkMode.Add, cancellationToken);

        return Success(result, $"Tag applied to {result.Succeeded} contacts.");
    }

    /// <summary>Removes a tag from contacts. The contacts themselves are untouched.</summary>
    /// <param name="id">Prefixed tag identifier.</param>
    /// <param name="request">Contacts to untag.</param>
    /// <param name="adminId">Super Admin scoping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">How many were untagged.</response>
    [HttpDelete("{id}/contacts")]
    [RequirePermission(Permissions.Contacts.TagsManage)]
    [ProducesResponseType(typeof(ApiResponse<BulkOperationResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UntagAsync(
        string id,
        [FromBody] MembershipRequest request,
        [FromQuery] string? adminId,
        CancellationToken cancellationToken)
    {
        using var scope = await _scope.EnterAsync(adminId, cancellationToken);

        var result = await _writes.SetTagMembershipAsync(id, request, BulkMode.Remove, cancellationToken);

        return Success(result, $"Tag removed from {result.Succeeded} contacts.");
    }
}
