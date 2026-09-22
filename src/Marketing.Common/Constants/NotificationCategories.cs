using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Common.Constants;

/// <summary>
/// Which group each kind of notification belongs to, and which groups may be switched off.
/// </summary>
/// <remarks>
/// The mapping lives here rather than beside the screen that renders it, because the same answer
/// has to be given in three places: the payload the client reads, the filter applied when a
/// notification is raised, and the filter applied when the bell is counted. A client inferring it
/// from the kind's prefix would agree until the first kind that did not follow the convention.
/// <para>
/// A kind with no entry is <see cref="NotificationCategory.System"/>, deliberately: a new kind is
/// never silently swallowed by a switch nobody knew applied to it.
/// </para>
/// </remarks>
public static class NotificationCategories
{
    private static readonly Dictionary<NotificationKind, NotificationCategory> MappedTo = new()
    {
        [NotificationKind.InboxMessageReceived] = NotificationCategory.Messages,

        [NotificationKind.CampaignCompleted] = NotificationCategory.Campaigns,
        [NotificationKind.CampaignFailed] = NotificationCategory.Campaigns,

        [NotificationKind.EmployeeInvited] = NotificationCategory.Team,

        [NotificationKind.PaymentReceived] = NotificationCategory.Billing,
        [NotificationKind.PaymentFailed] = NotificationCategory.Billing,
        [NotificationKind.PaymentSubmitted] = NotificationCategory.Billing,
        [NotificationKind.PaymentApproved] = NotificationCategory.Billing,
        [NotificationKind.PaymentRejected] = NotificationCategory.Billing,
        [NotificationKind.SubscriptionExpiring] = NotificationCategory.Billing,
        [NotificationKind.PlanUpgraded] = NotificationCategory.Billing,
        [NotificationKind.StorageLimit] = NotificationCategory.Billing,
        [NotificationKind.ContactsLimit] = NotificationCategory.Billing,
        [NotificationKind.MessagesLimit] = NotificationCategory.Billing,

        [NotificationKind.SecurityNewLogin] = NotificationCategory.Security,
        [NotificationKind.SecurityAlert] = NotificationCategory.Security,
        [NotificationKind.SecurityAccountSuspended] = NotificationCategory.Security,

        [NotificationKind.MetaDisconnected] = NotificationCategory.System,
        [NotificationKind.WhatsAppTokenExpiring] = NotificationCategory.System,
        [NotificationKind.AiRepliesExhausted] = NotificationCategory.System,
    };

    /// <summary>Every kind that has an explicit category. Anything else is <c>System</c>.</summary>
    public static IReadOnlyList<NotificationKind> Mapped { get; } = [.. MappedTo.Keys];

    /// <summary>Every category, in the order the settings screen lists them.</summary>
    public static IReadOnlyList<NotificationCategory> All { get; } =
        [.. Enum.GetValues<NotificationCategory>()];

    /// <summary>The group a kind belongs to. An unmapped kind is <c>System</c>.</summary>
    /// <param name="kind">Kind of notification.</param>
    public static NotificationCategory Of(NotificationKind kind) =>
        MappedTo.GetValueOrDefault(kind, NotificationCategory.System);

    /// <summary>Whether a user may switch this category off.</summary>
    /// <param name="category">Category to test.</param>
    public static bool CanBeSilenced(NotificationCategory category) =>
        category is not (NotificationCategory.Security or NotificationCategory.System);

    /// <summary>Every kind that belongs to a category, for filtering stored rows.</summary>
    /// <remarks>
    /// Only mapped kinds are listed. Unmapped kinds are <c>System</c>, which cannot be silenced, so
    /// no filter ever needs to name them.
    /// </remarks>
    /// <param name="category">Category to expand.</param>
    public static IReadOnlyList<NotificationKind> KindsIn(NotificationCategory category) =>
        [.. MappedTo.Where(entry => entry.Value == category).Select(entry => entry.Key)];
}
