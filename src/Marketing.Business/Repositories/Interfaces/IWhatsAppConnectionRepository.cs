using Marketing.DataAccess.Entities;

namespace Marketing.Business.Repositories.Interfaces;

/// <summary>WhatsApp connection lookups that the tenant filter cannot serve.</summary>
public interface IWhatsAppConnectionRepository : IRepository<WhatsAppConnection>
{
    /// <summary>
    /// Finds the connection that owns a Meta phone number id.
    /// <para>
    /// The fourth and last deliberate bypass of the tenant filter in the codebase. Meta's webhook
    /// arrives unauthenticated with no tenant context at all - the phone number id in the payload
    /// <em>is</em> how the tenant is discovered. The request is only trusted because its
    /// HMAC-SHA256 signature has already been verified against the app secret.
    /// </para>
    /// </summary>
    /// <param name="phoneNumberId">Meta's phone number identifier from the webhook payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppConnection?> FindByPhoneNumberIdAsync(
        string phoneNumberId,
        CancellationToken cancellationToken = default);

    /// <summary>Loads the connection for a known tenant, bypassing the ambient tenant.</summary>
    /// <param name="tenantId">Tenant to load for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppConnection?> FindForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);
}
