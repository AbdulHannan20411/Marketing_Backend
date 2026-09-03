using Marketing.Common.Exceptions;
using Marketing.Application.Interfaces;
using Marketing.Common.Helpers;
using Marketing.Infrastructure.WhatsApp.Clients;
using Marketing.Infrastructure.WhatsApp.Models;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marketing.Infrastructure.WhatsApp;

/// <summary>Meta Cloud API implementation of <see cref="IWhatsAppGateway"/>.</summary>
public sealed partial class MetaWhatsAppGateway : IWhatsAppGateway
{
    /// <summary>Templates fetched per page. Meta's maximum for this edge.</summary>
    private const int TemplatePageSize = 100;

    /// <summary>
    /// Ceiling on pages walked in one sync.
    /// <para>
    /// A guard, not a limit anyone should hit: without it a paging bug or a cursor Meta never
    /// advances turns a sync into an unbounded loop against someone else's API.
    /// </para>
    /// </summary>
    private const int MaxTemplatePages = 50;

    private readonly IWhatsAppCloudApi _client;
    private readonly IDateTimeProvider _clock;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<MetaWhatsAppGateway> _logger;

    /// <summary>Initialises a new instance.</summary>
    public MetaWhatsAppGateway(
        IWhatsAppCloudApi client,
        IDateTimeProvider clock,
        IOptions<WhatsAppOptions> options,
        ILogger<MetaWhatsAppGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MetaAccessToken> ExchangeCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var response = await _client.ExchangeCodeAsync(
            _options.AppId,
            _options.AppSecret,
            code,
            cancellationToken);

        return new MetaAccessToken(
            response.AccessToken,
            response.ExpiresIn is { } seconds and > 0 ? _clock.UtcNow.AddSeconds(seconds) : null);
    }

    /// <inheritdoc />
    public async Task<MetaPhoneNumber> GetPhoneNumberAsync(
        string phoneNumberId,
        string? accessToken = null,
        CancellationToken cancellationToken = default)
    {
        // Explicit when the caller has one. During onboarding the token has only just been written
        // and the handler that normally looks it up runs in its own dependency-injection scope,
        // which does not see the tenant the request is scoped to.
        var number = await _client.GetPhoneNumberAsync(
            phoneNumberId,
            accessToken is null ? null : Bearer(accessToken),
            cancellationToken);

        return new MetaPhoneNumber(
            number.Id,
            number.DisplayPhoneNumber,
            number.VerifiedName,
            number.QualityRating,
            number.MessagingTier);
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetTokenExpiryAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        TokenDebugResponse response;

        try
        {
            // The token inspects itself. Meta accepts that, and it avoids needing an app-level
            // token here just to read a property of the one already in hand.
            response = await _client.DebugTokenAsync(accessToken, accessToken, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deliberately swallowed. This call only decides whether a warning date can be shown;
            // failing the whole connection over it would trade a missing warning for a missing
            // connection. Whether the credential works is settled by the calls that follow.
            LogTokenInspectionFailed(exception);

            return null;
        }

        // Meta sends 0 for a token that never expires - a system user's. Read as "no expiry"
        // rather than as the epoch, which would report every permanent token as long dead.
        if (response.Data?.ExpiresAt is not { } expiresAt || expiresAt <= 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(expiresAt);
    }

    /// <inheritdoc />
    public async Task SubscribeToWebhooksAsync(
        string wabaId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        // Passed explicitly: this runs during connect, before the token is stored, so the handler
        // that normally supplies it has nothing to look up.
        var result = await _client.SubscribeAppAsync(wabaId, Bearer(accessToken), cancellationToken);

        if (!result.Success)
        {
            // Refused rather than warned about. A connection without a webhook subscription looks
            // healthy and receives nothing, which is the hardest failure of this integration to
            // diagnose after the fact.
            throw new BusinessRuleException(
                "whatsapp_subscribe_failed",
                "Meta refused to subscribe this app to the account's updates. Reconnect the number.");
        }
    }

    /// <inheritdoc />
    public async Task RegisterPhoneNumberAsync(
        string phoneNumberId,
        string pin,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.RegisterPhoneNumberAsync(
            phoneNumberId,
            new { messaging_product = "whatsapp", pin },
            Bearer(accessToken),
            cancellationToken);

        if (!result.Success)
        {
            throw new BusinessRuleException(
                "whatsapp_register_failed",
                "Meta refused to register this number for sending. Check it is not already in use on WhatsApp.");
        }
    }

    /// <inheritdoc />
    public async Task<MetaBusinessAccount> GetBusinessAccountAsync(
        string wabaId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var account = await _client.GetBusinessAccountAsync(
            wabaId, Bearer(accessToken), cancellationToken: cancellationToken);

        return new MetaBusinessAccount(account.Id ?? wabaId, account.Name, account.TemplateNamespace);
    }

    /// <summary>Formats a token as an Authorization header value.</summary>
    private static string Bearer(string accessToken) => $"Bearer {accessToken}";

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetaTemplate>> GetTemplatesAsync(
        string wabaId,
        string? accessToken = null,
        CancellationToken cancellationToken = default)
    {
        var templates = new List<MetaTemplate>();
        string? cursor = null;

        // Followed to the end rather than taking the first page. A tenant with more than a hundred
        // templates would otherwise appear to lose the rest on every sync.
        for (var page = 0; page < MaxTemplatePages; page++)
        {
            var response = await _client.GetTemplatesAsync(
                wabaId,
                TemplatePageSize,
                cursor,
                accessToken is null ? null : Bearer(accessToken),
                cancellationToken);

            templates.AddRange(response.Data.Select(template => new MetaTemplate(
                template.Id,
                template.Name,
                template.Language,
                template.Status,
                template.Category)));

            cursor = response.Paging?.Cursors?.After;

            if (string.IsNullOrEmpty(cursor) || response.Data.Count == 0)
            {
                break;
            }
        }

        return templates;
    }

    /// <inheritdoc />
    public async Task<string> SendTemplateAsync(
        string phoneNumberId,
        string recipient,
        string templateName,
        string languageCode,
        IReadOnlyList<string> bodyParameters,
        string? accessToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bodyParameters);

        // Meta expects the recipient as digits without a leading plus.
        var to = PhoneNumbers.Normalise(recipient);

        var components = bodyParameters.Count == 0
            ? null
            : new List<TemplateComponent>
            {
                new("body", [.. bodyParameters.Select(value => new TemplateParameter(value))]),
            };

        var request = new SendTemplateMessageRequest(
            to,
            new TemplateMessagePayload(templateName, new TemplateLanguage(languageCode), components));

        var response = await _client.SendMessageAsync(
            phoneNumberId,
            request,
            accessToken is null ? null : Bearer(accessToken),
            cancellationToken);

        // Meta accepts one message and returns one id. An empty array means the request was
        // accepted but nothing was queued, which must not be reported as a successful send.
        return response.Messages.Count > 0
            ? response.Messages[0].Id
            : throw new Common.Exceptions.ExternalServiceException(
                "MetaWhatsAppCloudApi",
                "Meta accepted the send request but returned no message id.");
    }

    [LoggerMessage(
        EventId = 2610,
        Level = LogLevel.Warning,
        Message = "Could not inspect the WhatsApp token's expiry; connecting without a warning date.")]
    private partial void LogTokenInspectionFailed(Exception exception);
}
