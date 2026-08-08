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
    /// The token is <em>written</em> as one claim holding a JSON array, but the bearer handler
    /// <em>expands</em> a JSON array claim into one claim per element while validating. So by the
    /// time a principal reaches here the permissions are repeated claims of plain strings, not a
    /// single parseable array - reading only the first value and deserialising it yields nothing
    /// and silently denies every authorisation check.
    /// </para>
    /// <para>
    /// Both shapes are accepted, because a principal built by hand (tests, background work) can
    /// still carry the unexpanded form. A malformed value yields an empty set: failing closed means
    /// a corrupt token grants nothing rather than throwing on every check.
    /// </para>
    /// </summary>
    public IReadOnlyCollection<string> Permissions
    {
        get
        {
            var claims = Principal?.FindAll(AppConstants.Claims.Permissions).ToArray() ?? [];

            if (claims.Length == 0)
            {
                return [];
            }

            // The expanded form: several claims, or one claim whose value is a bare permission.
            if (claims.Length > 1 || !IsJsonArray(claims[0].Value))
            {
                return [.. claims.Select(claim => claim.Value)];
            }

            try
            {
                return JsonSerializer.Deserialize<string[]>(claims[0].Value) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>Whether a claim value is still the raw JSON array rather than one element of it.</summary>
    /// <param name="value">Claim value.</param>
    private static bool IsJsonArray(string value) =>
        value.AsSpan().TrimStart().StartsWith("[", StringComparison.Ordinal);

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
