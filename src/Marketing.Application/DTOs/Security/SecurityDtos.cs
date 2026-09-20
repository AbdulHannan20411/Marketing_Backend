using Marketing.Common.Requests;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Security;

/// <summary>One device an account has signed in from.</summary>
/// <param name="SessionId">The device's most recent session, which is what revoking it ends.</param>
/// <param name="DeviceLabel">"Chrome / Windows".</param>
/// <param name="Browser">Browser family.</param>
/// <param name="OperatingSystem">Operating system family.</param>
/// <param name="DeviceType"><c>desktop</c>, <c>mobile</c> or <c>tablet</c>.</param>
/// <param name="IpAddress">Address it was last seen at.</param>
/// <param name="Location">City and country, when the edge reported them.</param>
/// <param name="FirstSeenAt">First sign-in from this device within the window.</param>
/// <param name="LastActiveAt">Last sign of life.</param>
/// <param name="IsActive">Signed in and seen within the last few minutes.</param>
/// <param name="IsCurrent">The device making this request, so a person cannot sign themselves out by mistake.</param>
/// <param name="CanRevoke">Whether there is still a session on it to end.</param>
/// <param name="SignIns">Sign-ins from this device within the window.</param>
public sealed record DeviceResponse(
    string SessionId,
    string DeviceLabel,
    string Browser,
    string OperatingSystem,
    string DeviceType,
    string? IpAddress,
    string? Location,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastActiveAt,
    bool IsActive,
    bool IsCurrent,
    bool CanRevoke,
    int SignIns);

/// <summary>How likely an account is to be shared. Shown to platform staff only.</summary>
/// <param name="Level">Low, medium or high.</param>
/// <param name="Score">Points behind the level.</param>
/// <param name="Reasons">Each contributing signal, in plain words.</param>
public sealed record RiskResponse(RiskLevel Level, int Score, IReadOnlyList<string> Reasons);

/// <summary>One person in a workspace, from a security point of view.</summary>
/// <param name="UserId">Public identifier, as the employees screen uses it.</param>
/// <param name="Name">Display name.</param>
/// <param name="Email">Sign-in address.</param>
/// <param name="Role">Admin or employee.</param>
/// <param name="ActiveSessions">Sessions alive right now.</param>
/// <param name="Devices">Distinct devices within the window.</param>
/// <param name="LastActiveAt">Last sign of life on any device.</param>
/// <param name="DisplacedLast24Hours">Times a new sign-in ended one of their sessions today.</param>
/// <param name="Risk">
/// The risk assessment - present for platform staff, always null for a workspace's own administrators.
/// </param>
/// <param name="Status"><c>active</c>, <c>invited</c> or <c>suspended</c> - the same status every other screen shows.</param>
/// <param name="CanSuspend">Whether this viewer may suspend them: never themselves, platform staff, or (from a workspace) its admin.</param>
public sealed record EmployeeSecurityResponse(
    string UserId,
    string Name,
    string Email,
    string Role,
    int ActiveSessions,
    int Devices,
    DateTimeOffset? LastActiveAt,
    int DisplacedLast24Hours,
    RiskResponse? Risk,
    EmployeeStatus Status = EmployeeStatus.Active,
    bool CanSuspend = false);

/// <summary>Suspends an account from the security screen.</summary>
/// <param name="Reason">Why, in plain words. Optional; at most 500 characters.</param>
/// <param name="AlertLevel">
/// What the screen showed when the button was pressed: <c>low</c>, <c>warning</c> or <c>high</c>.
/// Recorded in the audit trail next to the server's own assessment, never trusted for anything else.
/// </param>
public sealed record SuspendAccountRequest(string? Reason, string? AlertLevel);

/// <summary>How platform staff ask for the security summary.</summary>
/// <remarks>
/// Always paged, unlike the lists that grew paging later: this endpoint is new, so there is no
/// unpaged shape anyone depends on, and the rows are expensive enough to be worth a ceiling.
/// </remarks>
public sealed class SecuritySummaryQuery : PageRequest
{
    /// <summary>Rows the security screen renders per page.</summary>
    public const int TablePageSize = 10;

    /// <summary>Initialises a new instance with the security screen's page size.</summary>
    public SecuritySummaryQuery() => SetDefaultPageSize(TablePageSize);
}

/// <summary>One workspace's security posture, as the platform list shows it.</summary>
/// <remarks>
/// The counters answer "which customer should I look at first". Whether an individual is the
/// problem is a question for <c>GET /superadmin/security/tenants/{tenantId}</c>, which is why no
/// person is named here.
/// </remarks>
/// <param name="TenantId">Public tenant identifier, <c>tnt_…</c>.</param>
/// <param name="OrganizationName">Workspace name.</param>
/// <param name="People">Active members, platform staff excluded.</param>
/// <param name="ActiveSessions">Sessions alive right now across everyone in the workspace.</param>
/// <param name="HighRisk">Members scored high.</param>
/// <param name="MediumRisk">Members scored medium.</param>
/// <param name="NeedsAttention">
/// Members over the device limit or displaced from a session in the last 24 hours - the two signals
/// that mean a login is being shared, whatever the total score came to.
/// </param>
public sealed record OrganizationSecuritySummary(
    string TenantId,
    string OrganizationName,
    int People,
    int ActiveSessions,
    int HighRisk,
    int MediumRisk,
    int NeedsAttention);

/// <summary>A workspace's seats against how its logins are actually being used.</summary>
/// <param name="OrganizationId">Public tenant identifier.</param>
/// <param name="OrganizationName">Workspace name.</param>
/// <param name="PlanName">Plan it is on.</param>
/// <param name="PurchasedSeats">Seats bought, or null for no ceiling.</param>
/// <param name="UsedSeats">Active people in the workspace.</param>
/// <param name="ActiveSessions">Sessions alive right now across everyone.</param>
/// <param name="UniqueDevices">Distinct devices within the window across everyone.</param>
/// <param name="DeviceWindowDays">How far back devices are counted.</param>
/// <param name="Employees">Everyone in the workspace, riskiest first where risk is shown.</param>
public sealed record OrganizationSecurityResponse(
    string OrganizationId,
    string OrganizationName,
    string PlanName,
    int? PurchasedSeats,
    int UsedSeats,
    int ActiveSessions,
    int UniqueDevices,
    int DeviceWindowDays,
    IReadOnlyList<EmployeeSecurityResponse> Employees);
