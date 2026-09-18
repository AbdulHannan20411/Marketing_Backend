namespace Marketing.Application.DTOs.WhatsApp;

/// <summary>A workspace's automatic-reply rules, and what its plan allows.</summary>
/// <param name="Enabled">Master switch.</param>
/// <param name="Triggers">
/// Which occasions the workspace has switched on, keyed <c>greeting</c>, <c>first_message</c> and
/// <c>unanswered</c>. Every key is always present.
/// </param>
/// <param name="AllowedTriggers">
/// Which occasions the plan sells. A trigger that is false here is shown disabled, not hidden: an
/// admin should be able to see what an upgrade would buy them.
/// </param>
/// <param name="DelaySeconds">Pause before a greeting or first-message reply is sent.</param>
/// <param name="UnansweredAfterMinutes">How long a message may sit unanswered before the AI steps in.</param>
/// <param name="Instructions">What the business wants the assistant to sound like and to avoid.</param>
/// <param name="MaxPerConversationPerDay">Ceiling on automatic replies to one customer in a day.</param>
/// <param name="MonthlyLimit">Replies the plan allows per billing period, or null for no ceiling.</param>
/// <param name="UsedThisPeriod">Replies already sent this period.</param>
/// <param name="RemainingThisPeriod">What is left, or null when there is no ceiling.</param>
/// <param name="PeriodEndsAt">When the allowance resets.</param>
/// <param name="AssistantConfigured">
/// Whether the AI provider has a key on this deployment. False means every other setting here is
/// inert, and the screen should say so rather than let an admin switch on something that cannot run.
/// </param>
public sealed record AutoReplySettingsResponse(
    bool Enabled,
    IReadOnlyDictionary<string, bool> Triggers,
    IReadOnlyDictionary<string, bool> AllowedTriggers,
    int DelaySeconds,
    int UnansweredAfterMinutes,
    string Instructions,
    int MaxPerConversationPerDay,
    int? MonthlyLimit,
    int UsedThisPeriod,
    int? RemainingThisPeriod,
    DateTimeOffset? PeriodEndsAt,
    bool AssistantConfigured);

/// <summary>Changes to a workspace's automatic-reply rules.</summary>
/// <param name="Enabled">Master switch.</param>
/// <param name="Triggers">Occasions to switch on. Anything the plan does not sell is refused.</param>
/// <param name="DelaySeconds">Pause before a greeting or first-message reply, 10 to 1800 seconds.</param>
/// <param name="UnansweredAfterMinutes">Wait before answering an unanswered message, 5 to 1440 minutes.</param>
/// <param name="Instructions">Optional guidance for the assistant, up to 2000 characters.</param>
/// <param name="MaxPerConversationPerDay">Ceiling per customer per day, 1 to 20.</param>
public sealed record AutoReplySettingsRequest(
    bool Enabled,
    IReadOnlyDictionary<string, bool>? Triggers,
    int DelaySeconds,
    int UnansweredAfterMinutes,
    string? Instructions,
    int MaxPerConversationPerDay);
