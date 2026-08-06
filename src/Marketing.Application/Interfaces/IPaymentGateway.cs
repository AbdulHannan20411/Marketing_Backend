namespace Marketing.Application.Interfaces;

/// <summary>
/// Charges a customer.
/// <para>
/// A seam, not an abstraction for its own sake. Everything around it - subscriptions, invoices,
/// payment records, the billing history screen - is real and works today against a manual
/// implementation that records an offline payment. Introducing a provider later replaces one
/// class and touches nothing in the billing module.
/// </para>
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Name of the provider, recorded against payments for support and reconciliation.</summary>
    public string ProviderName { get; }

    /// <summary>
    /// Attempts to charge for an invoice.
    /// <para>
    /// Implementations must be idempotent on <paramref name="idempotencyKey"/>: a retried request
    /// must not charge twice. The client sends it as the <c>Idempotency-Key</c> header.
    /// </para>
    /// </summary>
    /// <param name="request">What to charge and against what.</param>
    /// <param name="idempotencyKey">Caller-supplied key that makes a retry safe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<PaymentAttemptResult> ChargeAsync(
        PaymentAttempt request,
        string? idempotencyKey,
        CancellationToken cancellationToken = default);
}

/// <summary>A charge to attempt.</summary>
/// <param name="InvoiceNumber">Invoice being settled.</param>
/// <param name="Amount">Gross amount, in major units.</param>
/// <param name="Currency">ISO 4217 currency code.</param>
/// <param name="Description">Human-readable description for the customer's statement.</param>
public sealed record PaymentAttempt(
    string InvoiceNumber,
    decimal Amount,
    string Currency,
    string Description);

/// <summary>Outcome of a charge.</summary>
/// <param name="Succeeded">Whether the money moved.</param>
/// <param name="ProviderReference">Provider's own identifier, for reconciliation.</param>
/// <param name="CardBrand">Card brand, when the provider used one.</param>
/// <param name="CardLast4">Last four digits only; never a full card number.</param>
/// <param name="FailureReason">Why it failed, when it did.</param>
public sealed record PaymentAttemptResult(
    bool Succeeded,
    string? ProviderReference,
    string? CardBrand,
    string? CardLast4,
    string? FailureReason)
{
    /// <summary>A successful charge.</summary>
    public static PaymentAttemptResult Success(string reference, string? cardBrand = null, string? cardLast4 = null) =>
        new(true, reference, cardBrand, cardLast4, null);

    /// <summary>A declined or errored charge.</summary>
    public static PaymentAttemptResult Failure(string reason) => new(false, null, null, null, reason);
}
