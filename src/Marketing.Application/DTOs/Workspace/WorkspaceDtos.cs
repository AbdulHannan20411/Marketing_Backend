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
    DateTimeOffset InvitedAt);

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

/// <summary>A notification.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>ntf_</c>.</param>
/// <param name="Kind">What it is about.</param>
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
