using System.Diagnostics;
using AwesomeAssertions;
using Marketing.API.Realtime;
using Marketing.Application.DTOs.Workspace;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.IntegrationTests.Routing;

/// <summary>
/// A realtime push must never hold up the request that caused it.
/// </summary>
/// <remarks>
/// Written after saving a campaign was measured at 8 to 49 seconds. The save itself was quick; the
/// push that followed it sat waiting on an unreachable Redis backplane, inside the request. The
/// backplane's connection settings are fixed separately - this is the backstop, and it holds
/// whatever the backplane is doing.
/// </remarks>
public sealed class RealtimePushBudgetTests
{
    [Fact]
    public async Task A_push_that_hangs_is_abandoned_rather_than_waited_on()
    {
        var hub = new StuckHubContext();
        var notifier = new SignalRRealtimeNotifier(hub, NullLogger<SignalRRealtimeNotifier>.Instance);

        var notification = new AppNotification(
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

        var watch = Stopwatch.StartNew();

        await notifier.NotifyTenantAsync(3, notification, TestContext.Current.CancellationToken);

        watch.Stop();

        // The budget is well under a second; this leaves room for a slow machine.
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        hub.Attempted.Should().BeTrue();
    }

    /// <summary>A hub whose sends never complete, as an unreachable backplane behaves.</summary>
    private sealed class StuckHubContext : IHubContext<RealtimeHub>
    {
        private readonly StuckClients _clients = new();

        public bool Attempted => _clients.Attempted;

        public IHubClients Clients => _clients;

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class StuckClients : IHubClients
    {
        private readonly StuckProxy _proxy = new();

        public bool Attempted => _proxy.Attempted;

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

    private sealed class StuckProxy : IClientProxy
    {
        public bool Attempted { get; private set; }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Attempted = true;

            return Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }
}
