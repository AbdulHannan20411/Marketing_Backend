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
    public async Task<string> UploadTemplateHeaderSampleAsync(
        byte[] content,
        string fileName,
        string mimeType,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (string.IsNullOrWhiteSpace(_options.AppId))
        {
            throw UploadFailed("WhatsApp:AppId is not configured, so example files cannot be uploaded to Meta.");
        }

        // Not the Graph client: the upload session id contains a colon that Refit would escape, and
        // the second call authenticates with "OAuth", not "Bearer". Both are plain HTTP.
        using var client = _httpClientFactory.CreateClient(MediaDownloadClient);

        var sessionId = await StartUploadSessionAsync(client, content.LongLength, fileName, mimeType, accessToken, cancellationToken)
                        ?? (_options.SystemUserAccessToken is { Length: > 0 } systemToken
                            // The business token may not be allowed to open an upload session against
                            // this app. The app's own system-user token always is.
                            ? await StartUploadSessionAsync(client, content.LongLength, fileName, mimeType, systemToken, cancellationToken)
                            : null)
                        ?? throw UploadFailed("Meta refused to open an upload session for the example file.");

        var uploadToken = sessionId.Token;

        try
        {
            return await SendUploadBytesAsync(client, sessionId.Id, uploadToken, content, 0, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or ExternalServiceException { IsTransient: true })
        {
            // Resumed, not restarted: Meta says how much it already has.
            LogUploadRetry(exception, fileName);

            var offset = await UploadOffsetAsync(client, sessionId.Id, uploadToken, cancellationToken);

            try
            {
                return await SendUploadBytesAsync(client, sessionId.Id, uploadToken, content, offset, cancellationToken);
            }
            catch (Exception retry) when (retry is HttpRequestException or ExternalServiceException)
            {
                throw UploadFailed(retry.Message);
            }
        }
        catch (ExternalServiceException exception)
        {
            throw UploadFailed(exception.Message);
        }
    }

    /// <summary>Opens a Resumable Upload session, or returns null when Meta refuses the token.</summary>
    private async Task<(string Id, string Token)?> StartUploadSessionAsync(
        HttpClient client,
        long length,
        string fileName,
        string mimeType,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var uri = $"{GraphRoot()}/{Uri.EscapeDataString(_options.AppId)}/uploads"
                  + $"?file_name={Uri.EscapeDataString(fileName)}"
                  + $"&file_length={length.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                  + $"&file_type={Uri.EscapeDataString(mimeType)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation("Authorization", Bearer(accessToken));

        using var response = await client.SendAsync(request, cancellationToken);

        if ((int)response.StatusCode is 400 or 401 or 403)
        {
            LogUploadSessionRefused((int)response.StatusCode);

            return null;
        }

        var body = await ReadJsonAsync(response, cancellationToken);

        return body.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } sessionId
            ? (sessionId, accessToken)
            : throw UploadFailed("Meta opened no upload session.");
    }

    /// <summary>Sends the bytes from an offset and returns the header handle.</summary>
    private async Task<string> SendUploadBytesAsync(
        HttpClient client,
        string sessionId,
        string accessToken,
        byte[] content,
        long offset,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GraphRoot()}/{sessionId}");

        // "OAuth", as Meta documents for this call, not "Bearer".
        request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {accessToken}");
        request.Headers.TryAddWithoutValidation("file_offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = new ByteArrayContent(content, (int)offset, content.Length - (int)offset);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);

        return body.TryGetProperty("h", out var handle) && handle.GetString() is { Length: > 0 } value
            ? value
            : throw new ExternalServiceException("MetaWhatsAppCloudApi", "Meta took the example file but returned no handle.");
    }

    /// <summary>How many bytes of an interrupted upload Meta already holds.</summary>
    private async Task<long> UploadOffsetAsync(
        HttpClient client,
        string sessionId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphRoot()}/{sessionId}");
        request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {accessToken}");

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await ReadJsonAsync(response, cancellationToken);

        return body.TryGetProperty("file_offset", out var offset) && offset.TryGetInt64(out var value) ? value : 0;
    }

    private static async Task<System.Text.Json.JsonElement> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new ExternalServiceException(
                "MetaWhatsAppCloudApi",
                $"Meta's upload API returned {(int)response.StatusCode}.",
                innerException: null,
                isTransient: (int)response.StatusCode >= 500);
        }

        using var document = System.Text.Json.JsonDocument.Parse(text.Length == 0 ? "{}" : text);

        return document.RootElement.Clone();
    }

    private string GraphRoot() => $"{_options.BaseUrl.TrimEnd('/')}/{_options.ApiVersion.Trim('/')}";

    /// <summary>The refusal the client shows; the reason is for the log, not the customer.</summary>
    private RequestRejectedException UploadFailed(string reason)
    {
        LogUploadFailed(reason);

        return new RequestRejectedException(
            System.Net.HttpStatusCode.BadGateway,
            "meta_upload_failed",
            "Meta did not accept the example file. Try again, or use a smaller file.");
    }

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
                cancellationToken: cancellationToken);

            templates.AddRange(response.Data.Select(template => new MetaTemplate(
                template.Id,
                template.Name,
                template.Language,
                template.Status,
                template.Category,
                template.Components?.FirstOrDefault(component =>
                    string.Equals(component.Type, "HEADER", StringComparison.OrdinalIgnoreCase))?.Format)));

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
        MetaHeaderMedia? headerMedia = null,
        string? accessToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bodyParameters);

        // Meta expects the recipient as digits without a leading plus.
        var to = PhoneNumbers.Normalise(recipient);

        var parts = new List<TemplateComponent>(2);

        if (headerMedia is not null)
        {
            parts.Add(new TemplateComponent(
                "header",
                [TemplateParameter.ForMedia(headerMedia.Kind, headerMedia.MetaMediaId, headerMedia.FileName)]));
        }

        if (bodyParameters.Count > 0)
        {
            parts.Add(new TemplateComponent("body", [.. bodyParameters.Select(value => new TemplateParameter(value))]));
        }

        var components = parts.Count == 0 ? null : parts;

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
        EventId = 2612,
        Level = LogLevel.Warning,
        Message = "Uploading template example file {FileName} to Meta was interrupted; resuming once.")]
    private partial void LogUploadRetry(Exception exception, string fileName);

    [LoggerMessage(
        EventId = 2614,
        Level = LogLevel.Warning,
        Message = "Uploading a template example file to Meta failed: {Reason}")]
    private partial void LogUploadFailed(string reason);

    [LoggerMessage(
        EventId = 2613,
        Level = LogLevel.Warning,
        Message = "Meta refused to open an upload session ({StatusCode}).")]
    private partial void LogUploadSessionRefused(int statusCode);

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
