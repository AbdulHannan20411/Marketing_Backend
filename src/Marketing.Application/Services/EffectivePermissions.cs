using Marketing.Common.Constants;
using Marketing.DataAccess.Entities;

namespace Marketing.Application.Services;

/// <summary>
/// Combines a user's role defaults with their per-user overrides.
/// <para>
/// The single definition of "what may this person do". Both the employees screen and token
/// issuance go through it, because the contract requires the permissions shown on that screen to
/// match what the person's token actually carries - and two separate implementations of that rule
/// would drift.
/// </para>
/// </summary>
public static class EffectivePermissions
{
    /// <summary>Resolves the effective grant.</summary>
    /// <param name="roleNames">Roles assigned to the user.</param>
    /// <param name="overrides">Per-user grant and revoke deltas.</param>
    public static IReadOnlyList<string> Resolve(
        IEnumerable<string> roleNames,
        IEnumerable<UserPermissionOverride>? overrides)
    {
        ArgumentNullException.ThrowIfNull(roleNames);

        var effective = new HashSet<string>(StringComparer.Ordinal);

        foreach (var role in roleNames)
        {
            effective.UnionWith(Permissions.ForRole(role));
        }

        if (overrides is null)
        {
            return Ordered(effective);
        }

        var materialised = overrides as IReadOnlyCollection<UserPermissionOverride> ?? [.. overrides];

        foreach (var entry in materialised.Where(entry => entry.IsGranted))
        {
            // Unknown permissions are dropped rather than granted. A stale override left behind by
            // a renamed permission must not silently become an unbounded grant.
            if (Permissions.IsKnown(entry.Permission))
            {
                effective.Add(entry.Permission);
            }
        }

        // Revokes applied last, so an explicit revoke always beats a grant from either source.
        // Deny-wins is the only ordering that fails safe.
        foreach (var entry in materialised.Where(entry => !entry.IsGranted))
        {
            effective.Remove(entry.Permission);
        }

        return Ordered(effective);
    }

    /// <summary>
    /// Computes the overrides needed to turn a role's defaults into a desired effective set.
    /// <para>
    /// Stored as deltas rather than the desired set itself: a stored absolute list would freeze
    /// that user's permissions the next time the role's defaults change, so they would silently
    /// miss every capability added by a later release.
    /// </para>
    /// </summary>
    /// <param name="roleNames">Roles assigned to the user.</param>
    /// <param name="desired">The effective set the caller wants.</param>
    /// <returns>Permission and whether it is granted, for each needed override.</returns>
    public static IReadOnlyList<(string Permission, bool IsGranted)> Diff(
        IEnumerable<string> roleNames,
        IEnumerable<string> desired)
    {
        ArgumentNullException.ThrowIfNull(roleNames);
        ArgumentNullException.ThrowIfNull(desired);

        var defaults = new HashSet<string>(StringComparer.Ordinal);

        foreach (var role in roleNames)
        {
            defaults.UnionWith(Permissions.ForRole(role));
        }

        var target = new HashSet<string>(
            desired.Where(Permissions.IsKnown),
            StringComparer.Ordinal);

        var deltas = new List<(string, bool)>();

        deltas.AddRange(target.Except(defaults, StringComparer.Ordinal).Select(permission => (permission, true)));
        deltas.AddRange(defaults.Except(target, StringComparer.Ordinal).Select(permission => (permission, false)));

        return deltas;
    }

    private static IReadOnlyList<string> Ordered(HashSet<string> effective) =>
        // Ordered by the catalogue rather than alphabetically, so the permissions screen groups
        // them the same way the client's own list does.
        [.. Permissions.All.Where(effective.Contains)];
}
