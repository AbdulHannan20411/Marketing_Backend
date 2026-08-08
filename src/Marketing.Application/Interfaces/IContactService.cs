using Marketing.Application.DTOs.Contacts;
using Marketing.Common.Requests;
using Marketing.Common.Responses;

namespace Marketing.Application.Interfaces;

/// <summary>Reads contacts, groups and tags for the resolved tenant.</summary>
public interface IContactService
{
    /// <summary>Returns a filtered, searched page of contacts.</summary>
    /// <param name="query">Paging, search and filter parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<ContactResponse>> GetContactsAsync(
        ContactQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one contact.</summary>
    /// <param name="contactId">Prefixed contact identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Common.Exceptions.NotFoundException">
    /// No such contact, or it belongs to another tenant. The two are indistinguishable by design.
    /// </exception>
    public Task<ContactResponse> GetContactAsync(
        string contactId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a page of a group's members.</summary>
    /// <param name="groupId">Prefixed group identifier.</param>
    /// <param name="request">Paging and search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<ContactResponse>> GetGroupMembersAsync(
        string groupId,
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a page of the contacts carrying a tag.</summary>
    /// <param name="tagId">Prefixed tag identifier.</param>
    /// <param name="request">Paging and search.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<ContactResponse>> GetTagMembersAsync(
        string tagId,
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns sets of contacts that appear to be the same person.</summary>
    /// <param name="query">Strategy and paging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<DuplicateGroupResponse>> GetDuplicatesAsync(
        DuplicateQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Returns every group with a live member count.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<ContactGroupResponse>> GetGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns every tag with a live contact count.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<ContactTagResponse>> GetTagsAsync(CancellationToken cancellationToken = default);
}
