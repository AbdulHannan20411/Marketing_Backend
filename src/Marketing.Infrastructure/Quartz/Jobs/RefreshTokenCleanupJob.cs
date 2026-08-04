using Marketing.Business.Repositories.Interfaces;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Marketing.Infrastructure.Quartz.Jobs;

/// <summary>
/// Removes refresh tokens that expired more than the retention window ago.
/// <para>
/// Expired tokens are spent credential material with no business value. Left in place they grow
/// the table without bound and keep the hot index for the refresh path - a lookup by token hash -
/// larger than it needs to be. A short retention window is kept so a security investigation can
/// still see recent session history.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public sealed class RefreshTokenCleanupJob : IJob
{
    /// <summary>Identity under which the job is scheduled.</summary>
    public static readonly JobKey Key = new("refresh-token-cleanup", "maintenance");

    /// <summary>How long an expired token is retained before deletion.</summary>
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);

    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<RefreshTokenCleanupJob> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="refreshTokenRepository">Refresh-token repository.</param>
    /// <param name="dateTimeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    public RefreshTokenCleanupJob(
        IRefreshTokenRepository refreshTokenRepository,
        IDateTimeProvider dateTimeProvider,
        ILogger<RefreshTokenCleanupJob> logger)
    {
        _refreshTokenRepository = refreshTokenRepository;
        _dateTimeProvider = dateTimeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cutoff = _dateTimeProvider.UtcNow - RetentionWindow;

        try
        {
            var deleted = await _refreshTokenRepository.PurgeExpiredAsync(cutoff, context.CancellationToken);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Purged {DeletedCount} refresh tokens that expired before {Cutoff}.",
                    deleted,
                    cutoff);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Scheduler shutdown. Nothing was committed; the next run picks the work back up.
            throw;
        }
        catch (Exception exception)
        {
            // Wrapping in JobExecutionException lets Quartz apply its own misfire and retry policy
            // rather than the job silently disappearing from the schedule.
            _logger.LogError(exception, "Refresh token cleanup failed.");
            throw new JobExecutionException(exception, refireImmediately: false);
        }
    }
}
