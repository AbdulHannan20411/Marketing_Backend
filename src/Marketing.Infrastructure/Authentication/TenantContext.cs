using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Shared.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Marketing.Infrastructure.Authentication;

/// <summary>
/// Resolves the tenant from the validated <c>tenant_id</c> JWT claim, with an explicit ambient
/// override for background work.
/// <para>
/// <b>The security-critical part of this file is what it does not do.</b> It never consults a
/// route value, query string, header or request body. A caller who edits a tenant identifier in a
/// request they control changes nothing, because nothing downstream ever reads it from there.
/// </para>
/// </summary>
public sealed class TenantContext : ITenantContext
{
    /// <summary>
    /// Explicit scope set by Quartz jobs and webhook processing, which have no bearer token.
    /// <see cref="AsyncLocal{T}"/> so a scope follows the async flow of one job execution and
    /// cannot bleed into another running concurrently on the same thread pool.
    /// </summary>
    private static readonly AsyncLocal<TenantScope?> AmbientScope = new();

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ICurrentUser _currentUser;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="httpContextAccessor">Accessor for the ambient request.</param>
    /// <param name="currentUser">Principal, consulted for the platform-administrator bypass.</param>
    public TenantContext(IHttpContextAccessor httpContextAccessor, ICurrentUser currentUser)
    {
        _httpContextAccessor = httpContextAccessor;
        _currentUser = currentUser;
    }

    /// <inheritdoc />
    public Guid? TenantId
    {
        get
        {
            if (AmbientScope.Value is { } scope)
            {
                return scope.TenantId;
            }

            var raw = _httpContextAccessor.HttpContext?.User.FindFirst(AppConstants.Claims.TenantId)?.Value;

            return Guid.TryParse(raw, out var tenantId) ? tenantId : null;
        }
    }

    /// <inheritdoc />
    public string? TenantSlug =>
        AmbientScope.Value?.TenantSlug
        ?? _httpContextAccessor.HttpContext?.User.FindFirst(AppConstants.Claims.TenantSlug)?.Value;

    /// <inheritdoc />
    public bool HasTenant => TenantId is not null;

    /// <inheritdoc />
    public bool CanAccessAllTenants =>
        // An explicit scope always wins. A job that entered a tenant deliberately must stay inside
        // it even when the principal driving it could see everything - otherwise the query filter
        // silently widens and the job processes other tenants' rows.
        AmbientScope.Value is null && _currentUser.IsSuperAdmin;

    /// <inheritdoc />
    public Guid RequireTenantId() =>
        TenantId ?? throw new TenantResolutionException();

    /// <inheritdoc />
    public IDisposable BeginScope(Guid tenantId, string? tenantSlug = null)
    {
        var scope = new TenantScope(tenantId, tenantSlug, AmbientScope.Value);
        AmbientScope.Value = scope;

        return scope;
    }

    /// <summary>Restores the enclosing tenant when disposed, so scopes nest correctly.</summary>
    private sealed class TenantScope : IDisposable
    {
        private bool _disposed;

        public TenantScope(Guid tenantId, string? tenantSlug, TenantScope? parent)
        {
            TenantId = tenantId;
            TenantSlug = tenantSlug;
            Parent = parent;
        }

        public Guid TenantId { get; }

        public string? TenantSlug { get; }

        private TenantScope? Parent { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            AmbientScope.Value = Parent;
        }
    }
}
