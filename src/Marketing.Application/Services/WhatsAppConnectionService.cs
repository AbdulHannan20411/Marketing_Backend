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
        var connection = await _connections.FindForTenantAsync(tenantId, cancellationToken);

        // Stored first so the auth handler can find the token when the verification call below
        // goes out - that call is itself authenticated as the tenant.
        if (connection is null)
        {
            connection = new WhatsAppConnection
            {
                Id = SequentialGuid.Create(),
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
            var number = await _gateway.GetPhoneNumberAsync(phoneNumberId, cancellationToken);

            connection.DisplayPhoneNumber = number.DisplayPhoneNumber;
            connection.VerifiedName = number.VerifiedName ?? string.Empty;
            connection.QualityRating = ParseQuality(number.QualityRating);
            connection.Status = ConnectionStatus.Connected;
            connection.ConnectedAt = _clock.UtcNow;
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
            connection.ConnectedAt,
            connection.WebhookHealthy,
            connection.TemplateNamespaceAlias);

    [LoggerMessage(
        EventId = 2601,
        Level = LogLevel.Information,
        Message = "Tenant {TenantId} connected WhatsApp number {PhoneNumberId}. Manual: {IsManual}.")]
    private partial void LogConnected(Guid tenantId, string phoneNumberId, bool isManual);

    [LoggerMessage(
        EventId = 2602,
        Level = LogLevel.Warning,
        Message = "Could not verify WhatsApp number {PhoneNumberId} for tenant {TenantId}.")]
    private partial void LogVerificationFailed(Exception exception, Guid tenantId, string phoneNumberId);

    [LoggerMessage(
        EventId = 2603,
        Level = LogLevel.Warning,
        Message = "Tenant {TenantId} disconnected WhatsApp; the stored token was destroyed.")]
    private partial void LogDisconnected(Guid tenantId);
}
