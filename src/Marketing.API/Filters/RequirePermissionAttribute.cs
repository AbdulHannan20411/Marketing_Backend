using Marketing.Common.Exceptions;
using Marketing.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Marketing.API.Filters;

/// <summary>
/// Requires a permission from the catalogue before the action runs.
/// <para>
/// The API enforces permissions independently of the UI. The client hides controls a user lacks,
/// but that is convenience: every request is treated as if the UI did not exist, because anyone
/// can issue one with curl.
/// </para>
/// </summary>
/// <remarks>
/// A filter rather than an authorization policy because the permission set is open-ended - one
/// policy per permission would mean fifty registrations that have to be kept in step with the
/// catalogue by hand.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequirePermissionAttribute : Attribute, IAsyncActionFilter
{
    private readonly string[] _permissions;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="permissions">
    /// Permissions to demand. Holding <b>any one</b> of them is sufficient, which matches endpoints
    /// the contract makes available to more than one capability - the campaigns list is readable
    /// both by someone who reports on campaigns and by someone who creates them.
    /// </param>
    public RequirePermissionAttribute(params string[] permissions)
    {
        _permissions = permissions;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var currentUser = context.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();

        if (!currentUser.IsAuthenticated)
        {
            throw new AuthenticationException("not_authenticated");
        }

        if (_permissions.Length == 0 || _permissions.Any(currentUser.HasPermission))
        {
            await next();
            return;
        }

        // 403 with a named permission. Naming it is safe - the catalogue is public in the client
        // bundle anyway - and it turns "why is this failing" into a one-line answer.
        throw new ForbiddenException(
            $"This action requires the {string.Join(" or ", _permissions)} permission.");
    }
}
