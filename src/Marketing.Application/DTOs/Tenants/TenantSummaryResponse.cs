using static Marketing.Common.Constants.AppConstants;

namespace Marketing.Application.DTOs.Tenants;

/// <summary>
/// Tenant record as returned to the platform administration portal.
/// <para>
/// Exposed only under the platform-administration policy. Tenant operators never receive a tenant
/// identifier from any endpoint - see <c>CurrentUserResponse</c> for why.
/// </para>
/// </summary>
/// <param name="Id">Tenant identifier.</param>
/// <param name="Name">Organisation name.</param>
/// <param name="Slug">URL-safe identifier.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="ContactEmail">Primary contact address.</param>
/// <param name="TimeZoneId">IANA time zone.</param>
/// <param name="CurrencyCode">ISO 4217 currency.</param>
/// <param name="ContactQuota">Contacts allowed by the plan.</param>
/// <param name="MonthlyMessageQuota">Outbound messages allowed per month.</param>
/// <param name="UserCount">Number of live users.</param>
/// <param name="CreatedOn">Creation instant.</param>
/// <param name="ActivatedOn">Instant onboarding completed.</param>
public sealed record TenantSummaryResponse(
    Guid Id,
    string Name,
    string Slug,
    TenantStatus Status,
    string ContactEmail,
    string TimeZoneId,
    string CurrencyCode,
    int ContactQuota,
    int MonthlyMessageQuota,
    int UserCount,
    DateTimeOffset CreatedOn,
    DateTimeOffset? ActivatedOn);
