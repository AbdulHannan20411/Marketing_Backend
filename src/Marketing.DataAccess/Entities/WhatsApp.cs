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

    /// <summary>Instant the connection was established.</summary>
    public DateTimeOffset? ConnectedAt { get; set; }

    /// <summary>Whether Meta's webhook is delivering.</summary>
    public bool WebhookHealthy { get; set; }

    /// <summary>Template namespace alias.</summary>
    public string TemplateNamespaceAlias { get; set; } = string.Empty;

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
