using Marketing.Application.DTOs.Contacts;
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

    /// <summary>Returns every group with a live member count.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<ContactGroupResponse>> GetGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns every tag with a live contact count.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<ContactTagResponse>> GetTagsAsync(CancellationToken cancellationToken = default);
}
