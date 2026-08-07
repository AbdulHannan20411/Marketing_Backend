using Marketing.Application.Interfaces;
using Marketing.Common.Helpers;
using Marketing.Infrastructure.WhatsApp.Clients;
using Marketing.Infrastructure.WhatsApp.Models;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Options;

namespace Marketing.Infrastructure.WhatsApp;

/// <summary>Meta Cloud API implementation of <see cref="IWhatsAppGateway"/>.</summary>
public sealed class MetaWhatsAppGateway : IWhatsAppGateway
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

    /// <summary>Initialises a new instance.</summary>
    public MetaWhatsAppGateway(
        IWhatsAppCloudApi client,
        IDateTimeProvider clock,
        IOptions<WhatsAppOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _clock = clock;
        _options = options.Value;
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
        CancellationToken cancellationToken = default)
    {
        var number = await _client.GetPhoneNumberAsync(phoneNumberId, cancellationToken);

        return new MetaPhoneNumber(
            number.Id,
            number.DisplayPhoneNumber,
            number.VerifiedName,
            number.QualityRating);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MetaTemplate>> GetTemplatesAsync(
        string wabaId,
        CancellationToken cancellationToken = default)
    {
        var templates = new List<MetaTemplate>();
        string? cursor = null;

        // Followed to the end rather than taking the first page. A tenant with more than a hundred
        // templates would otherwise appear to lose the rest on every sync.
        for (var page = 0; page < MaxTemplatePages; page++)
        {
            var response = await _client.GetTemplatesAsync(wabaId, TemplatePageSize, cursor, cancellationToken);

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

        var response = await _client.SendMessageAsync(phoneNumberId, request, cancellationToken);

        // Meta accepts one message and returns one id. An empty array means the request was
        // accepted but nothing was queued, which must not be reported as a successful send.
        return response.Messages.Count > 0
            ? response.Messages[0].Id
            : throw new Common.Exceptions.ExternalServiceException(
                "MetaWhatsAppCloudApi",
                "Meta accepted the send request but returned no message id.");
    }
}
