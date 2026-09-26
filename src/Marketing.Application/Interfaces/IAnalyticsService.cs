using Marketing.Application.DTOs.Campaigns;
using Marketing.Application.DTOs.Dashboard;
using Marketing.Common.Requests;
using Marketing.Common.Responses;

namespace Marketing.Application.Interfaces;

/// <summary>Dashboard and reporting reads for the resolved tenant.</summary>
public interface IAnalyticsService
{
    /// <summary>
    /// Returns the last 30 days of activity.
    /// <para>
    /// Served from pre-aggregated daily counters, not by scanning the message log - the screen is
    /// on the critical path with a sub-300 ms budget.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams every delivery failure, in the order the caller asked for, for export.
    /// </summary>
    /// <remarks>
    /// Unpaged and streamed. An export that returns a page is not an export, and materialising a
    /// workspace's whole failure history to write it straight back out would size the request by
    /// how badly a campaign went rather than by anything the caller chose.
    /// <para>
    /// <paramref name="request"/> is taken for its sort alone; its page and page size are ignored
    /// on purpose. The sort goes through the same allow-list and the same default as the paged
    /// read, so the file and the screen that offered it cannot disagree about the order - which
    /// they did, and the file is the artefact that leaves the product.
    /// </para>
    /// </remarks>
    /// <param name="request">Sort to apply. Paging on it is ignored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public IAsyncEnumerable<DeliveryFailureResponse> StreamFailuresAsync(
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a page of delivery failures, newest first.</summary>
    /// <param name="request">Paging parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<DeliveryFailureResponse>> GetFailuresAsync(
        PageRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Campaign reads for the resolved tenant.</summary>
public interface ICampaignService
{
    /// <summary>Returns every campaign, newest first.</summary>
    /// <param name="accountId">Only campaigns sent from this number; all when null or <c>all</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<CampaignResponse>> GetCampaignsAsync(
        string? accountId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one campaign.</summary>
    /// <param name="campaignId">Opaque campaign identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<CampaignResponse> GetCampaignAsync(
        string campaignId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a campaign's firings, newest first.</summary>
    /// <param name="campaignId">Opaque campaign identifier.</param>
    /// <param name="request">Paging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PagedResult<CampaignRunResponse>> GetRunsAsync(
        string campaignId,
        PageRequest request,
        CancellationToken cancellationToken = default);
}
