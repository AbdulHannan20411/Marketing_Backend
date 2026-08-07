using Marketing.DataAccess.Entities;

namespace Marketing.Business.Repositories.Interfaces;

/// <summary>
/// The per-recipient message log a campaign dispatch is driven from.
/// <para>
/// Its own repository rather than the generic one because two of its three access paths cannot be
/// expressed through the tenant-filtered query: the cross-tenant poll that finds work, and the
/// webhook lookup that arrives with no principal at all.
/// </para>
/// </summary>
public interface ICampaignMessageRepository : IRepository<CampaignMessage>
{
    /// <summary>
    /// Campaigns that are due to start, resume or continue, across every tenant.
    /// </summary>
    /// <remarks>
    /// Returns identifiers only. The caller enters each tenant's scope and re-reads the campaign
    /// through the filtered query, so nothing outside this one projection ever sees a row it is
    /// not entitled to.
    /// </remarks>
    /// <param name="utcNow">Current instant.</param>
    /// <param name="maximum">Ceiling on campaigns returned in one poll.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<DueCampaign>> FindDueCampaignsAsync(
        DateTimeOffset utcNow,
        int maximum,
        CancellationToken cancellationToken = default);

    /// <summary>Claims the next batch of unsent messages for a campaign, oldest first.</summary>
    /// <param name="campaignId">Campaign to draw from.</param>
    /// <param name="batchSize">Most messages to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<CampaignMessage>> ClaimPendingAsync(
        Guid campaignId,
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>Counts messages still waiting to be sent for a campaign.</summary>
    /// <param name="campaignId">Campaign to count.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<int> CountPendingAsync(Guid campaignId, CancellationToken cancellationToken = default);

    /// <summary>Contacts that already have a message row for a campaign.</summary>
    /// <param name="campaignId">Campaign to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<Guid>> GetQueuedContactIdsAsync(
        Guid campaignId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Messages sent by a tenant inside the rolling window, oldest first.
    /// <para>
    /// This is the real rolling-window count, derived from the log rather than from a stored
    /// counter, because a counter that drifts here means either sending past Meta's ceiling or
    /// stalling a campaign that had allowance left.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Tenant to measure.</param>
    /// <param name="since">Start of the window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<DateTimeOffset>> GetSendTimesSinceAsync(
        Guid tenantId,
        DateTimeOffset since,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the message a delivery receipt refers to.
    /// <para>
    /// Reads across tenants deliberately: a webhook carries Meta's message id and nothing else, and
    /// the row it matches is what establishes which tenant the receipt belongs to.
    /// </para>
    /// </summary>
    /// <param name="metaMessageId">Meta's message identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<CampaignMessage?> FindByMetaMessageIdAsync(
        string metaMessageId,
        CancellationToken cancellationToken = default);
}

/// <summary>A campaign the dispatcher has work to do on.</summary>
/// <param name="CampaignId">Campaign identifier.</param>
/// <param name="TenantId">Tenant that owns it.</param>
public sealed record DueCampaign(Guid CampaignId, Guid TenantId);
