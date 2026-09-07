using Marketing.Business.Repositories.Interfaces;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;

namespace Marketing.Application.Services.Email;

/// <summary>
/// Queues email instead of sending it.
/// </summary>
/// <remarks>
/// The implementation of <see cref="IEmailSender"/> the whole application uses. Nothing that
/// creates an account, invites an employee or resets a password waits on a mail server any more:
/// the message becomes a row, and the outbox poller delivers it.
/// <para>
/// Written through the caller's own unit of work, so it commits with whatever caused it. An
/// invitation cannot exist for an account that was rolled back, and an account cannot be created
/// without its invitation being queued - which was the failure mode of simply moving the send
/// outside the transaction.
/// </para>
/// </remarks>
public sealed class OutboxEmailSender : IEmailSender
{
    private readonly IRepository<OutboxEmail> _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDateTimeProvider _clock;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="outbox">Outbox repository.</param>
    /// <param name="unitOfWork">Unit of work.</param>
    /// <param name="clock">Clock.</param>
    public OutboxEmailSender(
        IRepository<OutboxEmail> outbox,
        IUnitOfWork unitOfWork,
        IDateTimeProvider clock)
    {
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reports the queue, not the eventual transport. Claiming "SMTP" here would put a delivery
    /// that has not happened yet into the audit trail.
    /// </remarks>
    public string TransportName => "Outbox";

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        _outbox.Add(new OutboxEmail
        {
            ToAddress = message.ToAddress,
            ToName = message.ToName,
            Subject = message.Subject,
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
            ReplyToAddress = message.ReplyToAddress,
            ReplyToName = message.ReplyToName,

            // Due immediately. The poller runs seconds behind, which is indistinguishable from
            // instant for a person waiting on an invitation email.
            NextAttemptOn = _clock.UtcNow,
        });

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
