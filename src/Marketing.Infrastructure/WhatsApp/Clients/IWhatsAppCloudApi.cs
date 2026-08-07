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
    [Get("/{phoneNumberId}")]
    public Task<WhatsAppPhoneNumber> GetPhoneNumberAsync(
        string phoneNumberId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the message templates defined on a WhatsApp Business Account.</summary>
    /// <param name="wabaId">WhatsApp Business Account identifier.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="after">Cursor returned by a previous page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Get("/{wabaId}/message_templates")]
    public Task<GraphPage<WhatsAppTemplate>> GetTemplatesAsync(
        string wabaId,
        [AliasAs("limit")] int limit = 100,
        [AliasAs("after")] string? after = null,
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

    /// <summary>Sends a message.</summary>
    /// <param name="phoneNumberId">Sending phone number identifier.</param>
    /// <param name="payload">
    /// Graph message body. Left untyped at this layer because the shape varies substantially by
    /// message type; the campaign services build and validate it before it reaches here.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Post("/{phoneNumberId}/messages")]
    public Task<SendMessageResponse> SendMessageAsync(
        string phoneNumberId,
        [Body] object payload,
        CancellationToken cancellationToken = default);
}
