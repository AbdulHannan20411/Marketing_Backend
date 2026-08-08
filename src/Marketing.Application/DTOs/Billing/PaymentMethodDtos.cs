using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Billing;

/// <summary>A stored means of payment, as the client renders it.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>pm_</c>.</param>
/// <param name="Kind">Instrument type.</param>
/// <param name="Brand">Card network or bank name.</param>
/// <param name="Last4">Last four digits. Display only; the full number is never held.</param>
/// <param name="ExpiryMonth">Expiry month.</param>
/// <param name="ExpiryYear">Expiry year.</param>
/// <param name="IsDefault">Whether renewals and retries charge this one.</param>
/// <param name="CreatedAt">Instant it was added.</param>
public sealed record PaymentMethodResponse(
    string Id,
    PaymentMethodKind Kind,
    string? Brand,
    string? Last4,
    int? ExpiryMonth,
    int? ExpiryYear,
    bool IsDefault,
    DateTimeOffset CreatedAt);

/// <summary>
/// Request to store a means of payment.
/// <para>
/// Takes a processor token, never card details. The browser tokenises the card directly with the
/// processor, so a real number never reaches this API and the platform stays outside PCI scope.
/// Any request carrying something that looks like a card number is refused.
/// </para>
/// </summary>
/// <param name="ProviderToken">Token returned by the processor's client-side tokenisation.</param>
/// <param name="Kind">Instrument type.</param>
/// <param name="MakeDefault">Whether this becomes the instrument renewals charge.</param>
public sealed record AddPaymentMethodRequest(
    string ProviderToken,
    PaymentMethodKind Kind = PaymentMethodKind.Card,
    bool MakeDefault = true);

/// <summary>The company details that appear on an invoice.</summary>
/// <param name="CompanyName">Legal entity name.</param>
/// <param name="AddressLine1">First address line.</param>
/// <param name="AddressLine2">Second address line.</param>
/// <param name="City">Town or city.</param>
/// <param name="Region">Region, state or county.</param>
/// <param name="PostalCode">Postal or ZIP code.</param>
/// <param name="Country">Country name.</param>
/// <param name="TaxId">VAT or tax registration identifier.</param>
/// <param name="BillingEmail">Address invoices are sent to.</param>
public sealed record BillingProfileResponse(
    string CompanyName,
    string AddressLine1,
    string AddressLine2,
    string City,
    string Region,
    string PostalCode,
    string Country,
    string TaxId,
    string BillingEmail);

/// <summary>Request to replace the billing profile. Omitted fields are left unchanged.</summary>
/// <param name="CompanyName">Legal entity name.</param>
/// <param name="AddressLine1">First address line.</param>
/// <param name="AddressLine2">Second address line.</param>
/// <param name="City">Town or city.</param>
/// <param name="Region">Region, state or county.</param>
/// <param name="PostalCode">Postal or ZIP code.</param>
/// <param name="Country">Country code or name.</param>
/// <param name="TaxId">VAT or tax registration identifier.</param>
/// <param name="BillingEmail">Address invoices are sent to.</param>
public sealed record UpdateBillingProfileRequest(
    string? CompanyName = null,
    string? AddressLine1 = null,
    string? AddressLine2 = null,
    string? City = null,
    string? Region = null,
    string? PostalCode = null,
    string? Country = null,
    string? TaxId = null,
    string? BillingEmail = null);
