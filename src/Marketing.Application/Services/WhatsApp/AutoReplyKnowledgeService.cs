using System.Text.Json;
using Marketing.Application.DTOs.WhatsApp;
using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using static Marketing.Common.Constants.AppConstants;

namespace Marketing.Application.Services.WhatsApp;

/// <summary>The spreadsheet of facts automatic replies answer from.</summary>
public interface IAutoReplyKnowledgeService
{
    /// <summary>The workspace's entries and fallback. Never a 404: a workspace that never uploaded gets empty defaults.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<AutoReplyKnowledgeResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces every entry, and the fallback, in one transaction.</summary>
    /// <param name="request">The upload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<AutoReplyKnowledgeResponse> ReplaceAsync(
        AutoReplyKnowledgeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Removes every entry, keeping the fallback.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAutoReplyKnowledgeService" />
public sealed class AutoReplyKnowledgeService : IAutoReplyKnowledgeService
{
    private readonly IAutoReplyKnowledgeRepository _entries;
    private readonly IRepository<AutoReplySettings> _settings;
    private readonly IRepository<User> _users;
    private readonly IAuditLogRepository _audit;
    private readonly IQueryExecutor _queries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IRequestContext _requestContext;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    public AutoReplyKnowledgeService(
        IAutoReplyKnowledgeRepository entries,
        IRepository<AutoReplySettings> settings,
        IRepository<User> users,
        IAuditLogRepository audit,
        IQueryExecutor queries,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IRequestContext requestContext,
        ITenantContext tenantContext,
        IDateTimeProvider clock)
    {
        _entries = entries;
        _settings = settings;
        _users = users;
        _audit = audit;
        _queries = queries;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _requestContext = requestContext;
        _tenantContext = tenantContext;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<AutoReplyKnowledgeResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _queries.FirstOrDefaultAsync(_settings.Query(), cancellationToken);
        var entries = await _queries.ToListAsync(
            _entries.Query().OrderBy(entry => entry.SortOrder).ThenBy(entry => entry.Id),
            cancellationToken);

        return await ToResponseAsync(settings, entries, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AutoReplyKnowledgeResponse> ReplaceAsync(
        AutoReplyKnowledgeRequest request,
        CancellationToken cancellationToken = default)
    {
        var validated = AutoReplyKnowledgeRules.Validate(request);
        var tenantId = _tenantContext.RequireTenantId();

        // One transaction: a failure part-way must leave the old file in place, never half of each.
        var settings = await _unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                await _entries.DeleteAllForTenantAsync(tenantId, token);

                foreach (var entry in validated.Entries)
                {
                    entry.TenantId = tenantId;
                    _entries.Add(entry);
                }

                var row = await LoadOrCreateSettingsAsync(tenantId, token);

                row.KnowledgeFallback = validated.Fallback;
                row.KnowledgeFallbackMessage = validated.FallbackMessage;
                row.KnowledgeSourceFileName = validated.SourceFileName;
                Stamp(row);

                // Counts and the file name only. The content is the customer's business and the
                // audit trail is read by more people than the file is.
                Audit(row, "auto_reply.knowledge.replaced", new
                {
                    entries = validated.Entries.Count,
                    sourceFileName = validated.SourceFileName,
                });

                await _unitOfWork.SaveChangesAsync(token);

                return row;
            },
            cancellationToken);

        return await ToResponseAsync(settings, validated.Entries, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.RequireTenantId();

        await _unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                var removed = await _entries.DeleteAllForTenantAsync(tenantId, token);
                var row = await LoadOrCreateSettingsAsync(tenantId, token);

                // The fallback survives; only the file goes.
                row.KnowledgeSourceFileName = null;
                Stamp(row);

                Audit(row, "auto_reply.knowledge.cleared", new { entries = removed });

                await _unitOfWork.SaveChangesAsync(token);
            },
            cancellationToken);
    }

    private async Task<AutoReplySettings> LoadOrCreateSettingsAsync(long tenantId, CancellationToken cancellationToken)
    {
        var row = await _queries.FirstOrDefaultAsync(_settings.Query(asNoTracking: false), cancellationToken);

        if (row is null)
        {
            // Everything else stays at the switched-off defaults: uploading knowledge does not turn
            // automatic replies on.
            row = new AutoReplySettings { TenantId = tenantId };
            _settings.Add(row);
        }

        return row;
    }

    private void Stamp(AutoReplySettings row)
    {
        row.KnowledgeUpdatedAt = _clock.UtcNow;
        row.KnowledgeUpdatedByUserId = _currentUser.UserId;
    }

    private void Audit(AutoReplySettings row, string eventName, object detail) =>
        _audit.Add(new AuditLog
        {
            TenantId = row.TenantId,
            UserId = _currentUser.AuditUserId,
            EntityName = eventName,
            EntityId = row.TenantId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            Action = AuditAction.Updated,
            Changes = JsonSerializer.Serialize(detail),
            CorrelationId = _requestContext.CorrelationId,
            IpAddress = _requestContext.IpAddress,
            OccurredOn = _clock.UtcNow,
        });

    private async Task<AutoReplyKnowledgeResponse> ToResponseAsync(
        AutoReplySettings? settings,
        IReadOnlyList<AutoReplyKnowledgeEntry> entries,
        CancellationToken cancellationToken)
    {
        string? updatedBy = null;

        if (settings?.KnowledgeUpdatedByUserId is { } userId)
        {
            updatedBy = await _queries.FirstOrDefaultAsync(
                _users.Query().Where(user => user.Id == userId).Select(user => user.DisplayName),
                cancellationToken);
        }

        return new AutoReplyKnowledgeResponse(
            [.. entries.Select(entry => new KnowledgeEntryDto(
                entry.Kind,
                entry.Title,
                entry.Answer,
                entry.Price,
                entry.Available,
                entry.Keywords))],
            settings?.KnowledgeFallback ?? AutoReplyKnowledgeRules.Handoff,
            settings?.KnowledgeFallbackMessage ?? AutoReplyKnowledgeRules.DefaultFallbackMessage,
            settings?.KnowledgeSourceFileName,
            settings?.KnowledgeUpdatedAt,
            updatedBy);
    }
}
