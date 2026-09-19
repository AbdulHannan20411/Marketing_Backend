using Marketing.Application.DTOs.WhatsApp;
using Marketing.Application.Interfaces;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services.WhatsApp;

/// <summary>A workspace's WhatsApp numbers: listing, naming, the default, and their lifecycle.</summary>
public interface IWhatsAppAccountService
{
    /// <summary>The numbers the caller may view, with the plan's allowance.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppAccountListResponse> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One number the caller may view.</summary>
    /// <param name="accountId">Public account id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppAccountResponse> GetAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Renames a number.</summary>
    /// <param name="accountId">Public account id.</param>
    /// <param name="request">The new label.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppAccountResponse> RenameAsync(
        string accountId,
        UpdateWhatsAppAccountRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Makes a connected number the workspace default.</summary>
    /// <param name="accountId">Public account id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Every visible number, since the previous default changed too.</returns>
    public Task<IReadOnlyList<WhatsAppAccountResponse>> SetDefaultAsync(
        string accountId,
        CancellationToken cancellationToken = default);

    /// <summary>Refreshes one number from Meta.</summary>
    /// <param name="accountId">Public account id, or null for the workspace default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refreshed connection, or null when the workspace has no number.</returns>
    public Task<WhatsAppConnection?> SyncAsync(string? accountId, CancellationToken cancellationToken = default);

    /// <summary>Disconnects one number, keeping its history and employee access.</summary>
    /// <param name="accountId">Public account id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppAccountResponse> DisconnectAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Removes a disconnected number and frees its slot.</summary>
    /// <param name="accountId">Public account id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task DeleteAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>Projects a number for the caller.</summary>
    /// <param name="connection">The stored connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppAccountResponse> ToResponseAsync(
        WhatsAppConnection connection,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWhatsAppAccountService" />
public sealed partial class WhatsAppAccountService : IWhatsAppAccountService
{
    private readonly IWhatsAppConnectionRepository _connections;
    private readonly IRepository<WhatsAppAccountAccess> _access;
    private readonly IRepository<User> _users;
    private readonly IQueryExecutor _queries;
    private readonly IWhatsAppAccessService _accessService;
    private readonly IWhatsAppConnectionService _connectionService;
    private readonly IWhatsAppGateway _gateway;
    private readonly ISecretProtector _protector;
    private readonly IPlanGuard _planGuard;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<WhatsAppAccountService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppAccountService(
        IWhatsAppConnectionRepository connections,
        IRepository<WhatsAppAccountAccess> access,
        IRepository<User> users,
        IQueryExecutor queries,
        IWhatsAppAccessService accessService,
        IWhatsAppConnectionService connectionService,
        IWhatsAppGateway gateway,
        ISecretProtector protector,
        IPlanGuard planGuard,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        ITenantContext tenantContext,
        IDateTimeProvider clock,
        ILogger<WhatsAppAccountService> logger)
    {
        _connections = connections;
        _access = access;
        _users = users;
        _queries = queries;
        _accessService = accessService;
        _connectionService = connectionService;
        _gateway = gateway;
        _protector = protector;
        _planGuard = planGuard;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _tenantContext = tenantContext;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WhatsAppAccountListResponse> ListAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.RequireTenantId();
        var scope = await _accessService.GetCallerScopeAsync(cancellationToken);
        var all = await _connections.FindAllForTenantAsync(tenantId, cancellationToken);
        var now = _clock.UtcNow;

        // Only what the caller may see. An employee with access to nothing gets an empty list, not
        // a 403: "no numbers for you" is a state the screen renders, not an error.
        var visible = all
            .Where(connection => scope.Allows(connection.Id, WhatsAppAccessLevel.View))
            .Select(connection => connection.ToAccountResponse(scope.PermissionsOn(connection.Id), now))
            .ToList();

        var plan = await _planGuard.CurrentPlanAsync(cancellationToken);

        return new WhatsAppAccountListResponse(
            visible,
            plan?.MaxWhatsAppAccounts,

            // The whole workspace, not the visible part: the limit is the workspace's.
            all.Count,
            await CallerDefaultAsync(all, scope, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<WhatsAppAccountResponse> GetAsync(string accountId, CancellationToken cancellationToken = default) =>
        await ToResponseAsync(await RequireAsync(accountId, WhatsAppAccessLevel.View, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public async Task<WhatsAppAccountResponse> RenameAsync(
        string accountId,
        UpdateWhatsAppAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connection = await RequireAsync(accountId, WhatsAppAccessLevel.View, cancellationToken);
        var others = (await _connections.FindAllForTenantAsync(_tenantContext.RequireTenantId(), cancellationToken))
            .Where(other => other.Id != connection.Id)
            .Select(other => other.Label);

        connection.Label = WhatsAppAccountRules.UniqueLabel(
            WhatsAppAccountRules.ValidateLabel(request.Label),
            others,
            allowSuffix: false);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await ToResponseAsync(connection, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WhatsAppAccountResponse>> SetDefaultAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var connection = await RequireAsync(accountId, WhatsAppAccessLevel.View, cancellationToken);

        // Everything unaware of multiple numbers sends from the default, so a number that cannot
        // send must not become it.
        if (connection.Status != ConnectionStatus.Connected)
        {
            throw new BusinessRuleException(
                "whatsapp_account_not_connected",
                $"{connection.Label} is not connected, so it cannot be the default. Reconnect it first.");
        }

        await _connectionService.SetDefaultAsync(_tenantContext.RequireTenantId(), connection, cancellationToken);

        return (await ListAsync(cancellationToken)).Items;
    }

    /// <inheritdoc />
    public async Task<WhatsAppConnection?> SyncAsync(string? accountId, CancellationToken cancellationToken = default)
    {
        var connection = await _accessService.ResolveAsync(accountId, WhatsAppAccessLevel.View, cancellationToken);

        if (connection?.PhoneNumberId is not { Length: > 0 } phoneNumberId)
        {
            return connection;
        }

        // Passed explicitly, like every other outbound call. A Super Admin refreshing on a
        // customer's behalf carries the tenant in an explicit scope rather than in their own claims,
        // and the handler that would otherwise supply the token cannot see it.
        var accessToken = connection.EncryptedAccessToken is { Length: > 0 } encrypted
            ? _protector.Unprotect(encrypted)
            : null;

        try
        {
            var number = await _gateway.GetPhoneNumberAsync(phoneNumberId, accessToken, cancellationToken);

            connection.DisplayPhoneNumber = number.DisplayPhoneNumber;
            connection.VerifiedName = number.VerifiedName ?? connection.VerifiedName;
            connection.QualityRating = Enum.TryParse<QualityRating>(number.QualityRating, true, out var rating)
                ? rating
                : connection.QualityRating;
            connection.MessagingTier = ParseTier(number.MessagingTier) ?? connection.MessagingTier;
            connection.PhoneNumberStatus = number.Status ?? connection.PhoneNumberStatus;

            if (connection.WabaId is { Length: > 0 } wabaId && accessToken is not null)
            {
                var account = await _gateway.GetBusinessAccountAsync(wabaId, accessToken, cancellationToken);

                connection.AccountStatus = account.ReviewStatus ?? connection.AccountStatus;
            }

            // A successful round trip is itself the evidence the connection works, so the status is
            // corrected rather than left at whatever it was when it last failed. A disconnected
            // number has no token and cannot get here with a working call.
            if (connection.Status != ConnectionStatus.Disconnected)
            {
                connection.Status = ConnectionStatus.Connected;
            }

            connection.ApiStatus = "ok";
            connection.LastError = null;
        }
        catch (ExternalServiceException exception)
        {
            // Recorded before it is rethrown, so the health panel shows what the sync found even
            // though the request that found it failed.
            connection.ApiStatus = exception.IsTransient ? "degraded" : "down";
            connection.LastError = exception.IsTransient
                ? "Meta did not answer in time. Try syncing again shortly."
                : "Meta refused the request for this number. Reconnect it if this continues.";

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            LogSyncFailed(exception, connection.Id);

            throw;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return connection;
    }

    /// <inheritdoc />
    public async Task<WhatsAppAccountResponse> DisconnectAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var connection = await RequireAsync(accountId, WhatsAppAccessLevel.View, cancellationToken);

        await _connectionService.DisconnectConnectionAsync(connection, cancellationToken);

        return await ToResponseAsync(connection, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var connection = await RequireAsync(accountId, WhatsAppAccessLevel.View, cancellationToken);

        if (connection.Status != ConnectionStatus.Disconnected)
        {
            throw new BusinessRuleException(
                "whatsapp_account_connected",
                $"{connection.Label} is still connected. Disconnect it before removing it.");
        }

        var grants = await _queries.ToListAsync(
            _access.Query(asNoTracking: false).Where(row => row.WhatsAppConnectionId == connection.Id),
            cancellationToken);

        foreach (var grant in grants)
        {
            _access.Remove(grant);
        }

        // Nobody keeps a default they can no longer have.
        var defaults = await _queries.ToListAsync(
            _users.Query(asNoTracking: false).Where(user => user.DefaultWhatsAppConnectionId == connection.Id),
            cancellationToken);

        foreach (var user in defaults)
        {
            user.DefaultWhatsAppConnectionId = null;
        }

        // Soft delete. Conversations and campaigns keep pointing at it and keep its label; the phone
        // number stays on the row but a deleted row never claims inbound traffic.
        connection.IsDefault = false;
        _connections.Remove(connection);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WhatsAppAccountResponse> ToResponseAsync(
        WhatsAppConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var scope = await _accessService.GetCallerScopeAsync(cancellationToken);

        return connection.ToAccountResponse(scope.PermissionsOn(connection.Id), _clock.UtcNow);
    }

    private async Task<WhatsAppConnection> RequireAsync(
        string accountId,
        WhatsAppAccessLevel required,
        CancellationToken cancellationToken) =>
        await _accessService.ResolveAsync(accountId, required, cancellationToken)
        ?? throw new NotFoundException("WhatsApp account", accountId);

    /// <summary>The caller's own default: theirs, else the workspace's, else the first they can see.</summary>
    private async Task<string?> CallerDefaultAsync(
        IReadOnlyList<WhatsAppConnection> all,
        WhatsAppAccessScope scope,
        CancellationToken cancellationToken)
    {
        long? own = null;

        if (_currentUser.UserId is { } userId)
        {
            own = await _queries.FirstOrDefaultAsync(
                _users.Query().Where(user => user.Id == userId).Select(user => user.DefaultWhatsAppConnectionId),
                cancellationToken);
        }

        var visible = all.Where(connection => scope.Allows(connection.Id, WhatsAppAccessLevel.View)).ToList();

        var chosen = visible.FirstOrDefault(connection => connection.Id == own)
                     ?? visible.FirstOrDefault(connection => connection.IsDefault)
                     ?? visible.FirstOrDefault(connection => connection.Status == ConnectionStatus.Connected)
                     ?? visible.FirstOrDefault();

        return chosen is null ? null : PublicId.From(PublicId.WhatsAppAccount, chosen.Id);
    }

    private static MessagingTier? ParseTier(string? tier) => tier?.Trim().ToUpperInvariant() switch
    {
        "TIER_50" or "TIER_250" => MessagingTier.Tier250,
        "TIER_1K" => MessagingTier.Tier1K,
        "TIER_10K" => MessagingTier.Tier10K,
        "TIER_100K" => MessagingTier.Tier100K,
        "TIER_UNLIMITED" or "UNLIMITED" => MessagingTier.Unlimited,
        _ => null,
    };

    [LoggerMessage(
        EventId = 2620,
        Level = LogLevel.Warning,
        Message = "Refreshing WhatsApp connection {ConnectionId} from Meta failed.")]
    private partial void LogSyncFailed(Exception exception, long connectionId);
}
