using System.Diagnostics;
using AwesomeAssertions;
using Marketing.API.Realtime;
using Marketing.Application.DTOs.Workspace;
using Marketing.Application.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.IntegrationTests.Routing;

/// <summary>
/// A realtime push must never hold up the request that caused it.
/// </summary>
/// <remarks>
/// Written after saving a campaign was measured at 8 to 49 seconds. The save itself was quick; the
/// push that followed it sat waiting on an unreachable Redis backplane, inside the request. Pushes
/// are queued now and sent by a background service, so the hub cannot be in a caller's way at all.
/// </remarks>
public sealed class RealtimePushBudgetTests
{
    private static AppNotification Notification() =>
        new(
            "ntf_1",
            NotificationKind.CampaignCompleted,
            "Campaign finished",
            "All messages sent.",
            NotificationPriority.Info,
            "megaphone",
            Read: false,
            ActionLabel: null,
            ActionRoute: null,
            DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Notifying_returns_at_once_and_leaves_the_push_for_the_sender()
    {
        var dispatcher = new RealtimeDispatcher();
        var notifier = new SignalRRealtimeNotifier(dispatcher, NullLogger<SignalRRealtimeNotifier>.Instance);

        var watch = Stopwatch.StartNew();

        await notifier.NotifyTenantAsync(3, Notification(), TestContext.Current.CancellationToken);
        await notifier.PublishCampaignProgressAsync(3, null!, TestContext.Current.CancellationToken);

        watch.Stop();

        // No hub, no backplane, no network: the caller writes to a channel and carries on.
        watch.ElapsedMilliseconds.Should().BeLessThan(100);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var queued = new List<RealtimePush>();

        await foreach (var push in dispatcher.ReadAllAsync(stop.Token))
        {
            queued.Add(push);

            if (queued.Count == 1)
            {
                break;
            }
        }

        // The null payload is dropped by the notifier rather than queued as a push of nothing.
        queued.Should().ContainSingle().Which.Method.Should().Be(RealtimeEvents.NotificationReceived);
    }

    [Fact]
    public async Task A_push_the_hub_refuses_does_not_stop_the_ones_behind_it()
    {
        var dispatcher = new RealtimeDispatcher();
        var hub = new FailingHubContext(failFirst: true);
        var service = new RealtimeDispatchService(dispatcher, hub, NullLogger<RealtimeDispatchService>.Instance);

        dispatcher.TryEnqueue(new RealtimePush(["tenant-3"], RealtimeEvents.NotificationReceived, Notification()));
        dispatcher.TryEnqueue(new RealtimePush(["tenant-3"], RealtimeEvents.CampaignProgress, Notification()));

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StartAsync(stop.Token);

        while (hub.Sent < 2 && !stop.IsCancellationRequested)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await service.StopAsync(CancellationToken.None);

        hub.Sent.Should().Be(2, "the second push goes out even though the first was refused");
    }

    /// <summary>A hub that refuses the first send and accepts the rest.</summary>
    private sealed class FailingHubContext : IHubContext<RealtimeHub>
    {
        private readonly FailingClients _clients;

        public FailingHubContext(bool failFirst) => _clients = new FailingClients(failFirst);

        public int Sent => _clients.Sent;

        public IHubClients Clients => _clients;

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class FailingClients : IHubClients
    {
        private readonly FailingProxy _proxy;

        public FailingClients(bool failFirst) => _proxy = new FailingProxy(failFirst);

        public int Sent => _proxy.Sent;

        public IClientProxy All => _proxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;

        public IClientProxy Client(string connectionId) => _proxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;

        public IClientProxy Group(string groupName) => _proxy;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;

        public IClientProxy User(string userId) => _proxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class FailingProxy : IClientProxy
    {
        private readonly bool _failFirst;
        private int _sent;

        public FailingProxy(bool failFirst) => _failFirst = failFirst;

        public int Sent => _sent;

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _sent);

            return _failFirst && attempt == 1
                ? Task.FromException(new InvalidOperationException("No backplane."))
                : Task.CompletedTask;
        }
    }
}
