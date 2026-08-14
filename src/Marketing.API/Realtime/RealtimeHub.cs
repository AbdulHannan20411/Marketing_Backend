using Marketing.Shared.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Marketing.API.Realtime;

/// <summary>
/// The platform's single SignalR hub.
/// <para>
/// Authenticated with the same bearer token as the REST API. Group membership is derived from the
/// validated token claims and never from anything the client sends - a client that could name its
/// own group would simply ask to join another tenant's, which is the obvious way to turn a
/// real-time feature into a data leak.
/// </para>
/// <para>
/// The hub is deliberately almost empty. It has no client-callable methods, because everything the
/// client needs to <em>do</em> already has a REST endpoint with permission checks on it; this is a
/// one-way push channel.
/// </para>
/// </summary>
[Authorize]
public sealed class RealtimeHub : Hub
{
    /// <summary>Route the hub is mapped at.</summary>
    public const string Route = "/hubs/realtime";

    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<RealtimeHub> _logger;

    /// <summary>Initialises a new instance.</summary>
    public RealtimeHub(ICurrentUser currentUser, ITenantContext tenantContext, ILogger<RealtimeHub> logger)
    {
        _currentUser = currentUser;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <summary>Group name carrying everything addressed to one tenant.</summary>
    public static string TenantGroup(long tenantId) => $"tenant:{tenantId:N}";

    /// <summary>Group name carrying everything addressed to one user.</summary>
    public static string UserGroup(long userId) => $"user:{userId:N}";

    /// <summary>
    /// Group name carrying everything addressed to platform staff.
    /// <para>
    /// Membership is decided here from the authenticated principal, never asked for by the client.
    /// A tenant able to join this group would see every other organisation's payment amounts.
    /// </para>
    /// </summary>
    public static string PlatformGroup() => "platform";

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        if (_currentUser.UserId is { } userId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        }

        // Platform staff have no tenant and therefore join no tenant group. They would otherwise
        // need to join all of them, which is both unbounded and the wrong default.
        if (_tenantContext.TenantId is { } tenantId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenantId));
        }

        // Decided from the role claim, so a customer cannot subscribe their way into the review
        // stream by asking.
        if (_currentUser.IsSuperAdmin)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, PlatformGroup());
        }

        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Groups are cleaned up by SignalR on disconnect, so there is nothing to undo here. The
        // log line exists because an unexpected disconnect is the first thing anyone looks for
        // when a client stops receiving updates.
        if (exception is not null)
        {
            _logger.LogWarning(
                exception,
                "Realtime connection {ConnectionId} dropped unexpectedly.",
                Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
