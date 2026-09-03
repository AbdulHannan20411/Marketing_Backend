using Marketing.Application.DTOs.WhatsApp;
using Marketing.Business.Repositories.Interfaces;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.DataAccess.Entities;
using Marketing.Application.Interfaces;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.Services;

/// <summary>Connects, refreshes and disconnects a tenant's Meta WhatsApp Business Account.</summary>
public interface IWhatsAppConnectionService
{
    /// <summary>Completes Embedded Signup and stores the resulting token.</summary>
    public Task<WhatsAppConnectionResponse> ConnectAsync(
        ConnectWhatsAppRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Connects using a directly supplied token. Platform staff only.</summary>
    public Task<WhatsAppConnectionResponse> ConnectManuallyAsync(
        ManualConnectWhatsAppRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Disconnects the account and destroys the stored token.</summary>
    public Task<WhatsAppConnectionResponse> DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances every connection still part-way through onboarding.
    /// </summary>
    /// <remarks>
    /// Driven by the scheduler. Safe to call concurrently and safe to call repeatedly: a step that
    /// has already succeeded is not run again, so a second caller finds nothing left to do.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many connections were advanced.</returns>
    public Task<int> RunPendingOnboardingAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWhatsAppConnectionService" />
public sealed partial class WhatsAppConnectionService : IWhatsAppConnectionService
{
    private readonly IWhatsAppConnectionRepository _connections;
    private readonly IWhatsAppGateway _gateway;
    private readonly ISecretProtector _protector;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<WhatsAppConnectionService> _logger;

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppConnectionService(
        IWhatsAppConnectionRepository connections,
        IWhatsAppGateway gateway,
        ISecretProtector protector,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        IDateTimeProvider clock,
        ILogger<WhatsAppConnectionService> logger)
    {
        _connections = connections;
        _gateway = gateway;
        _protector = protector;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WhatsAppConnectionResponse> ConnectAsync(
        ConnectWhatsAppRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Redeemed server-side. The app secret needed to exchange the code must never reach the
        // browser, which is the entire reason this endpoint exists rather than the client calling
        // Meta directly.
        var exchange = await _gateway.ExchangeCodeAsync(request.Code, cancellationToken);

        return await StoreAsync(
            exchange.Value,
            request.WabaId,
            request.PhoneNumberId,
            exchange.ExpiresAtUtc,
            isManual: false,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WhatsAppConnectionResponse> ConnectManuallyAsync(
        ManualConnectWhatsAppRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Asked, not assumed. Both a system-user token (no expiry) and a test-number token (often
        // hours) arrive through this same box, and the earlier assumption that a pasted token was
        // permanent recorded the short-lived kind as never expiring - the precise silent failure
        // the expiry field exists to prevent. A null answer here still means "no stated expiry".
        var expiresAt = await _gateway.GetTokenExpiryAsync(request.AccessToken, cancellationToken);

        return await StoreAsync(
            request.AccessToken,
            request.WabaId,
            request.PhoneNumberId,
            expiresAt,
            isManual: true,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WhatsAppConnectionResponse> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantContext.RequireTenantId();

        var connection = await _connections.FindForTenantAsync(tenantId, cancellationToken)
                         ?? throw new NotFoundException("No WhatsApp account is connected.");

        connection.Status = ConnectionStatus.Disconnected;
        connection.WebhookHealthy = false;
        connection.ConnectedAt = null;

        // The token is destroyed, not just orphaned. Leaving a live credential in a row nobody
        // reads is how a disconnected account still gets used months later.
        connection.EncryptedAccessToken = null;
        connection.TokenExpiresAt = null;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogDisconnected(tenantId);

        return WhatsAppConnectionResponse.Disconnected();
    }

    /// <summary>
    /// Verifies the number against Meta, then stores the connection with the token encrypted.
    /// </summary>
    private async Task<WhatsAppConnectionResponse> StoreAsync(
        string accessToken,
        string wabaId,
        string phoneNumberId,
        DateTimeOffset? expiresAt,
        bool isManual,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.RequireTenantId();

        // Inbound webhooks are routed by phone number id alone, so letting two tenants hold one
        // number would deliver a customer's conversations to a stranger. A unique index enforces
        // this, but the index alone surfaces as a 500 from the driver; checking first turns it into
        // an answer the operator can act on.
        var claimant = await _connections.FindByPhoneNumberIdAsync(phoneNumberId, cancellationToken);

        if (claimant is not null && claimant.TenantId != tenantId)
        {
            throw new BusinessRuleException(
                "whatsapp_number_already_connected",
                "That phone number is already connected to another workspace. Disconnect it there "
                + "first, or connect a different number.");
        }

        var connection = await _connections.FindForTenantAsync(tenantId, cancellationToken);

        // Stored first so the auth handler can find the token when the verification call below
        // goes out - that call is itself authenticated as the tenant.
        if (connection is null)
        {
            connection = new WhatsAppConnection
            {
                TenantId = tenantId,
            };

            _connections.Add(connection);
        }

        connection.WabaId = wabaId;
        connection.PhoneNumberId = phoneNumberId;
        connection.EncryptedAccessToken = _protector.Protect(accessToken);
        connection.TokenExpiresAt = expiresAt;
        connection.Status = ConnectionStatus.Pending;

        // Seeded up front so the client has the whole list to render immediately, including steps
        // that have not started. A panel that grows a row at a time reads as instability; one that
        // shows every step and lights them up reads as progress.
        connection.OnboardingSteps =
        [
            new WhatsAppOnboardingStep
            {
                // Already done by the time anything is stored: the code was exchanged, or the
                // pasted token inspected, before this method was reached.
                Step = OnboardingStep.Token,
                Status = OnboardingStepStatus.Succeeded,
                CompletedAt = _clock.UtcNow,
            },
            new WhatsAppOnboardingStep { Step = OnboardingStep.Subscribe },
            new WhatsAppOnboardingStep { Step = OnboardingStep.Register },
            new WhatsAppOnboardingStep { Step = OnboardingStep.Profile },
        ];

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogConnected(tenantId, phoneNumberId, isManual);

        // Returns here rather than running the remaining steps inline. Subscribing, registering and
        // reading the profile are three round trips to Meta that regularly take seconds and can
        // each fail for their own reason; holding the request open gave the caller one opaque
        // answer at the end, and a browser that gave up first got no answer at all. The poller
        // picks the connection up within seconds and the client watches the steps.
        return Map(connection);
    }

    /// <inheritdoc />
    public async Task<int> RunPendingOnboardingAsync(CancellationToken cancellationToken = default)
    {
        var tenants = await _connections.FindTenantsAwaitingOnboardingAsync(
            TenantsPerPoll, cancellationToken);

        var advanced = 0;

        foreach (var tenantId in tenants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Entered per tenant, so every query below - and the Graph handler's own token lookup -
            // is confined to this tenant and no other.
            using (_tenantContext.BeginScope(tenantId))
            {
                try
                {
                    await ContinueOnboardingAsync(tenantId, cancellationToken);
                    advanced++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One tenant's failed onboarding must not stop the rest. The connection keeps
                    // the step state it reached and is picked up again on the next poll.
                    LogOnboardingFailed(exception, tenantId);
                }
            }
        }

        return advanced;
    }

    /// <summary>Runs whatever steps remain for one tenant's pending connection.</summary>
    private async Task ContinueOnboardingAsync(long tenantId, CancellationToken cancellationToken)
    {
        var connection = await _connections.FindForTenantAsync(tenantId, cancellationToken);

        if (connection is not { Status: ConnectionStatus.Pending, PhoneNumberId: { Length: > 0 } phoneNumberId }
            || connection.WabaId is not { Length: > 0 } wabaId
            || connection.EncryptedAccessToken is not { Length: > 0 } encrypted)
        {
            return;
        }

        // Decrypted once and passed to each call. The handler that normally supplies it resolves
        // the tenant in its own scope, which a poller's scope does not reach - so it would find no
        // tenant, send no credential, and Meta would refuse every call as unauthenticated.
        var accessToken = _protector.Unprotect(encrypted);

        if (!await TryStepAsync(
                connection,
                OnboardingStep.Subscribe,
                () => _gateway.SubscribeToWebhooksAsync(wabaId, accessToken, cancellationToken),
                cancellationToken))
        {
            return;
        }

        // Registration is the one step whose failure is not a failure. Every Meta test number, and
        // every number onboarded through Embedded Signup, is already registered and rejects a
        // second attempt because the PIN it holds is not the one being offered. Recorded as skipped
        // so the panel says so plainly instead of showing a red step on a working connection.
        connection.RegistrationPin ??= NewRegistrationPin();

        await TryStepAsync(
            connection,
            OnboardingStep.Register,
            () => _gateway.RegisterPhoneNumberAsync(
                phoneNumberId, connection.RegistrationPin!, accessToken, cancellationToken),
            cancellationToken,
            failureIsSkip: true);

        if (!await TryStepAsync(
                connection,
                OnboardingStep.Profile,
                async () =>
                {
                    var number = await _gateway.GetPhoneNumberAsync(
                        phoneNumberId, accessToken, cancellationToken);

                    connection.DisplayPhoneNumber = number.DisplayPhoneNumber;
                    connection.VerifiedName = number.VerifiedName ?? string.Empty;
                    connection.QualityRating = ParseQuality(number.QualityRating);

                    // Absent means "leave what we had". Meta omits the tier on numbers it has not
                    // rated, and defaulting to the lowest would tell the customer their throughput
                    // had dropped when nothing had changed.
                    connection.MessagingTier = ParseTier(number.MessagingTier) ?? connection.MessagingTier;

                    var account = await _gateway.GetBusinessAccountAsync(
                        wabaId, accessToken, cancellationToken);

                    connection.TemplateNamespaceAlias =
                        account.TemplateNamespace ?? connection.TemplateNamespaceAlias;
                },
                cancellationToken))
        {
            return;
        }

        connection.Status = ConnectionStatus.Connected;
        connection.ConnectedAt = _clock.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Runs one step, recording what happened to it either way.
    /// </summary>
    /// <returns><see langword="true"/> when onboarding may continue.</returns>
    private async Task<bool> TryStepAsync(
        WhatsAppConnection connection,
        OnboardingStep step,
        Func<Task> work,
        CancellationToken cancellationToken,
        bool failureIsSkip = false)
    {
        var record = connection.OnboardingSteps.FirstOrDefault(entry => entry.Step == step);

        if (record is null)
        {
            // A connection stored before this feature existed has no step list. Adding the row
            // rather than refusing lets those connections finish onboarding normally.
            record = new WhatsAppOnboardingStep { Step = step };
            connection.OnboardingSteps.Add(record);
        }

        if (record.Status is OnboardingStepStatus.Succeeded or OnboardingStepStatus.Skipped)
        {
            // Already settled on an earlier poll. Re-running subscribe or register is not free and,
            // for register, actively harmful.
            return true;
        }

        record.Status = OnboardingStepStatus.Running;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            await work();

            record.Status = OnboardingStepStatus.Succeeded;
            record.Code = null;
            record.Message = null;
        }
        catch (Exception exception) when (exception is BusinessRuleException or ExternalServiceException)
        {
            record.Status = failureIsSkip ? OnboardingStepStatus.Skipped : OnboardingStepStatus.Failed;
            record.Code = failureIsSkip ? null : CodeFor(step, exception);
            record.Message = Truncate(exception.Message);

            if (!failureIsSkip)
            {
                connection.Status = ConnectionStatus.Error;
            }
        }

        record.CompletedAt = _clock.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return record.Status is not OnboardingStepStatus.Failed;
    }

    /// <summary>Maps a step failure to the stable code the client turns into a remedy.</summary>
    /// <remarks>
    /// Deliberately coarse. A code per step, plus the one distinction that changes the advice -
    /// whether the credential itself is the problem - is enough for the client to say something
    /// useful, and a finer taxonomy would break the moment Meta reworded an error.
    /// </remarks>
    private static string CodeFor(OnboardingStep step, Exception exception)
    {
        // The credential, not the step. Any step can report it and the remedy is always the same:
        // reconnect. Checked first because it would otherwise be blamed on whichever call happened
        // to be running when the token lapsed.
        if (exception.Message.Contains("401", StringComparison.Ordinal)
            || exception.Message.Contains("Code 190", StringComparison.Ordinal))
        {
            return "token_rejected";
        }

        return step switch
        {
            OnboardingStep.Subscribe => "subscribe_refused",
            OnboardingStep.Register => "register_refused",
            OnboardingStep.Profile => "profile_unreadable",
            _ => "onboarding_failed",
        };
    }

    /// <summary>Keeps an operator message inside the column that stores it.</summary>
    private static string Truncate(string message) =>
        message.Length <= 500 ? message : message[..500];

    /// <summary>
    /// Reads Meta's tier string, or null when it says nothing.
    /// </summary>
    /// <remarks>
    /// Meta reports <c>TIER_1K</c> and similar. Null is returned rather than the lowest tier so the
    /// caller can distinguish "Meta did not say" from "Meta said 250".
    /// </remarks>
    private static MessagingTier? ParseTier(string? tier) => tier?.Trim().ToUpperInvariant() switch
    {
        "TIER_50" or "TIER_250" => MessagingTier.Tier250,
        "TIER_1K" => MessagingTier.Tier1K,
        "TIER_10K" => MessagingTier.Tier10K,
        "TIER_100K" => MessagingTier.Tier100K,
        "TIER_UNLIMITED" or "UNLIMITED" => MessagingTier.Unlimited,
        _ => null,
    };

    /// <summary>
    /// Generates the six-digit PIN Meta requires to register a number.
    /// </summary>
    /// <remarks>
    /// Cryptographically random rather than sequential or derived: it is two-factor material for
    /// the customer's number, and a guessable one would let somebody else re-register it.
    /// </remarks>
    private static string NewRegistrationPin() =>
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(100_000, 1_000_000)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static QualityRating ParseQuality(string? rating) =>
        Enum.TryParse<QualityRating>(rating, ignoreCase: true, out var parsed) ? parsed : QualityRating.Green;

    /// <summary>
    /// Tenants advanced per poll.
    /// <para>
    /// Bounded so one poll cannot hold the scheduler while it walks every stalled connection on the
    /// platform. Onboarding is rare and short-lived, so a small number clears the queue quickly.
    /// </para>
    /// </summary>
    private const int TenantsPerPoll = 20;

    private static WhatsAppConnectionResponse Map(WhatsAppConnection connection) =>
        connection.ToResponse();

    [LoggerMessage(
        EventId = 2601,
        Level = LogLevel.Information,
        Message = "Tenant {TenantId} connected WhatsApp number {PhoneNumberId}. Manual: {IsManual}.")]
    private partial void LogConnected(long tenantId, string phoneNumberId, bool isManual);

    [LoggerMessage(
        EventId = 2602,
        Level = LogLevel.Warning,
        Message = "Could not verify WhatsApp number {PhoneNumberId} for tenant {TenantId}.")]
    private partial void LogVerificationFailed(Exception exception, long tenantId, string phoneNumberId);

    [LoggerMessage(
        EventId = 2604,
        Level = LogLevel.Information,
        Message = "Registration skipped for number {PhoneNumberId}: it is already registered. "
                  + "The profile read decides whether the number is usable.")]
    private partial void LogRegistrationSkipped(Exception exception, string phoneNumberId);

    [LoggerMessage(
        EventId = 2603,
        Level = LogLevel.Warning,
        Message = "Tenant {TenantId} disconnected WhatsApp; the stored token was destroyed.")]
    private partial void LogDisconnected(long tenantId);

    [LoggerMessage(
        EventId = 2604,
        Level = LogLevel.Error,
        Message = "Onboarding could not be advanced for tenant {TenantId}.")]
    private partial void LogOnboardingFailed(Exception exception, long tenantId);
}
