using Marketing.Common.Exceptions;
using Marketing.Application.Interfaces;
using Marketing.Common.Helpers;
using Marketing.Infrastructure.WhatsApp.Clients;
using Marketing.Infrastructure.WhatsApp.Models;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;
using static Marketing.Common.Constants.ContractEnums;

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

    /// <summary>Named client for Meta's media host, which is not the Graph host.</summary>
    public const string MediaDownloadClient = "meta-media";

    private readonly IWhatsAppCloudApi _client;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDateTimeProvider _clock;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<MetaWhatsAppGateway> _logger;

    /// <summary>Initialises a new instance.</summary>
    public MetaWhatsAppGateway(
        IWhatsAppCloudApi client,
        IHttpClientFactory httpClientFactory,
        IDateTimeProvider clock,
        IOptions<WhatsAppOptions> options,
        ILogger<MetaWhatsAppGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _httpClientFactory = httpClientFactory;
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
            cancellationToken: cancellationToken);

        return new MetaPhoneNumber(
            number.Id,
            number.DisplayPhoneNumber,
            number.VerifiedName,
            number.QualityRating,
            number.MessagingTier,
            number.Status);
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
    public async Task UnsubscribeFromWebhooksAsync(
        string wabaId,
        string accessToken,
        CancellationToken cancellationToken = default) =>
        await _client.UnsubscribeAppAsync(wabaId, Bearer(accessToken), cancellationToken);

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

        return new MetaBusinessAccount(
            account.Id ?? wabaId, account.Name, account.TemplateNamespace, account.ReviewStatus);
    }

    /// <inheritdoc />
    public Task<string> SendTextAsync(
        string phoneNumberId,
        string recipient,
        string body,
        string accessToken,
        CancellationToken cancellationToken = default) =>
        SendFreeFormAsync(
            phoneNumberId,
            new Dictionary<string, object?>
            {
                ["messaging_product"] = "whatsapp",
                ["recipient_type"] = "individual",
                ["to"] = PhoneNumbers.Normalise(recipient),
                ["type"] = "text",

                // Link previews on: an agent pasting a tracking link means the customer to open it,
                // and a bare URL with no card reads as suspicious in a chat.
                ["text"] = new Dictionary<string, object?>
                {
                    ["preview_url"] = true,
                    ["body"] = body,
                },
            },
            accessToken,
            cancellationToken);

    /// <inheritdoc />
    public Task<string> SendMediaAsync(
        string phoneNumberId,
        string recipient,
        ConversationMessageKind kind,
        string metaMediaId,
        string? caption,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var type = kind switch
        {
            ConversationMessageKind.Image => "image",
            ConversationMessageKind.Video => "video",
            ConversationMessageKind.Document => "document",
            ConversationMessageKind.Audio => "audio",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a media message kind."),
        };

        var content = new Dictionary<string, object?> { ["id"] = metaMediaId };

        // Audio carries no caption at all - Meta refuses the message rather than ignoring the field.
        if (kind != ConversationMessageKind.Audio && caption is { Length: > 0 } text)
        {
            content["caption"] = text;
        }

        return SendFreeFormAsync(
            phoneNumberId,
            new Dictionary<string, object?>
            {
                ["messaging_product"] = "whatsapp",
                ["recipient_type"] = "individual",
                ["to"] = PhoneNumbers.Normalise(recipient),
                ["type"] = type,
                [type] = content,
            },
            accessToken,
            cancellationToken);
    }

    /// <summary>Posts a prepared message body and returns the id Meta assigned it.</summary>
    private async Task<string> SendFreeFormAsync(
        string phoneNumberId,
        Dictionary<string, object?> payload,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var response = await _client.SendMessageAsync(
            phoneNumberId,
            payload,
            Bearer(accessToken),
            cancellationToken);

        // An accepted request that queued nothing is not a send, and reporting it as one would leave
        // an agent believing a customer had been answered.
        return response.Messages.Count > 0
            ? response.Messages[0].Id
            : throw new ExternalServiceException(
                "MetaWhatsAppCloudApi",
                "Meta accepted the message but returned no message id.");
    }

    /// <inheritdoc />
    public async Task<string> UploadMediaAsync(
        string phoneNumberId,
        Stream content,
        string fileName,
        string mimeType,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var response = await _client.UploadMediaAsync(
            phoneNumberId,
            new StreamPart(content, fileName, mimeType),
            "whatsapp",
            mimeType,
            Bearer(accessToken),
            cancellationToken);

        // Without an id the file cannot be attached to anything, so an acknowledgement that lacks
        // one is a failure rather than something to store and discover later.
        return response.Id is { Length: > 0 }
            ? response.Id
            : throw new ExternalServiceException(
                "MetaWhatsAppCloudApi",
                "Meta accepted the upload but returned no media id.");
    }

    /// <inheritdoc />
    public async Task<MetaMediaDownload> DownloadMediaAsync(
        string mediaId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var handle = await _client.GetMediaAsync(mediaId, Bearer(accessToken), cancellationToken);

        if (handle.Url is not { Length: > 0 } url)
        {
            throw new ExternalServiceException(
                "MetaWhatsAppCloudApi",
                "Meta described the media but gave no download URL.");
        }

        // Not the Graph client: the URL points at Meta's media host, and the Refit client is bound
        // to the Graph base address. The credential still has to travel, or the host returns 401.
        using var client = _httpClientFactory.CreateClient(MediaDownloadClient);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        request.Headers.TryAddWithoutValidation("Authorization", Bearer(accessToken));

        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new ExternalServiceException(
                "MetaWhatsAppCloudApi",
                $"Media download returned {(int)response.StatusCode}.",
                innerException: null,
                isTransient: (int)response.StatusCode >= 500);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        return new MetaMediaDownload(
            bytes,
            handle.MimeType is { Length: > 0 } mimeType
                ? mimeType
                : response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            handle.FileSize > 0 ? handle.FileSize : bytes.LongLength);
    }

    /// <summary>Formats a token as an Authorization header value.</summary>
    private static string Bearer(string accessToken) => $"Bearer {accessToken}";

    /// <inheritdoc />
    public async Task<MetaTemplateSubmission> CreateTemplateAsync(
        string wabaId,
        MetaTemplateDefinition definition,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var response = await _client.CreateTemplateAsync(
            wabaId,
            TemplateDefinitionRequest.ForCreate(definition),
            Bearer(accessToken),
            cancellationToken);

        // Without an id the template can never be edited, deleted or matched to a review verdict, so
        // an acknowledgement that lacks one is treated as a failure rather than stored half-made.
        return response.Id is { Length: > 0 } id
            ? new MetaTemplateSubmission(id, response.Status, response.Category)
            : throw new ExternalServiceException(
                "MetaWhatsAppCloudApi",
                "Meta accepted the template but returned no template id.");
    }

    /// <inheritdoc />
    public async Task UpdateTemplateAsync(
        string metaTemplateId,
        MetaTemplateDefinition definition,
        bool includeCategory,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.UpdateTemplateAsync(
            metaTemplateId,
            TemplateDefinitionRequest.ForEdit(definition, includeCategory),
            Bearer(accessToken),
            cancellationToken);

        if (!result.Success)
        {
            throw new ExternalServiceException("MetaWhatsAppCloudApi", "Meta did not accept the template edit.");
        }
    }

    /// <inheritdoc />
    public async Task DeleteTemplateAsync(
        string wabaId,
        string name,
        string metaTemplateId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.DeleteTemplateAsync(
            wabaId,
            name,
            metaTemplateId,
            Bearer(accessToken),
            cancellationToken);

        if (!result.Success)
        {
            throw new ExternalServiceException("MetaWhatsAppCloudApi", "Meta did not delete the template.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetaTemplate>> GetTemplatesAsync(
        string wabaId,
        string? accessToken = null,
        CancellationToken cancellationToken = default)
    {
        var templates = new List<MetaTemplate>();
        string? cursor = null;
        var pages = 0;

        // Followed to the end rather than taking the first page. A tenant with more than a hundred
        // templates would otherwise appear to lose the rest on every sync.
        for (var page = 0; page < MaxTemplatePages; page++)
        {
            pages++;

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

        // What Meta actually answered. A sync that shows nothing is otherwise indistinguishable from
        // one that read nothing: the call succeeds either way, and only this says whether the account
        // is empty or the templates were lost on the way in. Counts and statuses only - no names.
        LogTemplatesFetched(
            templates.Count,
            wabaId,
            pages,
            templates.Count == 0
                ? "none"
                : string.Join(
                    ", ",
                    templates
                        .GroupBy(template => template.Status, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(group => $"{group.Key}={group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)}")));

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

    [LoggerMessage(
        EventId = 2611,
        Level = LogLevel.Information,
        Message = "Meta returned {TemplateCount} templates for WhatsApp Business Account {WabaId} over {PageCount} page(s). By status: {StatusSummary}.")]
    private partial void LogTemplatesFetched(int templateCount, string wabaId, int pageCount, string statusSummary);
}
