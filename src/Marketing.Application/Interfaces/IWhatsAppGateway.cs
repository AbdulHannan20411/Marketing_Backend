namespace Marketing.Application.Interfaces;

/// <summary>
/// Everything the platform asks of the Meta WhatsApp Cloud API, expressed in the platform's own
/// terms.
/// <para>
/// The third seam, alongside payments and email. Services depend on this rather than on the Refit
/// client, so the Application layer carries no Refit attributes, no Graph payload shapes and no
/// app credentials - and so a service can be unit-tested without an HTTP stack.
/// </para>
/// </summary>
public interface IWhatsAppGateway
{
    /// <summary>
    /// Returns when a token stops working, or <see langword="null"/> when it never does.
    /// </summary>
    /// <remarks>
    /// Asked rather than assumed. Operators paste two very different credentials into the same
    /// box: a system-user token that never expires, and a test-number token that dies at a fixed
    /// clock boundary, sometimes hours later. Recording the second as permanent produces exactly
    /// the silent failure the expiry field exists to prevent.
    /// <para>
    /// Never throws. An inspection that fails is reported as an unknown expiry, because refusing
    /// to connect over an unavailable answer would be worse than connecting without a warning
    /// date - the credential itself is proven by the calls that follow.
    /// </para>
    /// </remarks>
    /// <param name="accessToken">The token to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<DateTimeOffset?> GetTokenExpiryAsync(
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges an Embedded Signup code for a business access token.
    /// <para>
    /// Authenticated by the app secret, which is why it lives behind this interface and not in a
    /// service: the secret never leaves the infrastructure layer.
    /// </para>
    /// </summary>
    /// <param name="code">Authorisation code from the signup callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<MetaAccessToken> ExchangeCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Reads a phone number's registration details, verifying the connection works.</summary>
    /// <param name="phoneNumberId">Meta phone number identifier.</param>
    /// <param name="accessToken">
    /// Bearer token when the caller already holds one, or null to use the stored one.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<MetaPhoneNumber> GetPhoneNumberAsync(
        string phoneNumberId,
        string? accessToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every template on a WhatsApp Business Account, following pagination.</summary>
    /// <param name="wabaId">WhatsApp Business Account identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="accessToken">Bearer token when the caller holds one, or null for the stored one.</param>
    public Task<IReadOnlyList<MetaTemplate>> GetTemplatesAsync(
        string wabaId,
        string? accessToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one template message.
    /// </summary>
    /// <param name="phoneNumberId">Sending number.</param>
    /// <param name="recipient">Recipient in E.164.</param>
    /// <param name="templateName">Template name as registered with Meta.</param>
    /// <param name="languageCode">Template language tag.</param>
    /// <param name="bodyParameters">Ordered values filling the body placeholders.</param>
    /// <param name="accessToken">
    /// Bearer token when the caller holds one, or null to use the stored one. Background jobs pass
    /// it explicitly, having no signed-in user for the handler to resolve a tenant from.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Meta's message identifier, which later delivery receipts are keyed by.</returns>
    public Task<string> SendTemplateAsync(
        string phoneNumberId,
        string recipient,
        string templateName,
        string languageCode,
        IReadOnlyList<string> bodyParameters,
        string? accessToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes this app to a business account's webhooks.
    /// </summary>
    /// <remarks>
    /// Nothing arrives from Meta until this succeeds — no inbound messages, no delivery receipts,
    /// no template verdicts. Its absence looks exactly like a quiet account, which is why the
    /// connection flow treats a failure here as a failure to connect rather than a warning.
    /// </remarks>
    /// <param name="wabaId">Business account identifier.</param>
    /// <param name="accessToken">The tenant's business token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task SubscribeToWebhooksAsync(
        string wabaId,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>Stops this app receiving a business account's webhooks.</summary>
    /// <remarks>
    /// Subscription is per business account, not per number: the caller must only unsubscribe when
    /// no other connected number in any workspace still uses the account.
    /// </remarks>
    /// <param name="wabaId">Business account identifier.</param>
    /// <param name="accessToken">The tenant's business token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task UnsubscribeFromWebhooksAsync(
        string wabaId,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>Registers a phone number so it can send.</summary>
    /// <param name="phoneNumberId">Phone number identifier.</param>
    /// <param name="pin">Six-digit two-factor PIN, generated and stored by the caller.</param>
    /// <param name="accessToken">The tenant's business token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task RegisterPhoneNumberAsync(
        string phoneNumberId,
        string pin,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a business account, for its name, namespace and messaging tier.</summary>
    /// <param name="wabaId">Business account identifier.</param>
    /// <param name="accessToken">The tenant's business token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<MetaBusinessAccount> GetBusinessAccountAsync(
        string wabaId,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a template on a business account and submits it for Meta's review.</summary>
    /// <param name="wabaId">Business account the template will belong to.</param>
    /// <param name="definition">What the template says.</param>
    /// <param name="accessToken">The tenant's business token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Meta's id for the template, and the status and category it assigned.</returns>
    public Task<MetaTemplateSubmission> CreateTemplateAsync(
        string wabaId,
        MetaTemplateDefinition definition,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces a submitted template's content, which resubmits it for review.</summary>
    /// <param name="metaTemplateId">Meta's id for the template.</param>
    /// <param name="definition">What the template now says. Its name and language are not sent.</param>
    /// <param name="includeCategory">Whether the category changed and must be sent.</param>
    /// <param name="accessToken">The tenant's business token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task UpdateTemplateAsync(
        string metaTemplateId,
        MetaTemplateDefinition definition,
        bool includeCategory,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes one language of a template from a business account.</summary>
    /// <param name="wabaId">Business account the template belongs to.</param>
    /// <param name="name">Template name.</param>
    /// <param name="metaTemplateId">Meta's id for this language of the template.</param>
    /// <param name="accessToken">The tenant's business token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task DeleteTemplateAsync(
        string wabaId,
        string name,
        string metaTemplateId,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a plain text message inside an open customer service window.</summary>
    /// <param name="phoneNumberId">Sending number.</param>
    /// <param name="recipient">Recipient in E.164.</param>
    /// <param name="body">Message text.</param>
    /// <param name="accessToken">The tenant's business token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Meta's message identifier, which later receipts are keyed by.</returns>
    public Task<string> SendTextAsync(
        string phoneNumberId,
        string recipient,
        string body,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a previously uploaded file inside an open customer service window.</summary>
    /// <param name="phoneNumberId">Sending number.</param>
    /// <param name="recipient">Recipient in E.164.</param>
    /// <param name="kind">Which of Meta's media message types to send.</param>
    /// <param name="metaMediaId">Meta's media identifier from an upload.</param>
    /// <param name="caption">Caption, where the kind supports one. Audio never does.</param>
    /// <param name="accessToken">The tenant's business token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Meta's message identifier.</returns>
    public Task<string> SendMediaAsync(
        string phoneNumberId,
        string recipient,
        Common.Constants.ContractEnums.ConversationMessageKind kind,
        string metaMediaId,
        string? caption,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>Uploads a file to Meta so a message can reference it.</summary>
    /// <param name="phoneNumberId">Sending number the file is uploaded against.</param>
    /// <param name="content">The bytes.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="mimeType">Media type.</param>
    /// <param name="accessToken">The tenant's business token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Meta's media identifier, which a send refers to.</returns>
    public Task<string> UploadMediaAsync(
        string phoneNumberId,
        Stream content,
        string fileName,
        string mimeType,
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a file a customer sent.
    /// </summary>
    /// <remarks>
    /// Two calls: Meta answers with a short-lived URL rather than the bytes, and that URL needs the
    /// same credential again. Both happen here so no caller has to know it takes two round trips.
    /// </remarks>
    /// <param name="mediaId">Meta's media identifier, from a webhook.</param>
    /// <param name="accessToken">The tenant's business token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<MetaMediaDownload> DownloadMediaAsync(
        string mediaId,
        string accessToken,
        CancellationToken cancellationToken = default);
}

/// <summary>A template's content, in the form Meta reviews.</summary>
/// <param name="Name">Lowercase name, unique per language on the account.</param>
/// <param name="Language">WhatsApp language code, for example <c>en_US</c>.</param>
/// <param name="Category"><c>MARKETING</c> or <c>UTILITY</c>.</param>
/// <param name="HeaderText">Text header, or null for none.</param>
/// <param name="BodyText">Body, with placeholders such as <c>{{1}}</c> verbatim.</param>
/// <param name="BodyExamples">One example value per body placeholder, in number order.</param>
/// <param name="FooterText">Footer, or null for none.</param>
/// <param name="Buttons">Buttons, in display order.</param>
public sealed record MetaTemplateDefinition(
    string Name,
    string Language,
    string Category,
    string? HeaderText,
    string BodyText,
    IReadOnlyList<string> BodyExamples,
    string? FooterText,
    IReadOnlyList<MetaTemplateButton> Buttons);

/// <summary>A template button, in the form Meta reviews.</summary>
/// <param name="Type"><c>QUICK_REPLY</c>, <c>URL</c> or <c>PHONE_NUMBER</c>.</param>
/// <param name="Text">Button label.</param>
/// <param name="Url">Web address, for a <c>URL</c> button.</param>
/// <param name="PhoneNumber">Phone number, for a <c>PHONE_NUMBER</c> button.</param>
public sealed record MetaTemplateButton(string Type, string Text, string? Url = null, string? PhoneNumber = null);

/// <summary>Meta's acknowledgement of a newly submitted template.</summary>
/// <param name="Id">Meta's id for the template.</param>
/// <param name="Status">Review status, usually <c>PENDING</c>.</param>
/// <param name="Category">The category Meta filed it under, which may differ from the one requested.</param>
public sealed record MetaTemplateSubmission(string Id, string? Status, string? Category);

/// <summary>A file fetched from Meta.</summary>
/// <param name="Content">The bytes.</param>
/// <param name="MimeType">Media type Meta reported.</param>
/// <param name="SizeBytes">Size in bytes.</param>
public sealed record MetaMediaDownload(byte[] Content, string MimeType, long SizeBytes);

/// <summary>A business account as Meta holds it.</summary>
/// <param name="Id">Account identifier.</param>
/// <param name="Name">Business name.</param>
/// <param name="TemplateNamespace">Namespace templates are published under.</param>
/// <param name="ReviewStatus">Meta's review status for the account - APPROVED, PENDING, REJECTED.</param>
public sealed record MetaBusinessAccount(
    string Id,
    string? Name,
    string? TemplateNamespace,
    string? ReviewStatus = null);

/// <summary>A business access token issued by Meta.</summary>
/// <param name="Value">The token. Encrypted before storage and never logged.</param>
/// <param name="ExpiresAtUtc">Expiry, or null for a long-lived system-user token.</param>
public sealed record MetaAccessToken(string Value, DateTimeOffset? ExpiresAtUtc);

/// <summary>A phone number registered against a WhatsApp Business Account.</summary>
/// <param name="Id">Phone number identifier.</param>
/// <param name="DisplayPhoneNumber">Number in international display format.</param>
/// <param name="VerifiedName">Business name Meta has verified.</param>
/// <param name="QualityRating">Meta's rating: green, yellow or red.</param>
/// <param name="MessagingTier">
/// Meta's daily unique-customer ceiling, reported as <c>TIER_1K</c> and similar. Null when Meta
/// does not return it, which the caller treats as "leave what we had" rather than as the lowest
/// tier — silently demoting a number on a missing field would misreport what it can send.
/// </param>
/// <param name="Status">Meta's status for the number - CONNECTED, FLAGGED, RESTRICTED.</param>
public sealed record MetaPhoneNumber(
    string Id,
    string DisplayPhoneNumber,
    string? VerifiedName,
    string? QualityRating,
    string? MessagingTier = null,
    string? Status = null);

/// <summary>A message template as Meta holds it.</summary>
/// <param name="Id">Meta's template identifier.</param>
/// <param name="Name">Template name.</param>
/// <param name="Language">BCP 47 language tag.</param>
/// <param name="Status">Review status.</param>
/// <param name="Category">Template category.</param>
public sealed record MetaTemplate(
    string Id,
    string Name,
    string Language,
    string Status,
    string Category);
