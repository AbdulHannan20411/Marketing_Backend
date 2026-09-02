using Marketing.Infrastructure.WhatsApp.Models;
using Refit;

namespace Marketing.Infrastructure.WhatsApp.Clients;

/// <summary>
/// Strongly typed Meta WhatsApp Cloud API client.
/// <para>
/// Deliberately a thin transport contract - one method per Graph endpoint, no business rules,
/// no retry logic, no token management. Resilience is applied by the HTTP pipeline and access
/// tokens are attached by a delegating handler, so a call site cannot accidentally opt out of
/// either. Controllers never touch this type; only the WhatsApp services do.
/// </para>
/// </summary>
/// <remarks>
/// Every route starts with a slash because Refit requires it and refuses to build the client
/// otherwise. The base address therefore must <b>not</b> end with one: Refit joins the two by
/// concatenation, so a trailing slash there and a leading slash here produce a doubled separator.
/// The version segment lives on the base address, which is why it cannot simply be trimmed.
/// </remarks>
public interface IWhatsAppCloudApi
{
    /// <summary>Lists the phone numbers registered against a WhatsApp Business Account.</summary>
    /// <param name="wabaId">WhatsApp Business Account identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Get("/{wabaId}/phone_numbers")]
    public Task<GraphPage<WhatsAppPhoneNumber>> GetPhoneNumbersAsync(
        string wabaId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a single phone number's registration details.</summary>
    /// <param name="phoneNumberId">Phone number identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="authorization">
    /// Bearer token when the caller has one to hand, or null to let the handler supply the stored
    /// one. Onboarding passes it explicitly: at that point the token has only just been written and
    /// the caller already holds it.
    /// </param>
    [Get("/{phoneNumberId}")]
    public Task<WhatsAppPhoneNumber> GetPhoneNumberAsync(
        string phoneNumberId,
        [Header("Authorization")] string? authorization = null,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the message templates defined on a WhatsApp Business Account.</summary>
    /// <param name="wabaId">WhatsApp Business Account identifier.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="after">Cursor returned by a previous page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="authorization">
    /// Bearer token when the caller holds one, or null to use the stored one.
    /// </param>
    [Get("/{wabaId}/message_templates")]
    public Task<GraphPage<WhatsAppTemplate>> GetTemplatesAsync(
        string wabaId,
        [AliasAs("limit")] int limit = 100,
        [AliasAs("after")] string? after = null,
        [Header("Authorization")] string? authorization = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges an Embedded Signup authorisation code for a business access token.
    /// <para>
    /// The only call on this client that is not authenticated by a tenant token - it is
    /// authenticated by the app secret, and it is how a tenant gets a token in the first place.
    /// </para>
    /// </summary>
    /// <param name="clientId">Meta app identifier.</param>
    /// <param name="clientSecret">Meta app secret.</param>
    /// <param name="code">Code returned by Embedded Signup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Get("/oauth/access_token")]
    public Task<TokenExchangeResponse> ExchangeCodeAsync(
        [AliasAs("client_id")] string clientId,
        [AliasAs("client_secret")] string clientSecret,
        [AliasAs("code")] string code,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes this app to a business account's webhooks.
    /// </summary>
    /// <remarks>
    /// Without this call Meta sends no webhooks at all for the account — no inbound messages, no
    /// delivery receipts, no template verdicts. It is the most commonly missed step in onboarding,
    /// and its symptom is silence rather than an error.
    /// </remarks>
    /// <param name="wabaId">WhatsApp Business Account identifier.</param>
    /// <param name="authorization">Bearer token, supplied explicitly during onboarding.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Post("/{wabaId}/subscribed_apps")]
    public Task<GraphSuccess> SubscribeAppAsync(
        string wabaId,
        [Header("Authorization")] string authorization,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a phone number for Cloud API sending.
    /// </summary>
    /// <remarks>
    /// Required before the number can send anything. The PIN is two-factor material the caller
    /// generates and stores; re-registering later needs the same value.
    /// </remarks>
    /// <param name="phoneNumberId">Phone number identifier.</param>
    /// <param name="payload">Registration body carrying the messaging product and the PIN.</param>
    /// <param name="authorization">Bearer token, supplied explicitly during onboarding.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Post("/{phoneNumberId}/register")]
    public Task<GraphSuccess> RegisterPhoneNumberAsync(
        string phoneNumberId,
        [Body] object payload,
        [Header("Authorization")] string authorization,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a business account, for its name and messaging tier.</summary>
    /// <param name="wabaId">WhatsApp Business Account identifier.</param>
    /// <param name="fields">Fields to return.</param>
    /// <param name="authorization">Bearer token, supplied explicitly during onboarding.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Get("/{wabaId}")]
    public Task<WhatsAppBusinessAccount> GetBusinessAccountAsync(
        string wabaId,
        [Header("Authorization")] string authorization,
        [AliasAs("fields")] string fields = "id,name,message_template_namespace,account_review_status",
        CancellationToken cancellationToken = default);

    /// <summary>Uploads media and returns the handle a send refers to.</summary>
    /// <param name="phoneNumberId">Phone number the media belongs to.</param>
    /// <param name="file">The bytes, as a multipart part.</param>
    /// <param name="messagingProduct">Always <c>whatsapp</c>.</param>
    /// <param name="type">Media MIME type.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Multipart]
    [Post("/{phoneNumberId}/media")]
    public Task<MediaUploadResponse> UploadMediaAsync(
        string phoneNumberId,
        StreamPart file,
        [AliasAs("messaging_product")] string messagingProduct,
        [AliasAs("type")] string type,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves a media id to the short-lived URL its bytes can be read from.</summary>
    /// <param name="mediaId">Media identifier from a webhook or an upload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Get("/{mediaId}")]
    public Task<MediaHandle> GetMediaAsync(
        string mediaId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a message template and submits it to Meta for review.</summary>
    /// <param name="wabaId">WhatsApp Business Account identifier.</param>
    /// <param name="payload">Template definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Post("/{wabaId}/message_templates")]
    public Task<TemplateMutationResponse> CreateTemplateAsync(
        string wabaId,
        [Body] object payload,
        CancellationToken cancellationToken = default);

    /// <summary>Edits a rejected template, which resubmits it.</summary>
    /// <param name="metaTemplateId">Meta's identifier for the template.</param>
    /// <param name="payload">Fields to change.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Post("/{metaTemplateId}")]
    public Task<GraphSuccess> UpdateTemplateAsync(
        string metaTemplateId,
        [Body] object payload,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a message.</summary>
    /// <param name="phoneNumberId">Sending phone number identifier.</param>
    /// <param name="payload">
    /// Graph message body. Left untyped at this layer because the shape varies substantially by
    /// message type; the campaign services build and validate it before it reaches here.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="authorization">
    /// Bearer token when the caller holds one, or null to use the stored one. Background jobs pass
    /// it explicitly: they run with no signed-in user, and the handler that looks the token up
    /// resolves the tenant in its own scope, which a job's scope does not reach.
    /// </param>
    [Post("/{phoneNumberId}/messages")]
    public Task<SendMessageResponse> SendMessageAsync(
        string phoneNumberId,
        [Body] object payload,
        [Header("Authorization")] string? authorization = null,
        CancellationToken cancellationToken = default);
}
