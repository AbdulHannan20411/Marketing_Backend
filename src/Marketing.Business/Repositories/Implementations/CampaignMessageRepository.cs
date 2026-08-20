using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Context;
using Marketing.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;
using static Marketing.Common.Constants.AppConstants;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Business.Repositories.Implementations;

/// <inheritdoc cref="ICampaignMessageRepository" />
public sealed class CampaignMessageRepository : Repository<CampaignMessage>, ICampaignMessageRepository
{
    /// <summary>Initialises a new instance.</summary>
    /// <param name="context">Database context.</param>
    public CampaignMessageRepository(ApplicationDbContext context)
        : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DueCampaign>> FindDueCampaignsAsync(
        DateTimeOffset utcNow,
        int maximum,
        CancellationToken cancellationToken = default) =>
        await Context.Set<Campaign>()
            .AsNoTracking()
            // The poller runs with no principal, so the tenant filter would match nothing. The
            // projection below returns two identifiers and no tenant-owned data; the caller enters
            // each tenant properly before reading anything else.
            .IgnoreQueryFilters()
            .Where(campaign =>
                !campaign.IsDeleted
                && campaign.TenantId != null
                && (campaign.Status == CampaignStatus.Sending
                    || (campaign.Status == CampaignStatus.Scheduled
                        && campaign.ScheduledAt != null
                        && campaign.ScheduledAt <= utcNow)
                    // A recurring campaign is due on its computed occurrence rather than on a
                    // fixed instant, and it returns here after every firing rather than once.
                    || (campaign.Status == CampaignStatus.Scheduled
                        && campaign.NextRunAtUtc != null
                        && campaign.NextRunAtUtc <= utcNow)
                    || (campaign.Status == CampaignStatus.Paused
                        && campaign.ResumeAfter != null
                        && campaign.ResumeAfter <= utcNow)))
            // Oldest due time first, whichever kind of schedule supplied it, so a campaign that
            // has been waiting is not starved by one scheduled a moment ago.
            .OrderBy(campaign => campaign.NextRunAtUtc ?? campaign.ScheduledAt)
            .Take(maximum)
            .Select(campaign => new DueCampaign(campaign.Id, campaign.TenantId!.Value))
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CampaignMessage>> ClaimPendingAsync(
        long campaignId,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        await Set
            .Where(message => message.CampaignId == campaignId && message.Status == CampaignMessageStatus.Pending)
            .OrderBy(message => message.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int> CountPendingAsync(long campaignId, CancellationToken cancellationToken = default) =>
        Set
            .AsNoTracking()
            .CountAsync(
                message => message.CampaignId == campaignId && message.Status == CampaignMessageStatus.Pending,
                cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetQueuedContactIdsAsync(
        long campaignId,
        CancellationToken cancellationToken = default) =>
        await Set
            .AsNoTracking()
            .Where(message => message.CampaignId == campaignId)
            .Select(message => message.ContactId)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DateTimeOffset>> GetSendTimesSinceAsync(
        long tenantId,
        DateTimeOffset since,
        CancellationToken cancellationToken = default) =>
        await Set
            .AsNoTracking()
            // Explicit tenant predicate rather than the ambient filter: the caller is a job that
            // has already entered the tenant, and stating it here means this method is correct
            // whether or not it does.
            .IgnoreQueryFilters()
            .Where(message =>
                !message.IsDeleted
                && message.TenantId == tenantId
                && message.SentOn != null
                && message.SentOn >= since)
            .OrderBy(message => message.SentOn)
            .Select(message => message.SentOn!.Value)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<CampaignMessage?> FindByMetaMessageIdAsync(
        string metaMessageId,
        CancellationToken cancellationToken = default) =>
        Set
            .IgnoreQueryFilters()
            .Where(message => !message.IsDeleted && message.MetaMessageId == metaMessageId)
            .FirstOrDefaultAsync(cancellationToken);
}
