using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Marketing.DataAccess.Interceptors;

/// <summary>
/// Logs commands that exceed a duration threshold.
/// <para>
/// Reported with the command text but never with parameter values: the parameters of a query
/// against contacts or message bodies are customer PII, and a performance log is not an
/// appropriate place for it.
/// </para>
/// </summary>
public sealed class SlowQueryLoggingInterceptor : DbCommandInterceptor
{
    private readonly ILogger<SlowQueryLoggingInterceptor> _logger;
    private readonly TimeSpan _threshold;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="threshold">Duration above which a command is reported. Defaults to 500 ms.</param>
    public SlowQueryLoggingInterceptor(ILogger<SlowQueryLoggingInterceptor> logger, TimeSpan? threshold = null)
    {
        _logger = logger;
        _threshold = threshold ?? TimeSpan.FromMilliseconds(500);
    }

    /// <inheritdoc />
    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Report(eventData);
        return base.ReaderExecuted(command, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Report(eventData);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Report(eventData);
        return base.NonQueryExecuted(command, eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Report(eventData);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    private void Report(CommandExecutedEventData eventData)
    {
        if (eventData.Duration < _threshold)
        {
            return;
        }

        _logger.LogWarning(
            "Slow database command: {ElapsedMilliseconds} ms exceeded the {ThresholdMilliseconds} ms threshold. Command: {CommandText}",
            (long)eventData.Duration.TotalMilliseconds,
            (long)_threshold.TotalMilliseconds,
            eventData.Command.CommandText);
    }
}
