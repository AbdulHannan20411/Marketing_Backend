using Marketing.Common.Constants;
using Marketing.Common.Exceptions;
using Marketing.Shared.Abstractions;
using RoleCatalog = Marketing.Common.Constants.Roles;

namespace Marketing.Application.Services;

/// <summary>
/// The guards that stop one operator handing another more access than they hold themselves.
/// <para>
/// Gathered here rather than spread across invite, permission update and role change because they
/// are the same rules in three places, and a privilege-escalation hole is exactly the kind of thing
/// that appears when one copy of a check drifts from the others.
/// </para>
/// </summary>
internal static class EmployeeRules
{
    /// <summary>
    /// Refuses permissions the catalogue does not define.
    /// </summary>
    /// <param name="permissions">Permissions the caller asked for.</param>
    /// <exception cref="ValidationException">One or more are not in the catalogue.</exception>
    public static void EnsureKnown(IReadOnlyList<string> permissions)
    {
        var unknown = permissions.Where(permission => !Permissions.IsKnown(permission)).ToList();

        if (unknown.Count > 0)
        {
            throw new ValidationException(
                "permissions",
                $"Not recognised: {string.Join(", ", unknown)}.");
        }
    }

    /// <summary>
    /// Refuses permissions the caller does not hold.
    /// <para>
    /// The guard that matters most in this file. Without it an Admin can grant a subordinate
    /// something they cannot do themselves, and from there escalate by signing in as them.
    /// </para>
    /// </summary>
    /// <param name="permissions">Permissions the caller asked to grant.</param>
    /// <param name="caller">The principal making the request.</param>
    /// <exception cref="ForbiddenException">The caller lacks one of them.</exception>
    public static void EnsureCallerHolds(IReadOnlyList<string> permissions, ICurrentUser caller)
    {
        // A Super Admin holds everything by definition, so there is nothing they could grant that
        // would exceed their own access.
        if (caller.IsSuperAdmin)
        {
            return;
        }

        var beyond = permissions.Where(permission => !caller.HasPermission(permission)).ToList();

        if (beyond.Count > 0)
        {
            throw new ForbiddenException(
                "You cannot grant a permission you do not hold yourself: "
                + $"{string.Join(", ", beyond)}.");
        }
    }

    /// <summary>
    /// Refuses permissions belonging to a module the plan does not include.
    /// </summary>
    /// <remarks>
    /// The matrix disables those categories in the interface, but the interface is not the gate:
    /// a tenant without the social module must not be able to grant social permissions by calling
    /// the endpoint directly.
    /// </remarks>
    /// <param name="permissions">Permissions the caller asked to grant.</param>
    /// <param name="enabledModules">Module keys the tenant's plan includes.</param>
    /// <exception cref="BusinessRuleException">One belongs to a module outside the plan.</exception>
    public static void EnsureWithinPlan(IReadOnlyList<string> permissions, IReadOnlyCollection<string> enabledModules)
    {
        var outside = permissions
            .Where(permission => ModuleFor(permission) is { } module
                                 && !enabledModules.Contains(module, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (outside.Count > 0)
        {
            throw new BusinessRuleException(
                "permission_not_in_plan",
                $"Your plan does not include the features these permissions belong to: {string.Join(", ", outside)}.");
        }
    }

    /// <summary>
    /// Refuses a role assignment the caller is not entitled to make.
    /// </summary>
    /// <param name="role">Role being granted.</param>
    /// <param name="caller">The principal making the request.</param>
    /// <exception cref="ForbiddenException">The caller may not grant that role.</exception>
    public static void EnsureRoleGrantable(string role, ICurrentUser caller)
    {
        var normalised = RoleCatalog.Normalise(role);

        // Platform staff are never created through a tenant's own employee endpoints, whoever asks.
        if (string.Equals(normalised, RoleCatalog.SuperAdmin, StringComparison.OrdinalIgnoreCase))
        {
            throw new ForbiddenException("The Super Admin role cannot be assigned here.");
        }

        if (string.Equals(normalised, RoleCatalog.Admin, StringComparison.OrdinalIgnoreCase)
            && !caller.IsSuperAdmin
            && !caller.IsInRole(RoleCatalog.Admin))
        {
            throw new ForbiddenException("Only an Admin can grant the Admin role.");
        }

        if (!RoleCatalog.All.Contains(normalised, StringComparer.OrdinalIgnoreCase))
        {
            throw new ValidationException("role", $"\"{role}\" is not a role.");
        }
    }

    /// <summary>
    /// Refuses an action the caller is aiming at their own account.
    /// </summary>
    /// <remarks>
    /// Suspending or demoting yourself is never the intent, and locking the last administrator out
    /// of a workspace costs a support conversation to undo.
    /// </remarks>
    /// <param name="targetUserId">Account being acted on.</param>
    /// <param name="caller">The principal making the request.</param>
    /// <param name="action">Verb used in the message, for example "suspend".</param>
    /// <exception cref="BusinessRuleException">The caller is the target.</exception>
    public static void EnsureNotSelf(long targetUserId, ICurrentUser caller, string action)
    {
        if (caller.UserId == targetUserId)
        {
            throw new BusinessRuleException("cannot_target_self", $"You cannot {action} your own account.");
        }
    }

    /// <summary>
    /// Refuses to leave a workspace with no administrator.
    /// </summary>
    /// <param name="remainingAdmins">Admins that would remain after the change.</param>
    /// <param name="action">Verb used in the message.</param>
    /// <exception cref="BusinessRuleException">None would remain.</exception>
    public static void EnsureAnAdminRemains(int remainingAdmins, string action)
    {
        if (remainingAdmins <= 0)
        {
            throw new BusinessRuleException(
                "last_admin",
                $"This is the only Admin in the workspace. Promote someone else before you {action} them.");
        }
    }

    /// <summary>
    /// Maps a permission to the plan module that governs it, or null when nothing does.
    /// </summary>
    /// <remarks>
    /// Derived from the permission's first segment, which is how the catalogue is organised.
    /// Dashboard, reports and settings permissions belong to no module and are always available -
    /// a customer must be able to reach their own subscription whatever they have bought.
    /// </remarks>
    /// <param name="permission">Dot-notation permission.</param>
    private static string? ModuleFor(string permission)
    {
        var separator = permission.IndexOf('.', StringComparison.Ordinal);
        var head = separator > 0 ? permission[..separator] : permission;

        return head.ToLowerInvariant() switch
        {
            "whatsapp" => PlanModules.WhatsApp,
            "email" => PlanModules.Email,
            "social" => PlanModules.Social,
            "contacts" or "groups" or "tags" => PlanModules.Crm,
            _ => null,
        };
    }
}
