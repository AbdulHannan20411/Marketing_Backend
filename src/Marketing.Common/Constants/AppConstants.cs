namespace Marketing.Common.Constants;

/// <summary>
/// Every constant and enumeration the platform shares, in one place.
/// <para>
/// Grouped as nested classes rather than scattered across files so that "what magic values does
/// this system have" is answerable by opening a single type. Roles and permissions are the two
/// deliberate exceptions - they live in <see cref="Roles"/> and <see cref="Permissions"/> because
/// they are security contracts that deserve their own reviewable surface.
/// </para>
/// <para>
/// Consuming code can shorten <c>AppConstants.Claims.TenantId</c> to <c>Claims.TenantId</c> with
/// <c>using static Marketing.Common.Constants.AppConstants;</c>.
/// </para>
/// </summary>
public static class AppConstants
{
    /// <summary>Name of the application, used in log enrichment and OpenAPI metadata.</summary>
    public const string ApplicationName = "Marketing.API";

    /// <summary>Base route for every versioned endpoint.</summary>
    public const string ApiRoutePrefix = "api/v{version:apiVersion}";

    // -------------------------------------------------------------------------------------
    // Constant groups
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Custom JWT claim types issued by this platform.
    /// <para>
    /// The tenant identifier is transported here and nowhere else. It is never accepted from a
    /// request body, route value, query string or header.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The client decodes the JWT payload directly and drives navigation and authorisation from
    /// it, so these names are a contract. A JWT payload is signed but <b>not encrypted</b> - treat
    /// every claim here as public to the token holder and never put a secret in one.
    /// </remarks>
    public static class Claims
    {
        /// <summary>Full name of the authenticated user.</summary>
        public const string Name = "name";

        /// <summary>
        /// The principal's single role: <c>SuperAdmin</c>, <c>Admin</c> or <c>Employee</c>.
        /// <para>
        /// Singular by contract. The client reads one string, not a list, so a user carrying two
        /// roles would break it - see <c>Roles.Primary</c>.
        /// </para>
        /// </summary>
        public const string Role = "role";

        /// <summary>
        /// The principal's effective permissions, serialised as a JSON array.
        /// <para>
        /// The effective set, not the role's defaults: the client does not derive permissions from
        /// role, so per-user overrides have to be resolved server-side before issuance.
        /// </para>
        /// </summary>
        public const string Permissions = "permissions";

        /// <summary>
        /// Display label for the organisation.
        /// <para>
        /// A label and nothing more. It is never a tenant identifier and must never be used for
        /// data access - tenancy comes from <see cref="TenantId"/>, server-side only.
        /// </para>
        /// </summary>
        public const string WorkspaceName = "workspaceName";

        /// <summary>Avatar URL, or null.</summary>
        public const string AvatarUrl = "avatarUrl";

        /// <summary>Tenant the token was issued for. Absent for platform staff.</summary>
        public const string TenantId = "tenant_id";

        /// <summary>Human-readable tenant slug, carried for logging and diagnostics only.</summary>
        public const string TenantSlug = "tenant_slug";

        /// <summary>Opaque identifier of the session that issued the token.</summary>
        public const string SessionId = "sid";
    }

    /// <summary>Custom HTTP headers emitted or consumed by the API.</summary>
    public static class Headers
    {
        /// <summary>Ties every log entry and audit row for one request together.</summary>
        public const string CorrelationId = "X-Correlation-Id";

        /// <summary>Identifier of a single request.</summary>
        public const string RequestId = "X-Request-Id";

        /// <summary>Attached to an error response so support can locate the log entry.</summary>
        public const string ExceptionId = "X-Exception-Id";

        /// <summary>Total row count returned alongside a paged collection.</summary>
        public const string TotalCount = "X-Total-Count";

        /// <summary>Set when a request failed purely because the access token had expired.</summary>
        public const string TokenExpired = "X-Token-Expired";

        /// <summary>Signature header Meta uses to sign webhook payloads.</summary>
        public const string MetaSignature = "X-Hub-Signature-256";
    }

    /// <summary>
    /// Authorization policy names. Controllers reference these rather than raw role strings, so the
    /// mapping from intent to roles stays in exactly one place.
    /// </summary>
    public static class Policies
    {
        /// <summary>Requires <see cref="Roles.SuperAdmin"/>.</summary>
        public const string SuperAdminOnly = "policy:super-admin";

        /// <summary>Requires <see cref="Roles.Admin"/> or <see cref="Roles.SuperAdmin"/>.</summary>
        public const string TenantAdministration = "policy:tenant-administration";

        /// <summary>Requires any authenticated member of the current tenant.</summary>
        public const string TenantMembership = "policy:tenant-membership";

        /// <summary>Requires a resolved, non-empty tenant on the request.</summary>
        public const string RequireTenant = "policy:require-tenant";
    }

    /// <summary>
    /// Named rate-limiting policies. Each endpoint group gets its own budget so a burst of report
    /// queries can never starve campaign dispatch or webhook ingestion.
    /// </summary>
    public static class RateLimits
    {
        /// <summary>Login, refresh and password endpoints. Partitioned by client address.</summary>
        public const string Authentication = "rl:authentication";

        /// <summary>Reporting endpoints. Expensive, so limited by concurrency.</summary>
        public const string Reports = "rl:reports";

        /// <summary>Campaign creation and dispatch, partitioned by tenant.</summary>
        public const string Campaigns = "rl:campaigns";

        /// <summary>Meta webhook ingestion. High ceiling - Meta drops slow endpoints.</summary>
        public const string Webhook = "rl:webhook";

        /// <summary>Platform administration endpoints, partitioned by user.</summary>
        public const string Admin = "rl:admin";

        /// <summary>Default budget applied to everything else.</summary>
        public const string Default = "rl:default";
    }

    /// <summary>
    /// Well-known identities used when no interactive user is present.
    /// <para>
    /// Background jobs, migrations and seeding still have to satisfy the non-nullable
    /// <c>CreatedBy</c> column. A fixed, reserved identifier makes those rows obvious in an audit
    /// query instead of indistinguishable from a real user's changes.
    /// </para>
    /// </summary>
    public static class Platform
    {
        /// <summary>Identifier stamped on rows written by the platform itself.</summary>
        public static readonly Guid SystemUserId = new("00000000-0000-0000-0000-000000000001");

        /// <summary>Display name shown for system-authored changes.</summary>
        public const string SystemDisplayName = "System";

        /// <summary>Address of the system principal. Never receives mail and cannot sign in.</summary>
        public const string SystemEmail = "system@platform.internal";
    }

    /// <summary>Default values applied when a caller supplies none.</summary>
    public static class Defaults
    {
        /// <summary>Page size used when a request omits one.</summary>
        public const int PageSize = 25;

        /// <summary>Largest page a caller may request.</summary>
        public const int MaxPageSize = 200;

        /// <summary>IANA time zone assigned to a new tenant.</summary>
        public const string TimeZoneId = "UTC";

        /// <summary>ISO 4217 currency assigned to a new tenant.</summary>
        public const string CurrencyCode = "USD";

        /// <summary>Cache time to live when a caller specifies none.</summary>
        public const int CacheSeconds = 300;
    }

    /// <summary>
    /// Builders for every Redis key the platform writes.
    /// <para>
    /// Keys are always tenant-prefixed. Centralising construction is what makes it impossible to
    /// serve one tenant an entry populated by another, and it keeps a prefix available for bulk
    /// invalidation when a tenant changes.
    /// </para>
    /// </summary>
    public static class CacheKeys
    {
        private const string Root = "marketing";

        /// <summary>Prefix covering every entry owned by a tenant.</summary>
        public static string TenantPrefix(Guid tenantId) => $"{Root}:t:{tenantId:N}:";

        /// <summary>Prefix covering entries that are not tenant-scoped.</summary>
        public static string PlatformPrefix() => $"{Root}:platform:";

        /// <summary>Cached permission set for a user, invalidated when their roles change.</summary>
        public static string UserPermissions(Guid tenantId, Guid userId) =>
            $"{TenantPrefix(tenantId)}user:{userId:N}:permissions";

        /// <summary>Cached tenant record, keyed by slug for the sign-in path.</summary>
        public static string TenantBySlug(string slug) =>
            $"{PlatformPrefix()}tenant:slug:{slug.ToLowerInvariant()}";

        /// <summary>Cached tenant record, keyed by identifier.</summary>
        public static string TenantById(Guid tenantId) => $"{PlatformPrefix()}tenant:id:{tenantId:N}";

        /// <summary>Dashboard KPI payload for a tenant and a named period.</summary>
        public static string DashboardKpis(Guid tenantId, string period) =>
            $"{TenantPrefix(tenantId)}dashboard:kpis:{period}";

        /// <summary>Synced WhatsApp template list for a tenant.</summary>
        public static string Templates(Guid tenantId) => $"{TenantPrefix(tenantId)}templates";

        /// <summary>Tenant settings blob.</summary>
        public static string TenantSettings(Guid tenantId) => $"{TenantPrefix(tenantId)}settings";
    }

    // -------------------------------------------------------------------------------------
    // Enumerations
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Lifecycle state of a tenant.
    /// <para>Persisted as text, so reordering the members can never reinterpret existing rows.</para>
    /// </summary>
    public enum TenantStatus
    {
        /// <summary>Created but has not completed onboarding; sign-in is blocked.</summary>
        Pending = 0,

        /// <summary>Fully operational.</summary>
        Active = 1,

        /// <summary>Temporarily disabled, typically for non-payment. Data retained, sign-in blocked.</summary>
        Suspended = 2,

        /// <summary>Closed. Retained only for the contractual window.</summary>
        Cancelled = 3,
    }

    /// <summary>Lifecycle state of a user account.</summary>
    public enum UserStatus
    {
        /// <summary>Invited but has not accepted; sign-in is blocked.</summary>
        Invited = 0,

        /// <summary>Normal. Sign-in permitted.</summary>
        Active = 1,

        /// <summary>Disabled by an administrator; sign-in blocked and sessions revoked.</summary>
        Disabled = 2,

        /// <summary>Locked out after repeated failed sign-in attempts; clears automatically.</summary>
        Locked = 3,
    }

    /// <summary>Direction applied to a sort expression on a paged query.</summary>
    public enum SortDirection
    {
        /// <summary>Ascending order.</summary>
        Ascending = 0,

        /// <summary>Descending order.</summary>
        Descending = 1,
    }

    /// <summary>The kind of change recorded in an audit-trail entry.</summary>
    public enum AuditAction
    {
        /// <summary>A new row was inserted.</summary>
        Created = 0,

        /// <summary>An existing row was updated.</summary>
        Updated = 1,

        /// <summary>A row was soft-deleted.</summary>
        Deleted = 2,
    }

    /// <summary>What an emailed single-use token authorises.</summary>
    public enum UserTokenPurpose
    {
        /// <summary>Activates an invited account and sets its first password.</summary>
        Invitation,

        /// <summary>Sets a new password on an existing account.</summary>
        PasswordReset,
    }

    /// <summary>Outcome of a scheduled job execution.</summary>
    public enum JobOutcome
    {
        /// <summary>The job completed without error.</summary>
        Succeeded = 0,

        /// <summary>The job threw and will be retried according to its trigger.</summary>
        Failed = 1,

        /// <summary>The job was cancelled, normally by host shutdown.</summary>
        Cancelled = 2,

        /// <summary>The job was skipped because it is disabled by configuration.</summary>
        Skipped = 3,
    }
}
