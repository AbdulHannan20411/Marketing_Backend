using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using System.Security.Claims;
using Marketing.Common.Constants;
using Marketing.Shared.Abstractions;
using Microsoft.AspNetCore.Http;

// This type exposes a Roles property, which would otherwise shadow the Roles constant class.
using RoleCatalog = Marketing.Common.Constants.Roles;

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
    public long? UserId
    {
        get
        {
            // ASP.NET Core remaps "sub" to ClaimTypes.NameIdentifier by default, but the mapping is
            // switched off in the API host so tokens round-trip unchanged. Read both so this type
            // stays correct regardless of that setting.
            var raw = Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub)
                      ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

            return long.TryParse(raw, out var userId) ? userId : null;
        }
    }

    /// <inheritdoc />
    public string? Email =>
        Principal?.FindFirstValue(JwtRegisteredClaimNames.Email)
        ?? Principal?.FindFirstValue(ClaimTypes.Email);

    /// <inheritdoc />
    public Guid? SessionId =>
        Guid.TryParse(Principal?.FindFirstValue(AppConstants.Claims.SessionId), out var sessionId)
            ? sessionId
            : null;

    /// <inheritdoc />
    public string? DisplayName => Principal?.FindFirstValue(AppConstants.Claims.Name);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Roles =>
        Principal?.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray() ?? [];

    /// <summary>
    /// Permissions carried by the token.
    /// <para>
    /// The claim holds a JSON array in a single claim value, so it is parsed rather than collected
    /// from repeated claims. A malformed value yields an empty set: failing closed here means a
    /// corrupt token grants nothing instead of throwing on every authorisation check.
    /// </para>
    /// </summary>
    public IReadOnlyCollection<string> Permissions
    {
        get
        {
            var raw = Principal?.FindFirstValue(AppConstants.Claims.Permissions);

            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<string[]>(raw) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    /// <inheritdoc />
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true && UserId is not null;

    /// <inheritdoc />
    public bool IsSuperAdmin => IsInRole(RoleCatalog.SuperAdmin);

    /// <inheritdoc />
    public long AuditUserId => UserId ?? AppConstants.Platform.SystemUserId;

    /// <inheritdoc />
    public bool IsInRole(string role) => Principal?.IsInRole(role) == true;

    /// <inheritdoc />
    public bool HasPermission(string permission) =>
        Marketing.Common.Constants.Permissions.IsSatisfiedBy(Permissions, permission);
}
