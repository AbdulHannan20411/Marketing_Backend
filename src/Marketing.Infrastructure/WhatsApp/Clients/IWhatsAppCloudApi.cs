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
    Task<GraphCollection<WhatsAppPhoneNumber>> GetPhoneNumbersAsync(
        string wabaId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a single phone number's registration details.</summary>
    /// <param name="phoneNumberId">Phone number identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Get("/{phoneNumberId}")]
    Task<WhatsAppPhoneNumber> GetPhoneNumberAsync(
        string phoneNumberId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the message templates defined on a WhatsApp Business Account.</summary>
    /// <param name="wabaId">WhatsApp Business Account identifier.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="after">Cursor returned by a previous page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Get("/{wabaId}/message_templates")]
    Task<GraphCollection<WhatsAppTemplate>> GetTemplatesAsync(
        string wabaId,
        [AliasAs("limit")] int limit = 100,
        [AliasAs("after")] string? after = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a message.</summary>
    /// <param name="phoneNumberId">Sending phone number identifier.</param>
    /// <param name="payload">
    /// Graph message body. Left untyped at this layer because the shape varies substantially by
    /// message type; the campaign services build and validate it before it reaches here.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [Post("/{phoneNumberId}/messages")]
    Task<SendMessageResponse> SendMessageAsync(
        string phoneNumberId,
        [Body] object payload,
        CancellationToken cancellationToken = default);
}
