using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;

namespace Marketing.API.Realtime;

/// <summary>One push waiting to go out.</summary>
/// <param name="Groups">Hub groups to deliver to.</param>
/// <param name="Method">Client method name.</param>
/// <param name="Payload">What the client receives.</param>
public sealed record RealtimePush(IReadOnlyList<string> Groups, string Method, object Payload);

/// <summary>
/// Hands realtime pushes to a background sender instead of sending them inside the request.
/// </summary>
/// <remarks>
/// A push is a courtesy: the data is already committed and every screen fetches it anyway. Sending
/// it inline put the backplane's health inside the user's save button - an unreachable Redis turned
/// an eight-millisecond campaign save into an eight-second one. Queueing costs a channel write.
/// </remarks>
public interface IRealtimeDispatcher
{
    /// <summary>Queues a push. Returns false when the queue is full and the push was dropped.</summary>
    /// <param name="push">What to send.</param>
    public bool TryEnqueue(RealtimePush push);

    /// <summary>Reads queued pushes until the application stops.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public IAsyncEnumerable<RealtimePush> ReadAllAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRealtimeDispatcher" />
public sealed class RealtimeDispatcher : IRealtimeDispatcher
{
    /// <summary>
    /// Pushes held before the oldest is dropped.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. If the sender cannot keep up - a dead backplane, a flood of campaign
    /// progress - the right failure is to lose the stalest notifications, not the server's memory.
    /// The newest state is the one worth delivering, and a client that missed one sees it on its
    /// next fetch.
    /// </remarks>
    private const int Capacity = 2_000;

    private readonly Channel<RealtimePush> _channel = Channel.CreateBounded<RealtimePush>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    /// <inheritdoc />
    public bool TryEnqueue(RealtimePush push) => _channel.Writer.TryWrite(push);

    /// <inheritdoc />
    public IAsyncEnumerable<RealtimePush> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>Sends queued realtime pushes, outside the requests that produced them.</summary>
public sealed partial class RealtimeDispatchService : BackgroundService
{
    /// <summary>
    /// How long one push may take before it is abandoned.
    /// </summary>
    /// <remarks>
    /// Nothing waits on this any more, but a backplane that never answers would otherwise stall the
    /// queue behind it. A push nobody received is a refresh; a stuck queue is every client silent.
    /// </remarks>
    private static readonly TimeSpan PushBudget = TimeSpan.FromSeconds(5);

    private readonly IRealtimeDispatcher _queue;
    private readonly IHubContext<RealtimeHub> _hub;
    private readonly ILogger<RealtimeDispatchService> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="queue">Pushes waiting to go out.</param>
    /// <param name="hub">The hub they go to.</param>
    /// <param name="logger">Logger.</param>
    public RealtimeDispatchService(
        IRealtimeDispatcher queue,
        IHubContext<RealtimeHub> hub,
        ILogger<RealtimeDispatchService> logger)
    {
        _queue = queue;
        _hub = hub;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var push in _queue.ReadAllAsync(stoppingToken))
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            budget.CancelAfter(PushBudget);

            try
            {
                await _hub.Clients.Groups(push.Groups).SendAsync(push.Method, push.Payload, budget.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // One undeliverable push must not end the loop: everything queued behind it is for
                // other people, and they are still connected.
                LogPushFailed(exception, push.Method, push.Groups.Count);
            }
        }
    }

    [LoggerMessage(
        EventId = 4103,
        Level = LogLevel.Warning,
        Message = "Realtime push of {Method} to {GroupCount} group(s) failed; clients will see it on their next fetch.")]
    private partial void LogPushFailed(Exception exception, string method, int groupCount);
}
