using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// One customer's claim that they have paid, awaiting a human decision.
/// <para>
/// There is no payment processor. Money moves out of band and a platform administrator decides, so
/// this row records an <em>intent</em> and a file — never an entitlement. The plan moves when the
/// request is approved and at no other point; a customer who can upload an image must not be able
/// to upgrade themselves.
/// </para>
/// </summary>
public sealed class PaymentRequest : BaseEntity, IRequiresTenant
{
    /// <summary>Plan the customer is buying.</summary>
    public long SubscriptionPlanId { get; set; }

    /// <summary>
    /// Plan name as it stood when the request was submitted.
    /// <para>
    /// Copied rather than joined, so the review queue and the audit trail keep saying what was
    /// bought even after the plan is renamed, repriced or archived.
    /// </para>
    /// </summary>
    public string PlanName { get; set; } = string.Empty;

    /// <summary>Period the customer is paying for.</summary>
    public BillingCycle BillingCycle { get; set; } = BillingCycle.Monthly;

    /// <summary>
    /// Amount owed, derived on the server from the plan and cycle.
    /// <para>
    /// Never accepted from the client. A customer-supplied amount is a customer-supplied invoice,
    /// and a reviewer skimming a queue will not catch a one-rupee submission.
    /// </para>
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>Currency of <see cref="Amount"/>, taken from the plan.</summary>
    public string Currency { get; set; } = "PKR";

    /// <summary>How the customer says they sent the money.</summary>
    public PaymentChannel Channel { get; set; }

    /// <summary>Transaction reference from the customer's receipt, when they gave one.</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>Free text from the customer.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>Key the uploaded proof is stored under.</summary>
    public required string ProofStorageKey { get; set; }

    /// <summary>Name the customer's file arrived under, shown to the reviewer.</summary>
    public string ProofFileName { get; set; } = string.Empty;

    /// <summary>Media type of the stored proof, used to serve it back.</summary>
    public string ProofContentType { get; set; } = string.Empty;

    /// <summary>Size of the uploaded proof.</summary>
    public long ProofSizeBytes { get; set; }

    /// <summary>Where the review has got to.</summary>
    public PaymentRequestStatus Status { get; set; } = PaymentRequestStatus.Pending;

    /// <summary>Organisation name at submission, denormalised for the platform queue.</summary>
    public string Organisation { get; set; } = string.Empty;

    /// <summary>User who submitted it.</summary>
    public long SubmittedByUserId { get; set; }

    /// <summary>Display name of the submitter, denormalised for the queue.</summary>
    public string SubmittedByName { get; set; } = string.Empty;

    /// <summary>Email of the submitter. The decision email goes here.</summary>
    public string SubmittedByEmail { get; set; } = string.Empty;

    /// <summary>Instant it was submitted.</summary>
    public DateTimeOffset SubmittedAt { get; set; }

    /// <summary>Instant it was decided, either way.</summary>
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>Platform administrator who decided it.</summary>
    public long? ReviewedByUserId { get; set; }

    /// <summary>Display name of that administrator, denormalised for the customer's view.</summary>
    public string? ReviewedByName { get; set; }

    /// <summary>
    /// Why it was refused. Non-null only when <see cref="Status"/> is
    /// <see cref="PaymentRequestStatus.Rejected"/>, because it is emailed to the customer verbatim
    /// and is the only thing telling them what to fix.
    /// </summary>
    public string? RejectionReason { get; set; }

    /// <summary>Invoice raised when the request was approved.</summary>
    public long? InvoiceId { get; set; }

    /// <summary>Plan navigation.</summary>
    public SubscriptionPlan SubscriptionPlan { get; set; } = null!;

    /// <summary>Invoice navigation, set when the request is approved.</summary>
    public Invoice? Invoice { get; set; }
}

/// <summary>
/// One place a customer can send money, and how to tell them to do it.
/// <para>
/// Held as data rather than compiled into the client, because account numbers change and a stale
/// one in a shipped bundle means money going to nobody — with a receipt to prove the customer sent
/// it.
/// </para>
/// </summary>
public sealed class PaymentChannelSetting : BaseEntity
{
    /// <summary>Which channel this configures. One row per channel.</summary>
    public PaymentChannel Channel { get; set; }

    /// <summary>Name shown on the payment method card.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Name the account is held in, so the customer can check it before sending.</summary>
    public string AccountTitle { get; set; } = string.Empty;

    /// <summary>Wallet number or bank account number.</summary>
    public string AccountNumber { get; set; } = string.Empty;

    /// <summary>Bank, for transfers. Null for a mobile wallet, which relabels the field.</summary>
    public string? BankName { get; set; }

    /// <summary>Key of the uploaded QR image, when one has been provided.</summary>
    public string? QrStorageKey { get; set; }

    /// <summary>Media type of the stored QR image.</summary>
    public string? QrContentType { get; set; }

    /// <summary>Step-by-step wording shown under the account details.</summary>
    public List<string> Instructions { get; set; } = [];

    /// <summary>
    /// Whether the channel is offered. False hides it without deleting the history of payments
    /// already made through it.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Order the channels are listed in.</summary>
    public int SortOrder { get; set; }
}
