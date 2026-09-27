using Marketing.Application.Contracts;
using Marketing.Application.Services.Exports;
using Marketing.Common.Exceptions;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Marketing.Infrastructure.Messaging.Consumers;

/// <summary>
/// Runs a queued export when the broker delivers one.
/// </summary>
/// <remarks>
/// Thin on purpose. Everything about running an export - claiming it, reading in a stream,
/// writing, storing, notifying - is <see cref="IExportRunner"/>, which knows nothing about
/// MassTransit and is therefore testable without a broker. This class exists to turn a delivery
/// into a call and to decide what a failure means to the queue.
/// <para>
/// <b>Acknowledgement.</b> MassTransit acknowledges when this returns and redelivers when it
/// throws. The receive endpoint is already configured with three retries five seconds apart and
/// dead-letters to <c>&lt;queue&gt;_error</c> afterwards, so nothing extra is needed here - what
/// matters is being careful about which failures are worth another attempt.
/// </para>
/// <para>
/// <b>Idempotency.</b> The runner claims through the job row's status, so a redelivery of an
/// export that already completed does nothing and acknowledges. That is the guard that stops a
/// retry writing a second file and sending a second "your export is ready".
/// </para>
/// </remarks>
public sealed partial class ExportJobConsumer : IConsumer<ExportJobQueued>
{
    private readonly IExportRunner _runner;
    private readonly ILogger<ExportJobConsumer> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="runner">Runs the export.</param>
    /// <param name="logger">Logger.</param>
    public ExportJobConsumer(IExportRunner runner, ILogger<ExportJobConsumer> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Consume(ConsumeContext<ExportJobQueued> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var message = context.Message;

        try
        {
            await _runner.RunAsync(message.ExportJobId, message.TenantId, context.CancellationToken);
        }
        catch (BusinessRuleException exception)
        {
            // A state the machine does not allow - almost always a redelivery arriving after the
            // job moved on. Swallowed and acknowledged rather than retried: the third attempt
            // will find exactly the same state as the first, and the only thing retrying adds is
            // a dead letter for something that is not wrong.
            LogAlreadySettled(message.ExportJobId, exception.Message);
        }
    }

    [LoggerMessage(
        EventId = 4230,
        Level = LogLevel.Debug,
        Message = "Export {ExportJobId} had already moved on when the message arrived: {Reason}")]
    private partial void LogAlreadySettled(long exportJobId, string reason);
}
