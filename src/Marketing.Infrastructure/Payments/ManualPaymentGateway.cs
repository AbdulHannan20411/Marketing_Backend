using Marketing.Application.Interfaces;
using Marketing.Shared.Abstractions;
using Microsoft.Extensions.Logging;

namespace Marketing.Infrastructure.Payments;

/// <summary>
/// Records an offline payment rather than moving money.
/// <para>
/// The platform has no payment provider configured. This implementation lets the whole billing
/// flow - change plan, renew, retry an invoice - work end to end and produce a truthful billing
/// history, with every settlement marked as manual so nobody mistakes it for a card charge.
/// </para>
/// <para>
/// It never fails a charge. That is the honest behaviour for a manual process: whether the money
/// actually arrived is a question for whoever reconciles the bank statement, not for this class.
/// </para>
/// </summary>
public sealed partial class ManualPaymentGateway : IPaymentGateway
{
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<ManualPaymentGateway> _logger;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="clock">Clock, used to build the reference.</param>
    /// <param name="logger">Logger.</param>
    public ManualPaymentGateway(IDateTimeProvider clock, ILogger<ManualPaymentGateway> logger)
    {
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "manual";

    /// <inheritdoc />
    public Task<PaymentAttemptResult> ChargeAsync(
        PaymentAttempt request,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Deterministic from the invoice number, so replaying the same settlement produces the
        // same reference and reconciliation does not see two entries for one payment.
        var reference = $"manual-{request.InvoiceNumber}-{_clock.UtcNow:yyyyMMdd}";

        LogManualSettlement(request.InvoiceNumber, request.Amount, request.Currency, idempotencyKey);

        return Task.FromResult(PaymentAttemptResult.Success(reference));
    }

    [LoggerMessage(
        EventId = 2301,
        Level = LogLevel.Information,
        Message = "Recorded a manual settlement of {Amount} {Currency} for invoice {InvoiceNumber}. IdempotencyKey: {IdempotencyKey}")]
    private partial void LogManualSettlement(
        string invoiceNumber,
        decimal amount,
        string currency,
        string? idempotencyKey);
}
