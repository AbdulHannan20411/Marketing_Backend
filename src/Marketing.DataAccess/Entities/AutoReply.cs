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
}
