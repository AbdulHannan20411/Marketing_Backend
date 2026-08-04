using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Marketing.Common.Constants;
using Marketing.Shared.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Marketing.Infrastructure.Authentication;

/// <summary>
/// Reads the current principal from the validated JWT on <c>HttpContext</c>.
/// <para>
/// Falls back to the system identity when there is no request - Quartz jobs, the startup seeder -
/// so those paths still satisfy the non-nullable audit columns and appear correctly attributed in
/// the audit trail rather than as a random user.
/// </para>
/// </summary>
public sealed class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="httpContextAccessor">Accessor for the ambient request.</param>
    public HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    /// <inheritdoc />
    public Guid? UserId
    {
        get
        {
            // ASP.NET Core remaps "sub" to ClaimTypes.NameIdentifier by default, but the mapping is
            // switched off in the API host so tokens round-trip unchanged. Read both so this type
            // stays correct regardless of that setting.
            var raw = Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub)
                      ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

            return Guid.TryParse(raw, out var userId) ? userId : null;
        }
    }

    /// <inheritdoc />
    public string? Email =>
        Principal?.FindFirstValue(JwtRegisteredClaimNames.Email)
        ?? Principal?.FindFirstValue(ClaimTypes.Email);

    /// <inheritdoc />
    public string? DisplayName => Principal?.FindFirstValue(ApplicationClaimTypes.DisplayName);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Roles =>
        Principal?.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray() ?? [];

    /// <inheritdoc />
    public IReadOnlyCollection<string> Permissions =>
        Principal?.FindAll(ApplicationClaimTypes.Permission).Select(claim => claim.Value).ToArray() ?? [];

    /// <inheritdoc />
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true && UserId is not null;

    /// <inheritdoc />
    public bool IsPlatformAdmin => IsInRole(RoleNames.PlatformAdmin);

    /// <inheritdoc />
    public Guid AuditUserId => UserId ?? SystemIdentity.UserId;

    /// <inheritdoc />
    public bool IsInRole(string role) => Principal?.IsInRole(role) == true;

    /// <inheritdoc />
    public bool HasPermission(string permission) =>
        Permissions.Contains(permission, StringComparer.Ordinal)
        || Permissions.Any(granted => IsWildcardMatch(granted, permission));

    /// <summary>
    /// Matches a wildcard grant such as <c>tenant:*</c> against a concrete permission.
    /// </summary>
    private static bool IsWildcardMatch(string granted, string requested)
    {
        if (!granted.EndsWith(":*", StringComparison.Ordinal))
        {
            return false;
        }

        var prefix = granted[..^1];
        return requested.StartsWith(prefix, StringComparison.Ordinal);
    }
}
