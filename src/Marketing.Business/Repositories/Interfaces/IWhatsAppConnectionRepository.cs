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
        long tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>Every number in a workspace, the default first.</summary>
    /// <param name="tenantId">Workspace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<WhatsAppConnection>> FindAllForTenantAsync(
        long tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>One number, only if it belongs to the workspace.</summary>
    /// <param name="tenantId">Workspace it must belong to.</param>
    /// <param name="connectionId">The number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppConnection?> FindByIdForTenantAsync(
        long tenantId,
        long connectionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether any other live connection, in any workspace, still uses a business account.
    /// </summary>
    /// <remarks>
    /// Webhook subscriptions are per business account, not per number, so unsubscribing when one of
    /// two numbers on it disconnects would silence the other.
    /// </remarks>
    /// <param name="wabaId">Business account.</param>
    /// <param name="excludingId">The connection being disconnected.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<bool> IsWabaInUseElsewhereAsync(
        string wabaId,
        long excludingId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What a workspace calls each of its numbers, keyed by connection, deleted numbers included.
    /// </summary>
    /// <remarks>
    /// Deleted ones too because conversations and campaigns outlive the number they used, and a
    /// thread labelled with nothing reads as data loss.
    /// </remarks>
    /// <param name="tenantId">Workspace.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyDictionary<long, string>> LabelsForTenantAsync(
        long tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The number a campaign or conversation was written against, or the workspace default when it
    /// predates multiple numbers and names none.
    /// </summary>
    /// <param name="tenantId">Workspace.</param>
    /// <param name="connectionId">Connection the record names, if any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<WhatsAppConnection?> FindForRecordAsync(
        long tenantId,
        long? connectionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns tenant ids whose connection is still part-way through onboarding.
    /// </summary>
    /// <remarks>
    /// Spans tenants because the poller runs with no signed-in user; only the identifiers are
    /// returned, and the caller enters each tenant's scope before touching anything else, so no
    /// row crosses a tenant boundary.
    /// </remarks>
    /// <param name="limit">Most tenants to return in one poll.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<IReadOnlyList<long>> FindTenantsAwaitingOnboardingAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
