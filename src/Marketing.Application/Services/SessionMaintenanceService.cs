using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Application.Services;

/// <summary>Housekeeping over authentication sessions.</summary>
public sealed partial class SessionMaintenanceService : ISessionMaintenanceService
{
    /// <summary>
    /// How long an expired refresh token is retained before deletion.
    /// <para>
    /// Long enough for a security investigation to reconstruct recent session history, short
    /// enough that spent credential material does not accumulate indefinitely.
    /// </para>
    /// </summary>
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);

    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<SessionMaintenanceService> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="refreshTokens">Refresh-token repository.</param>
    /// <param name="dateTimeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public SessionMaintenanceService(
        IRefreshTokenRepository refreshTokens,
        IDateTimeProvider dateTimeProvider,
        ILogger<SessionMaintenanceService> logger)
    {
        _refreshTokens = refreshTokens;
        _dateTimeProvider = dateTimeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> PurgeExpiredSessionsAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = _dateTimeProvider.UtcNow - RetentionWindow;

        var removed = await _refreshTokens.PurgeExpiredAsync(cutoff, cancellationToken);

        if (removed > 0)
        {
            LogSessionsPurged(removed, cutoff);
        }

        return removed;
    }

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Information,
        Message = "Purged {RemovedCount} refresh tokens that expired before {Cutoff}.")]
    private partial void LogSessionsPurged(int removedCount, DateTimeOffset cutoff);
}
