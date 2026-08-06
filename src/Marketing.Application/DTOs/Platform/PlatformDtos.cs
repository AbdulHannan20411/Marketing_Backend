using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.Application.DTOs.Platform;

/// <summary>An Admin account, as the platform screens list it.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>adm_</c>. This is what <c>?adminId=</c> carries.</param>
/// <param name="Name">Full name of the account owner.</param>
/// <param name="Initials">Initials.</param>
/// <param name="Email">Email address.</param>
/// <param name="Organisation">Organisation name.</param>
/// <param name="Plan">Commercial plan band.</param>
/// <param name="Status">Account state.</param>
/// <param name="EmployeeCount">Live employees.</param>
/// <param name="ContactCount">Live contacts.</param>
/// <param name="CampaignCount">Campaigns created.</param>
/// <param name="LeadCount">Contacts at the lead stage.</param>
/// <param name="CustomerCount">Contacts at the customer stage.</param>
/// <param name="MessagesThisMonth">Messages sent this calendar month.</param>
/// <param name="LastActiveAt">Instant anyone in the organisation was last active.</param>
/// <param name="CreatedAt">Instant the account was created.</param>
public sealed record AdminAccount(
    string Id,
    string Name,
    string Initials,
    string Email,
    string Organisation,
    TenantPlan Plan,
    TenantAccountStatus Status,
    int EmployeeCount,
    int ContactCount,
    int CampaignCount,
    int LeadCount,
    int CustomerCount,
    int MessagesThisMonth,
    DateTimeOffset LastActiveAt,
    DateTimeOffset CreatedAt);

/// <summary>One day on the platform trend chart.</summary>
/// <param name="Date">Date only.</param>
/// <param name="Messages">Messages sent across every tenant.</param>
/// <param name="Customers">Customers across every tenant.</param>
public sealed record PlatformTrendPoint(DateOnly Date, int Messages, int Customers);

/// <summary>Tenant count and revenue for one plan band.</summary>
/// <param name="Plan">Plan band.</param>
/// <param name="AdminCount">Accounts on it.</param>
/// <param name="MonthlyRevenue">Monthly recurring revenue, in major units.</param>
public sealed record PlanBreakdown(TenantPlan Plan, int AdminCount, decimal MonthlyRevenue);

/// <summary>A high-volume Admin account.</summary>
/// <param name="AdminId">Account identifier.</param>
/// <param name="Name">Owner name.</param>
/// <param name="Organisation">Organisation name.</param>
/// <param name="MessagesThisMonth">Messages sent this month.</param>
/// <param name="DeliveryRate">Delivery rate, where 98.2 means 98.2 per cent.</param>
public sealed record TopAdmin(
    string AdminId,
    string Name,
    string Organisation,
    int MessagesThisMonth,
    decimal DeliveryRate);

/// <summary>Platform-wide aggregates across every Admin account.</summary>
/// <param name="TotalAdmins">Admin accounts.</param>
/// <param name="ActiveAdmins">Accounts that are active rather than trialing or suspended.</param>
/// <param name="TotalEmployees">Employees across every organisation.</param>
/// <param name="TotalCampaigns">Campaigns across every organisation.</param>
/// <param name="TotalLeads">Contacts at the lead stage.</param>
/// <param name="TotalCustomers">Contacts at the customer stage.</param>
/// <param name="TotalContacts">Contacts across every organisation.</param>
/// <param name="TotalMessagesThisMonth">Messages sent this calendar month.</param>
/// <param name="MessagesDelta">Percentage change against the previous period.</param>
/// <param name="CustomersDelta">Percentage change against the previous period.</param>
/// <param name="LeadsDelta">Percentage change against the previous period.</param>
/// <param name="CampaignsDelta">Percentage change against the previous period.</param>
/// <param name="Trend">Thirty daily points, oldest first.</param>
/// <param name="PlanBreakdown">Accounts and revenue by plan band.</param>
/// <param name="TopAdmins">Highest-volume accounts.</param>
public sealed record PlatformOverview(
    int TotalAdmins,
    int ActiveAdmins,
    int TotalEmployees,
    int TotalCampaigns,
    int TotalLeads,
    int TotalCustomers,
    int TotalContacts,
    int TotalMessagesThisMonth,
    decimal MessagesDelta,
    decimal CustomersDelta,
    decimal LeadsDelta,
    decimal CampaignsDelta,
    IReadOnlyList<PlatformTrendPoint> Trend,
    IReadOnlyList<PlanBreakdown> PlanBreakdown,
    IReadOnlyList<TopAdmin> TopAdmins);

/// <summary>A tenant organisation.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>tnt_</c>.</param>
/// <param name="Name">Organisation name.</param>
/// <param name="OwnerEmail">Owner's email address.</param>
/// <param name="Plan">Commercial plan band.</param>
/// <param name="Status">Account state.</param>
/// <param name="Seats">Seats purchased.</param>
/// <param name="MessagesThisMonth">Messages sent this calendar month.</param>
/// <param name="MessageQuota">Monthly message ceiling.</param>
/// <param name="CreatedAt">Instant the tenant was created.</param>
public sealed record TenantResponse(
    string Id,
    string Name,
    string OwnerEmail,
    TenantPlan Plan,
    TenantAccountStatus Status,
    int Seats,
    int MessagesThisMonth,
    int MessageQuota,
    DateTimeOffset CreatedAt);

/// <summary>One audit-trail entry.</summary>
/// <param name="Id">Opaque identifier, prefixed <c>aud_</c>.</param>
/// <param name="Actor">Who acted.</param>
/// <param name="ActorInitials">Initials.</param>
/// <param name="Action">What they did.</param>
/// <param name="Target">What they did it to.</param>
/// <param name="Workspace">Organisation the change belonged to.</param>
/// <param name="IpAddress">Client address.</param>
/// <param name="Severity">Severity.</param>
/// <param name="OccurredAt">Instant it happened.</param>
public sealed record AuditLogEntryResponse(
    string Id,
    string Actor,
    string ActorInitials,
    string Action,
    string Target,
    string Workspace,
    string IpAddress,
    AuditSeverity Severity,
    DateTimeOffset OccurredAt);

/// <summary>Health of one monitored service.</summary>
/// <param name="Name">Service name.</param>
/// <param name="Status">Health.</param>
/// <param name="UptimePercent">Uptime, where 99.9 means 99.9 per cent.</param>
/// <param name="LatencyMs">Observed latency.</param>
public sealed record ServiceHealth(string Name, ServiceStatus Status, decimal UptimePercent, int LatencyMs);

/// <summary>Consumption against one platform quota.</summary>
/// <param name="Label">Display label.</param>
/// <param name="Used">Current usage.</param>
/// <param name="Limit">Ceiling.</param>
/// <param name="Unit">Unit of measure.</param>
public sealed record QuotaUsage(string Label, int Used, int Limit, string Unit);

/// <summary>One bucket on the throughput chart.</summary>
/// <param name="Label">Hour label, for example <c>00:00</c>.</param>
/// <param name="Value">Messages in that hour.</param>
public sealed record ThroughputPoint(string Label, int Value);

/// <summary>Infrastructure health for the monitoring screen.</summary>
/// <param name="Services">Monitored services.</param>
/// <param name="Quotas">Platform quotas.</param>
/// <param name="Throughput">Twenty-four hourly buckets.</param>
public sealed record SystemSnapshot(
    IReadOnlyList<ServiceHealth> Services,
    IReadOnlyList<QuotaUsage> Quotas,
    IReadOnlyList<ThroughputPoint> Throughput);
