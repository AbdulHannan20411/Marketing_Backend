using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.Email;

/// <summary>Delivers whatever email is waiting in the outbox.</summary>
public interface IEmailOutboxProcessor
{
    /// <summary>
    /// Sends every message that is due, and returns how many were delivered.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<int> SendDueAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IEmailOutboxProcessor" />
public sealed partial class EmailOutboxProcessor : IEmailOutboxProcessor
{
    /// <summary>
    /// Messages delivered per poll.
    /// <para>
    /// Bounded because each one is a network round trip measured in seconds. A large batch would
    /// hold the scheduler for minutes and delay every other job behind it.
    /// </para>
    /// </summary>
    private const int BatchSize = 20;

    private readonly IRepository<OutboxEmail> _outbox;
    private readonly IEmailDispatcher _dispatcher;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<EmailOutboxProcessor> _logger;

    /// <summary>Initialises a new instance.</summary>
    public EmailOutboxProcessor(
        IRepository<OutboxEmail> outbox,
        IEmailDispatcher dispatcher,
        IUnitOfWork unitOfWork,
        IDateTimeProvider clock,
        ILogger<EmailOutboxProcessor> logger)
    {
        _outbox = outbox;
        _dispatcher = dispatcher;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int> SendDueAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        var due = await _outbox
            .Query(asNoTracking: false)
            // Bypasses the tenant filter deliberately: these rows have no tenant, and the poller
            // has no signed-in user for one to be derived from.
            .IgnoreQueryFilters()
            .Where(email => !email.IsDeleted
                            && email.Status == OutboxEmailStatus.Pending
                            && email.NextAttemptOn <= now)
            // Oldest first, so a message that has been waiting is not starved by newer ones.
            .OrderBy(email => email.NextAttemptOn)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var sent = 0;

        foreach (var email in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TrySendAsync(email, cancellationToken))
            {
                sent++;
            }
        }

        // Saved once for the batch rather than per message. Every row was already claimed by this
        // poll, and twenty round trips to the database on top of twenty to the relay is waste.
        if (due.Count > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return sent;
    }

    private async Task<bool> TrySendAsync(OutboxEmail email, CancellationToken cancellationToken)
    {
        email.AttemptCount++;

        try
        {
            await _dispatcher.SendAsync(
                new EmailMessage(email.ToAddress, email.ToName, email.Subject, email.HtmlBody, email.TextBody)
                {
                    ReplyToAddress = email.ReplyToAddress,
                    ReplyToName = email.ReplyToName,
                },
                cancellationToken);

            email.Status = OutboxEmailStatus.Sent;
            email.SentOn = _clock.UtcNow;
            email.LastError = null;

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            email.LastError = exception.Message.Length <= 500
                ? exception.Message
                : exception.Message[..500];

            if (OutboxRetryPolicy.ShouldGiveUp(email.AttemptCount))
            {
                // Written off rather than retried forever. A permanently bad address would
                // otherwise be attempted on every poll for the life of the platform.
                email.Status = OutboxEmailStatus.Failed;

                LogGivenUp(exception, email.ToAddress, email.AttemptCount);

                return false;
            }

            var delay = OutboxRetryPolicy.DelayFor(email.AttemptCount);

            email.NextAttemptOn = _clock.UtcNow.Add(delay);

            LogRetrying(exception, email.ToAddress, email.AttemptCount, delay);

            return false;
        }
    }

    [LoggerMessage(
        EventId = 2701,
        Level = LogLevel.Warning,
        Message = "Delivery to {ToAddress} failed on attempt {AttemptCount}; retrying in {Delay}.")]
    private partial void LogRetrying(Exception exception, string toAddress, int attemptCount, TimeSpan delay);

    [LoggerMessage(
        EventId = 2702,
        Level = LogLevel.Error,
        Message = "Giving up on delivery to {ToAddress} after {AttemptCount} attempts.")]
    private partial void LogGivenUp(Exception exception, string toAddress, int attemptCount);
}
