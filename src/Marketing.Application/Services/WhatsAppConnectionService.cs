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

        return await StoreAsync(
            request.AccessToken,
            request.WabaId,
            request.PhoneNumberId,
            // A system-user token has no stated expiry. Recorded as null rather than guessed, so
            // nothing later treats a fabricated date as a fact.
            expiresAt: null,
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

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            // Subscribe first. Without it Meta delivers nothing for this account — no inbound
            // messages, no receipts, no template verdicts — and the failure is silent: the
            // connection looks healthy and the inbox simply never fills. Doing it before the
            // profile read means a refusal here stops the connection rather than producing a
            // connected-looking account that can never receive anything.
            await _gateway.SubscribeToWebhooksAsync(wabaId, accessToken, cancellationToken);

            // Registration is what makes a freshly onboarded number able to send. The PIN is
            // two-factor material Meta will ask for again if the number is ever re-registered, so
            // it is generated once and kept rather than regenerated on each attempt.
            connection.RegistrationPin ??= NewRegistrationPin();

            // Not fatal when it fails. A number that is already registered - every Meta test
            // number, and any number onboarded through Embedded Signup - rejects a second
            // registration because the PIN it already holds is not the one being offered. That is
            // not a broken connection; it is a number that needed no registering.
            //
            // Whether the number actually works is settled by the profile read below, which is the
            // honest test. Treating this call as decisive made every test number impossible to
            // connect.
            try
            {
                await _gateway.RegisterPhoneNumberAsync(
                    phoneNumberId, connection.RegistrationPin, accessToken, cancellationToken);
            }
            catch (ExternalServiceException exception)
            {
                LogRegistrationSkipped(exception, phoneNumberId);
            }

            var number = await _gateway.GetPhoneNumberAsync(phoneNumberId, accessToken, cancellationToken);

            connection.DisplayPhoneNumber = number.DisplayPhoneNumber;
            connection.VerifiedName = number.VerifiedName ?? string.Empty;
            connection.QualityRating = ParseQuality(number.QualityRating);

            // Absent means "leave what we had". Meta omits the tier on numbers it has not yet
            // rated, and defaulting to the lowest would tell the customer their throughput had
            // dropped when nothing changed.
            connection.MessagingTier = ParseTier(number.MessagingTier) ?? connection.MessagingTier;

            var account = await _gateway.GetBusinessAccountAsync(wabaId, accessToken, cancellationToken);

            connection.TemplateNamespaceAlias = account.TemplateNamespace ?? connection.TemplateNamespaceAlias;

            connection.Status = ConnectionStatus.Connected;
            connection.ConnectedAt = _clock.UtcNow;
        }
        catch (BusinessRuleException)
        {
            // Subscription or registration refused. The credential is kept for the same reason a
            // failed verification keeps it — the operator can usually fix the cause and retry
            // without starting signup again — but the connection is not claimed as working.
            connection.Status = ConnectionStatus.Error;

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            throw;
        }
        catch (ExternalServiceException exception)
        {
            // The credential is kept and the connection is marked errored rather than rolled back.
            // A token that works for exchange but fails verification is usually a permissions or
            // number-registration problem the operator can fix, and discarding it would make them
            // start the whole signup again.
            connection.Status = ConnectionStatus.Error;

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            LogVerificationFailed(exception, tenantId, phoneNumberId);

            throw new BusinessRuleException(
                "whatsapp_verification_failed",
                "The account was linked but the phone number could not be verified with Meta. "
                + "Check that the number is registered and the app has the whatsapp_business_messaging permission.");
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogConnected(tenantId, phoneNumberId, isManual);

        return Map(connection);
    }

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

    private static WhatsAppConnectionResponse Map(WhatsAppConnection connection) =>
        new(
            connection.Status,
            connection.DisplayPhoneNumber,
            connection.VerifiedName,
            connection.BusinessProfileAbout,
            connection.BusinessCategory,
            connection.QualityRating,
            connection.MessagingLimit,
            connection.MessagesLast24h,
            connection.MessagingTier,
            connection.ConnectedAt,
            connection.WebhookHealthy,
            connection.TemplateNamespaceAlias);

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
}
