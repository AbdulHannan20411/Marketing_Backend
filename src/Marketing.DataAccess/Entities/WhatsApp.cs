using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.DataAccess.Entities;

/// <summary>
/// A tenant's link to a Meta WhatsApp Business Account.
/// <para>
/// One per tenant today. Modelled as its own row rather than columns on the tenant because the
/// plan limits already contemplate more than one connected account, and because the access token
/// lives here and deserves to be isolated.
/// </para>
/// </summary>
public sealed class WhatsAppConnection : BaseEntity, IRequiresTenant
{
    /// <summary>Connection state.</summary>
    public ConnectionStatus Status { get; set; } = ConnectionStatus.Disconnected;

    /// <summary>WhatsApp Business Account identifier at Meta.</summary>
    public string? WabaId { get; set; }

    /// <summary>Phone number identifier used when sending.</summary>
    public string? PhoneNumberId { get; set; }

    /// <summary>Number in international display format.</summary>
    public string DisplayPhoneNumber { get; set; } = string.Empty;

    /// <summary>Business name Meta has verified.</summary>
    public string VerifiedName { get; set; } = string.Empty;

    /// <summary>Business profile "about" text.</summary>
    public string BusinessProfileAbout { get; set; } = string.Empty;

    /// <summary>Business category.</summary>
    public string BusinessCategory { get; set; } = string.Empty;

    /// <summary>Meta's current quality rating.</summary>
    public QualityRating QualityRating { get; set; } = QualityRating.Green;

    /// <summary>Rolling 24-hour messaging ceiling.</summary>
    public int MessagingLimit { get; set; }

    /// <summary>Messages sent in the rolling 24-hour window.</summary>
    public int MessagesLast24h { get; set; }

    /// <summary>
    /// Meta's daily ceiling on unique customers this number may start conversations with.
    /// </summary>
    /// <remarks>
    /// Read from Meta on connect and refreshed by the account-update webhook. Defaults to the
    /// lowest tier, which is where an unverified business genuinely starts — assuming anything
    /// higher would let the platform promise a send volume Meta will refuse.
    /// </remarks>
    public MessagingTier MessagingTier { get; set; } = MessagingTier.Tier250;

    /// <summary>Instant the connection was established.</summary>
    public DateTimeOffset? ConnectedAt { get; set; }

    /// <summary>Whether Meta's webhook is delivering.</summary>
    public bool WebhookHealthy { get; set; }

    /// <summary>Template namespace alias.</summary>
    public string TemplateNamespaceAlias { get; set; } = string.Empty;

    /// <summary>
    /// Six-digit PIN this number was registered with.
    /// </summary>
    /// <remarks>
    /// Two-factor material for the customer's number: Meta asks for the same value if the number is
    /// ever re-registered, so it is kept rather than regenerated. Never returned in a DTO, for the
    /// same reason the access token is not.
    /// </remarks>
    public string? RegistrationPin { get; set; }

    /// <summary>
    /// Encrypted system-user access token.
    /// <para>
    /// Never leaves this layer and never appears in a DTO. Stored encrypted at rest; the column is
    /// excluded from the audit trail for the same reason password hashes are.
    /// </para>
    /// </summary>
    public string? EncryptedAccessToken { get; set; }

    /// <summary>Instant the access token expires.</summary>
    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>
    /// Per-step progress of the connection attempt, in the order the steps run.
    /// </summary>
    /// <remarks>
    /// Persisted rather than held in memory because the work outlives the request that started it:
    /// the browser is polling a different process by the time the later steps run, and a restart
    /// mid-onboarding must not lose what already succeeded.
    /// </remarks>
    public List<WhatsAppOnboardingStep> OnboardingSteps { get; set; } = [];
}

/// <summary>One stage of connecting a WhatsApp account, and how it went.</summary>
/// <remarks>
/// Stored as JSON on the connection rather than as its own table. The list is short, fixed, always
/// read whole and never queried across connections, which is the shape JSON suits; a table would
/// add a join and a migration for every new step.
/// </remarks>
public sealed class WhatsAppOnboardingStep
{
    /// <summary>Which stage this is.</summary>
    public OnboardingStep Step { get; set; }

    /// <summary>How it went.</summary>
    public OnboardingStepStatus Status { get; set; }

    /// <summary>
    /// Stable machine-readable cause when the step failed.
    /// </summary>
    /// <remarks>
    /// A code rather than a sentence, because the remedy is the client's to word and to translate.
    /// Shipping prose from here would put user-facing copy in the database, where it cannot be
    /// changed without a deployment.
    /// </remarks>
    public string? Code { get; set; }

    /// <summary>What went wrong, for an operator reading a log or a support ticket.</summary>
    public string? Message { get; set; }

    /// <summary>Instant the step reached its final state.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>A message template synchronised from Meta.</summary>
public sealed class MessageTemplate : BaseEntity, IRequiresTenant
{
    /// <summary>Meta's template identifier.</summary>
    public string? MetaTemplateId { get; set; }

    /// <summary>Template name.</summary>
    public required string Name { get; set; }

    /// <summary>Category, which determines pricing and consent rules.</summary>
    public TemplateCategory Category { get; set; } = TemplateCategory.Utility;

    /// <summary>Meta review status.</summary>
    public TemplateStatus Status { get; set; } = TemplateStatus.Pending;

    /// <summary>BCP 47 language tag, for example <c>en_GB</c>.</summary>
    public string Language { get; set; } = "en_GB";

    /// <summary>Optional header text.</summary>
    public string? HeaderText { get; set; }

    /// <summary>Body text. Placeholders such as <c>{{1}}</c> are preserved verbatim.</summary>
    public required string BodyText { get; set; }

    /// <summary>Optional footer text.</summary>
    public string? FooterText { get; set; }

    /// <summary>Ordered variable names; the first fills <c>{{1}}</c>.</summary>
    public List<string> Variables { get; set; } = [];

    /// <summary>Button labels.</summary>
    public List<string> Buttons { get; set; } = [];

    /// <summary>Meta's quality score for this template.</summary>
    public QualityRating QualityScore { get; set; } = QualityRating.Green;

    /// <summary>How many campaigns have used it.</summary>
    public int TimesUsed { get; set; }

    /// <summary>Why Meta rejected it, when it did.</summary>
    public string? RejectionReason { get; set; }
}
