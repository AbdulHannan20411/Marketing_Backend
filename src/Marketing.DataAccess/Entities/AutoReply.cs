namespace Marketing.DataAccess.Entities;

/// <summary>
/// One workspace's rules for answering customers automatically.
/// </summary>
/// <remarks>
/// One row per tenant, created on first use with everything switched off. Which triggers may be
/// switched on at all is the plan's business, not this row's: an admin can enable what they bought,
/// and a row that was configured on a richer plan simply stops firing the triggers the plan no longer
/// includes rather than losing its settings.
/// </remarks>
public sealed class AutoReplySettings : BaseEntity, IRequiresTenant
{
    /// <summary>Master switch. Off means nothing is ever sent automatically.</summary>
    public bool Enabled { get; set; }

    /// <summary>Answer a message that is only a greeting.</summary>
    public bool GreetingEnabled { get; set; }

    /// <summary>Answer the first message of a new conversation, whatever it says.</summary>
    public bool FirstMessageEnabled { get; set; }

    /// <summary>Answer anything nobody has replied to within <see cref="UnansweredAfterMinutes"/>.</summary>
    public bool UnansweredEnabled { get; set; }

    /// <summary>
    /// How long to wait before a greeting or first-message reply goes out, in seconds.
    /// </summary>
    /// <remarks>
    /// A pause, not a delay for its own sake: an instant answer reads as a robot, and it also denies
    /// an agent who is already typing the chance to answer first. Ten seconds to thirty minutes.
    /// </remarks>
    public int DelaySeconds { get; set; } = 60;

    /// <summary>How long a message may go unanswered before the AI steps in, in minutes.</summary>
    public int UnansweredAfterMinutes { get; set; } = 300;

    /// <summary>
    /// What the business wants the AI to sound like, and anything it must or must not say.
    /// </summary>
    /// <remarks>
    /// Free text, handed to the model with the conversation. This is where a business says "we are a
    /// salon in Lahore, opening hours are 11 to 9, never quote prices" - which is the difference
    /// between a useful answer and a confident wrong one.
    /// </remarks>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>
    /// Most automatic replies one conversation may receive in a day.
    /// </summary>
    /// <remarks>
    /// A backstop against a customer and a model talking to each other. Nothing in the trigger rules
    /// can loop on its own - every reply needs a new inbound message - but a person replying "ok" to
    /// each answer would otherwise be answered every time.
    /// </remarks>
    public int MaxPerConversationPerDay { get; set; } = 3;

    /// <summary>
    /// Billing period end the "allowance spent" notice was already sent for.
    /// </summary>
    /// <remarks>
    /// Stops the notice repeating every ten seconds once the allowance runs out, and lets it be sent
    /// again next period without a separate table to remember that it was.
    /// </remarks>
    public DateTimeOffset? QuotaNoticeSentForPeriodEnd { get; set; }

    /// <summary>
    /// What to do when the knowledge file does not answer the question: <c>handoff</c> sends
    /// <see cref="KnowledgeFallbackMessage"/>, <c>silent</c> sends nothing.
    /// </summary>
    public string KnowledgeFallback { get; set; } = "handoff";

    /// <summary>The holding message sent on a handoff. Kept when the fallback is silent.</summary>
    public string KnowledgeFallbackMessage { get; set; } =
        "Thanks for your message! Someone from our team will get back to you shortly.";

    /// <summary>Name of the spreadsheet last uploaded, for display only.</summary>
    public string? KnowledgeSourceFileName { get; set; }

    /// <summary>When the knowledge was last replaced. Null if it never has been.</summary>
    public DateTimeOffset? KnowledgeUpdatedAt { get; set; }

    /// <summary>Who last replaced it.</summary>
    public long? KnowledgeUpdatedByUserId { get; set; }
}

/// <summary>
/// One row of the workspace's knowledge file: a fact, a question and answer, a product, a policy or a
/// rule the automatic replies must follow.
/// </summary>
/// <remarks>
/// Replaced wholesale on every upload - the spreadsheet is the single source of truth - so rows are
/// hard-deleted rather than soft-deleted, and are excluded from the per-row audit trail. One upload
/// is recorded as a single audit entry instead of five hundred.
/// </remarks>
public sealed class AutoReplyKnowledgeEntry : BaseEntity, IRequiresTenant
{
    /// <summary>Position in the upload; entries are returned in this order.</summary>
    public int SortOrder { get; set; }

    /// <summary><c>business</c>, <c>faq</c>, <c>product</c>, <c>policy</c> or <c>rule</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>What the fact is, the question, the product name, the policy name or the rule.</summary>
    public required string Title { get; set; }

    /// <summary>The fact, answer, description or policy. May be empty only for a rule.</summary>
    public string Answer { get; set; } = string.Empty;

    /// <summary>Price exactly as written. Products only.</summary>
    public string? Price { get; set; }

    /// <summary>Whether it can be ordered now. Products only; null when not stated.</summary>
    public bool? Available { get; set; }

    /// <summary>Other ways customers ask for it.</summary>
    public List<string> Keywords { get; set; } = [];
}

/// <summary>
/// What happened when an automatic reply was attempted from the knowledge file.
/// </summary>
/// <remarks>
/// Two jobs. It stops the dispatcher asking the model the same unanswerable question on every run,
/// and it is the list of questions the file did not cover - what an admin should add next.
/// </remarks>
public sealed class AutoReplyAttempt : BaseEntity, IRequiresTenant
{
    /// <summary>The conversation.</summary>
    public long ConversationId { get; set; }

    /// <summary>When the customer message being answered arrived.</summary>
    public DateTimeOffset InboundMessageAt { get; set; }

    /// <summary>Which trigger fired.</summary>
    public required string Trigger { get; set; }

    /// <summary><c>answered</c>, <c>unknown</c> or <c>rejected</c> (a reply that failed the output checks).</summary>
    public required string Outcome { get; set; }

    /// <summary>Whether the handoff message was sent for it.</summary>
    public bool FallbackSent { get; set; }

    /// <summary>The customer's question, shortened, so a report can show what to add to the file.</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>When the attempt was made.</summary>
    public DateTimeOffset AttemptedAt { get; set; }
}
