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
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ICurrentUser _currentUser;

    /// <summary>
    /// Explicit scope entered by Super Admin request scoping, Quartz jobs and webhook processing,
    /// none of which get a tenant from a bearer token.
    /// <para>
    /// Held per instance, and this service is registered scoped, so the scope lives exactly as long
    /// as the request or job that entered it. It was an <see cref="AsyncLocal{T}"/>, which is
    /// wrong in a way that fails silently: a write inside an <c>async</c> method is discarded when
    /// that method returns, so a resolver that awaited a database read before entering the scope
    /// handed its caller a scope that was already gone.
    /// </para>
    /// </summary>
    private TenantScope? _ambientScope;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="httpContextAccessor">Accessor for the ambient request.</param>
    /// <param name="currentUser">Principal, consulted for the platform-administrator bypass.</param>
    public TenantContext(IHttpContextAccessor httpContextAccessor, ICurrentUser currentUser)
    {
        _httpContextAccessor = httpContextAccessor;
        _currentUser = currentUser;
    }

    /// <inheritdoc />
    public long? TenantId
    {
        get
        {
            if (_ambientScope is { } scope)
            {
                return scope.TenantId;
            }

            var raw = _httpContextAccessor.HttpContext?.User.FindFirst(AppConstants.Claims.TenantId)?.Value;

            return long.TryParse(raw, out var tenantId) ? tenantId : null;
        }
    }

    /// <inheritdoc />
    public string? TenantSlug =>
        _ambientScope?.TenantSlug
        ?? _httpContextAccessor.HttpContext?.User.FindFirst(AppConstants.Claims.TenantSlug)?.Value;

    /// <inheritdoc />
    public bool HasTenant => TenantId is not null;

    /// <inheritdoc />
    public bool CanAccessAllTenants =>
        // An explicit scope always wins. A job that entered a tenant deliberately must stay inside
        // it even when the principal driving it could see everything - otherwise the query filter
        // silently widens and the job processes other tenants' rows.
        _ambientScope is null && _currentUser.IsSuperAdmin;

    /// <inheritdoc />
    public long RequireTenantId() =>
        TenantId ?? throw new TenantResolutionException();

    /// <inheritdoc />
    public IDisposable BeginScope(long tenantId, string? tenantSlug = null)
    {
        var scope = new TenantScope(this, tenantId, tenantSlug, _ambientScope);
        _ambientScope = scope;

        return scope;
    }

    /// <summary>Restores the enclosing tenant when disposed, so scopes nest correctly.</summary>
    private sealed class TenantScope : IDisposable
    {
        private bool _disposed;

        private readonly TenantContext _owner;

        public TenantScope(TenantContext owner, long tenantId, string? tenantSlug, TenantScope? parent)
        {
            _owner = owner;
            TenantId = tenantId;
            TenantSlug = tenantSlug;
            Parent = parent;
        }

        public long TenantId { get; }

        public string? TenantSlug { get; }

        private TenantScope? Parent { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner._ambientScope = Parent;
        }
    }
}
