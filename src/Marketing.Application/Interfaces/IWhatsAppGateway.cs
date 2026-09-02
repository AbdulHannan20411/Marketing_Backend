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
}

/// <summary>A business account as Meta holds it.</summary>
/// <param name="Id">Account identifier.</param>
/// <param name="Name">Business name.</param>
/// <param name="TemplateNamespace">Namespace templates are published under.</param>
public sealed record MetaBusinessAccount(string Id, string? Name, string? TemplateNamespace);

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
public sealed record MetaPhoneNumber(
    string Id,
    string DisplayPhoneNumber,
    string? VerifiedName,
    string? QualityRating,
    string? MessagingTier = null);

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
