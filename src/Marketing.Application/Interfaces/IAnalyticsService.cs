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
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<CampaignResponse>> GetCampaignsAsync(CancellationToken cancellationToken = default);

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
