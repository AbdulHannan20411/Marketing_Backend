using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Workspace;

/// <summary>A member of an organisation.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>emp_</c>.</param>
/// <param name="Name">Full name.</param>
/// <param name="Initials">Initials, computed server-side.</param>
/// <param name="Email">Email address.</param>
/// <param name="JobTitle">Job title.</param>
/// <param name="Role">Role name.</param>
/// <param name="Status">Account state.</param>
/// <param name="Permissions">
/// The effective grant - role defaults plus per-user overrides. Must match what this person's
/// token would carry, or the employees screen and the app itself will disagree.
/// </param>
/// <param name="LastActiveAt">Instant of last activity.</param>
/// <param name="InvitedAt">Instant they were invited.</param>
/// <param name="WhatsAppAccess">
/// Which WhatsApp numbers they may use, and for what. Always empty for an administrator, who holds
/// every number by role rather than by rows.
/// </param>
/// <param name="DefaultWhatsAppAccountId">The number their screens open on, or null.</param>
public sealed record EmployeeResponse(
    string Id,
    string Name,
    string Initials,
    string Email,
    string JobTitle,
    string Role,
    EmployeeStatus Status,
    IReadOnlyList<string> Permissions,
    DateTimeOffset? LastActiveAt,
    DateTimeOffset InvitedAt,
    IReadOnlyList<WhatsApp.WhatsAppAccessEntry>? WhatsAppAccess = null,
    string? DefaultWhatsAppAccountId = null);

/// <summary>A reusable bundle of permissions.</summary>
/// <param name="Id">Opaque identifier.</param>
/// <param name="Name">Set name.</param>
/// <param name="Description">Description.</param>
/// <param name="IsSystem">Whether the platform ships it. System sets cannot be deleted.</param>
/// <param name="Permissions">Permissions in the set.</param>
/// <param name="AssignedCount">How many employees currently hold every permission in the set.</param>
public sealed record PermissionSetResponse(
    string Id,
    string Name,
    string Description,
    bool IsSystem,
    IReadOnlyList<string> Permissions,
    int AssignedCount);

/// <summary>
/// Which groups of notifications one person still wants.
/// </summary>
/// <remarks>
/// Always all six, whatever is stored: a client handed a partial object cannot tell a category it
/// has never heard of from one that is switched off. <c>Security</c> and <c>System</c> are always
/// true - they are not offered as switches and are not honoured as ones.
/// </remarks>
/// <param name="Messages">Customer replies and conversation assignment.</param>
/// <param name="Campaigns">A campaign finished, failed or paused.</param>
/// <param name="Team">Invitations and permission changes.</param>
/// <param name="Billing">Payments, renewals, plan changes and usage limits.</param>
/// <param name="Security">Sign-ins and sharing warnings. Always true.</param>
/// <param name="System">Connections, tokens and allowances. Always true.</param>
public sealed record NotificationPreferences(
    bool Messages,
    bool Campaigns,
    bool Team,
    bool Billing,
    bool Security,
    bool System);

/// <summary>
/// How a caller asks for their notifications.
/// </summary>
/// <remarks>
/// Paging is opt-in: with neither <c>page</c> nor <c>pageSize</c> the endpoint answers exactly as
/// it always has, with a plain array. Filters apply either way, so a client can narrow the list
/// before it starts paging it.
/// </remarks>
public sealed class NotificationQuery
{
    /// <summary>Rows the notification centre renders per page.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>Largest page a caller may ask for.</summary>
    public const int MaxPageSize = 100;

    /// <summary>One-based page number, or null when the caller did not ask for a page.</summary>
    public int? Page { get; init; }

    /// <summary>Rows per page, or null to use <see cref="DefaultPageSize"/>.</summary>
    public int? PageSize { get; init; }

    /// <summary>Whether to return only what the recipient has not read.</summary>
    public bool? UnreadOnly { get; init; }

    /// <summary>Severity to filter by, or null for every severity.</summary>
    public NotificationPriority? Priority { get; init; }

    /// <summary>Group to filter by, or null for every group.</summary>
    public NotificationCategory? Category { get; init; }

    /// <summary>Whether the caller asked for a page rather than the whole list.</summary>
    public bool WantsPage => Page.HasValue || PageSize.HasValue;
}

/// <summary>
/// One page of notifications, with the counts the bell shows.
/// </summary>
/// <remarks>
/// The paging fields are the usual <c>PagedResult</c> ones. The two counts are deliberately not:
/// they are taken over everything addressed to the caller, ignoring both the page and the filters,
/// because the bell means "unread", never "unread on this page" or "unread among critical ones".
/// </remarks>
/// <param name="Items">Notifications on this page, newest first.</param>
/// <param name="Page">One-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="TotalItems">Matching notifications across every page.</param>
/// <param name="TotalPages">Number of pages, never below one.</param>
/// <param name="UnreadCount">Unread notifications, whatever this page holds.</param>
/// <param name="CriticalCount">Unread notifications of critical severity.</param>
public sealed record NotificationFeed(
    IReadOnlyList<AppNotification> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages,
    int UnreadCount,
    int CriticalCount);

/// <summary>A notification.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>ntf_</c>.</param>
/// <param name="Kind">What it is about.</param>
/// <param name="Category">
/// The group it belongs to, and the switch that silences it. Derived from <paramref name="Kind"/>
/// server-side so the client never has to infer it from the kind's prefix.
/// </param>
/// <param name="Title">Headline.</param>
/// <param name="Body">Body text.</param>
/// <param name="Priority">Severity.</param>
/// <param name="Icon">Icon key from the client's registry; an unknown key renders nothing.</param>
/// <param name="Read">Whether the recipient has read it.</param>
/// <param name="ActionLabel">Call-to-action label.</param>
/// <param name="ActionRoute">In-app route the action navigates to.</param>
/// <param name="OccurredAt">Instant the event happened.</param>
public sealed record AppNotification(
    string Id,
    NotificationKind Kind,
    NotificationCategory Category,
    string Title,
    string Body,
    NotificationPriority Priority,
    string Icon,
    bool Read,
    string? ActionLabel,
    string? ActionRoute,
    DateTimeOffset OccurredAt);

/// <summary>One global search hit.</summary>
/// <param name="Id">Identifier of the underlying record.</param>
/// <param name="Kind">Category.</param>
/// <param name="Title">Primary line.</param>
/// <param name="Subtitle">Secondary line.</param>
/// <param name="Icon">Icon key from the client's registry.</param>
/// <param name="Route">In-app route to open the record.</param>
public sealed record SearchResult(
    string Id,
    SearchResultKind Kind,
    string Title,
    string Subtitle,
    string Icon,
    string Route);

/// <summary>Search hits of one kind.</summary>
/// <param name="Kind">Category.</param>
/// <param name="Label">Group heading.</param>
/// <param name="Results">Hits, capped at four.</param>
public sealed record SearchResultGroup(
    SearchResultKind Kind,
    string Label,
    IReadOnlyList<SearchResult> Results);
