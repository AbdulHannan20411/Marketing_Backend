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
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<MetaPhoneNumber> GetPhoneNumberAsync(
        string phoneNumberId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every template on a WhatsApp Business Account, following pagination.</summary>
    /// <param name="wabaId">WhatsApp Business Account identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<MetaTemplate>> GetTemplatesAsync(
        string wabaId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one template message.
    /// </summary>
    /// <param name="phoneNumberId">Sending number.</param>
    /// <param name="recipient">Recipient in E.164.</param>
    /// <param name="templateName">Template name as registered with Meta.</param>
    /// <param name="languageCode">Template language tag.</param>
    /// <param name="bodyParameters">Ordered values filling the body placeholders.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Meta's message identifier, which later delivery receipts are keyed by.</returns>
    public Task<string> SendTemplateAsync(
        string phoneNumberId,
        string recipient,
        string templateName,
        string languageCode,
        IReadOnlyList<string> bodyParameters,
        CancellationToken cancellationToken = default);
}

/// <summary>A business access token issued by Meta.</summary>
/// <param name="Value">The token. Encrypted before storage and never logged.</param>
/// <param name="ExpiresAtUtc">Expiry, or null for a long-lived system-user token.</param>
public sealed record MetaAccessToken(string Value, DateTimeOffset? ExpiresAtUtc);

/// <summary>A phone number registered against a WhatsApp Business Account.</summary>
/// <param name="Id">Phone number identifier.</param>
/// <param name="DisplayPhoneNumber">Number in international display format.</param>
/// <param name="VerifiedName">Business name Meta has verified.</param>
/// <param name="QualityRating">Meta's rating: green, yellow or red.</param>
public sealed record MetaPhoneNumber(
    string Id,
    string DisplayPhoneNumber,
    string? VerifiedName,
    string? QualityRating);

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
