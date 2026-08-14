using System.Text.Json.Serialization;
using Marketing.Common.Requests;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Payments;

/// <summary>
/// Where a customer can send money, and how they are told to.
/// </summary>
/// <param name="Channel">Which channel this describes.</param>
/// <param name="DisplayName">Name shown on the payment method card.</param>
/// <param name="AccountTitle">Name the account is held in.</param>
/// <param name="AccountNumber">Wallet number or bank account number.</param>
/// <param name="BankName">Bank, for transfers. Null for a wallet, which relabels the field.</param>
/// <param name="QrImageUrl">
/// Where to fetch the QR code. An API path, an absolute URL or a <c>data:</c> URI — the client
/// handles all three, and an API path is fetched with the bearer token.
/// </param>
/// <param name="Instructions">Step-by-step wording shown under the account details.</param>
/// <param name="IsActive">Whether the channel is offered.</param>
public sealed record PaymentChannelDetails(
    PaymentChannel Channel,
    string DisplayName,
    string AccountTitle,
    string AccountNumber,
    string? BankName,
    string QrImageUrl,
    IReadOnlyList<string> Instructions,
    bool IsActive);

/// <summary>
/// One submitted payment, as both the customer and the reviewer see it.
/// </summary>
/// <remarks>
/// <paramref name="BillingCycle"/> is a string rather than the shared <c>BillingCycle</c> enum on
/// purpose. This module's contract specifies PascalCase, and that enum crosses the wire camelCase
/// everywhere else in the API; giving it a PascalCase converter would silently change every other
/// billing endpoint. The mapping is done once, in the service.
/// </remarks>
/// <param name="Id">Opaque identifier, prefixed <c>pyr_</c>.</param>
/// <param name="Status">Where the review has got to.</param>
/// <param name="PlanId">Plan being bought.</param>
/// <param name="PlanName">Its name at submission.</param>
/// <param name="BillingCycle"><c>Monthly</c> or <c>Yearly</c>.</param>
/// <param name="Amount">Server-derived amount owed.</param>
/// <param name="Currency">Currency of the amount.</param>
/// <param name="Channel">How the customer says they paid.</param>
/// <param name="Reference">Transaction reference from their receipt.</param>
/// <param name="Note">Free text from the customer.</param>
/// <param name="ProofUrl">Path the uploaded proof is fetched from, with the bearer token.</param>
/// <param name="ProofFileName">Name the file arrived under.</param>
/// <param name="ProofContentType">Media type of the stored proof.</param>
/// <param name="Organisation">Workspace that submitted it.</param>
/// <param name="SubmittedByName">Who submitted it.</param>
/// <param name="SubmittedByEmail">Their email; decision mail goes here.</param>
/// <param name="AdminId">Admin account of the submitting workspace, for platform scoping.</param>
/// <param name="SubmittedAt">Instant it was submitted.</param>
/// <param name="ReviewedAt">Instant it was decided.</param>
/// <param name="ReviewedBy">Administrator who decided it.</param>
/// <param name="RejectionReason">Why it was refused. Non-null only when rejected.</param>
public sealed record PaymentRequestResponse(
    string Id,
    PaymentRequestStatus Status,
    string PlanId,
    string PlanName,
    string BillingCycle,
    decimal Amount,
    string Currency,
    PaymentChannel Channel,
    string Reference,
    string Note,
    string ProofUrl,
    string ProofFileName,
    string ProofContentType,
    string Organisation,
    string SubmittedByName,
    string SubmittedByEmail,
    string? AdminId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ReviewedAt,
    string? ReviewedBy,
    string? RejectionReason);

/// <summary>Query parameters for the platform review queue.</summary>
public sealed class PaymentRequestQuery : PageRequest
{
    /// <summary>Sentinel meaning "do not filter".</summary>
    public const string All = "all";

    /// <summary>Status to filter by, or <c>all</c>.</summary>
    public string Status { get; init; } = All;
}

/// <summary>The reason a payment was refused.</summary>
/// <param name="Reason">
/// Emailed to the customer verbatim, so it has to say what to fix. Required, and at least ten
/// characters — the client enforces the same rule, and the server enforces it because the client's
/// copy is advisory.
/// </param>
public sealed record RejectPaymentRequest(string Reason);

/// <summary>Pushed over the realtime hub on submission and on every decision.</summary>
/// <param name="RequestId">Opaque request identifier.</param>
/// <param name="Status">Where the review has got to.</param>
/// <param name="PlanName">Plan the request concerns.</param>
/// <param name="Organisation">Workspace that submitted it.</param>
/// <param name="RejectionReason">Why it was refused, when it was.</param>
public sealed record PaymentRequestEvent(
    string RequestId,
    PaymentRequestStatus Status,
    string PlanName,
    string Organisation,
    string? RejectionReason);

/// <summary>
/// The enum converters this contract needs, for the host to register.
/// </summary>
/// <remarks>
/// The <c>[JsonConverter]</c> attributes on the enums are not enough on their own: a converter in
/// <c>JsonSerializerOptions.Converters</c> beats a type-level attribute, and this API registers a
/// global camelCase enum converter. Registered <b>before</b> that one, because the first match wins.
/// </remarks>
public static class PaymentContractJson
{
    /// <summary>Converters that keep the manual-payment enums PascalCase on the wire.</summary>
    public static IReadOnlyList<JsonConverter> Converters { get; } =
    [
        new JsonStringEnumConverter<PaymentChannel>(),
        new JsonStringEnumConverter<PaymentRequestStatus>(),
    ];
}
