using AwesomeAssertions;
using Marketing.Application.Services;
using Marketing.Common.Constants;
using Marketing.DataAccess.Entities;

namespace Marketing.UnitTests.Services;

/// <summary>
/// The floor every workspace member holds regardless of what anybody granted.
/// </summary>
/// <remarks>
/// Without it, inviting somebody with no permissions ticked produced an account that could
/// authenticate and then do nothing: the landing route needs <c>dashboard.view</c>, so the first
/// thing a new employee saw was a permission error on the page they arrived at.
/// </remarks>
public sealed class PermissionBaselineTests
{
    private static UserPermissionOverride Revoke(string permission) =>
        new() { Permission = permission, IsGranted = false };

    private static UserPermissionOverride Grant(string permission) =>
        new() { Permission = permission, IsGranted = true };

    [Fact]
    public void The_baseline_is_a_short_list_of_known_permissions()
    {
        // Everything here is granted to people nobody chose to grant it to, which is the opposite
        // failure and just as real. The count assertion is a deliberate speed bump.
        Permissions.Baseline.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(3);
        Permissions.Baseline.Should().OnlyContain(permission => Permissions.IsKnown(permission));
        Permissions.Baseline.Should().Contain(Permissions.Dashboard.View);
    }

    [Fact]
    public void An_employee_granted_nothing_can_still_reach_the_dashboard()
    {
        // The exact case reported: invited with no permissions selected, every screen errored.
        var effective = EffectivePermissions.Resolve(
            [Roles.Employee],
            Permissions.ForRole(Roles.Employee).Select(Revoke).ToList());

        effective.Should().Contain(Permissions.Dashboard.View);
    }

    [Fact]
    public void An_explicit_revoke_cannot_remove_the_baseline()
    {
        // Applied after the revokes on purpose. An administrator unticking it would otherwise
        // produce an account that signs in and can do nothing at all.
        var effective = EffectivePermissions.Resolve(
            [Roles.Employee],
            [Revoke(Permissions.Dashboard.View)]);

        effective.Should().Contain(Permissions.Dashboard.View);
    }

    [Fact]
    public void Accounts_already_invited_without_it_heal_on_their_next_refresh()
    {
        // The reason the floor is enforced in Resolve and not only when writing overrides: rows
        // revoking it already exist in the database, and nobody should have to notice and re-edit
        // every affected employee.
        var stored = new List<UserPermissionOverride>
        {
            Revoke(Permissions.Dashboard.View),
            Grant(Permissions.Contacts.View),
        };

        var effective = EffectivePermissions.Resolve([Roles.Employee], stored);

        effective.Should().Contain(Permissions.Dashboard.View);
        effective.Should().Contain(Permissions.Contacts.View);
    }

    [Fact]
    public void No_revoke_is_written_for_the_baseline_when_nothing_is_requested()
    {
        // The write-side half. A revoke stored here would be dead weight that Resolve then has to
        // undo on every single request.
        var deltas = EffectivePermissions.Diff([Roles.Employee], []);

        deltas.Should().NotContain(delta => delta.Permission == Permissions.Dashboard.View);
    }

    [Fact]
    public void The_floor_does_not_become_a_starter_pack()
    {
        // The bug this replaces was the opposite one: an invitee silently receiving twelve
        // permissions nobody chose. Asking for nothing must still mean nothing beyond the floor.
        var effective = EffectivePermissions.Resolve(
            [Roles.Employee],
            Permissions.ForRole(Roles.Employee).Select(Revoke).ToList());

        effective.Should().BeEquivalentTo(Permissions.Baseline);
    }

    [Fact]
    public void An_administrator_is_unaffected()
    {
        // Administrators hold everything by role; the floor must not change that in either
        // direction.
        var effective = EffectivePermissions.Resolve([Roles.Admin], overrides: null);

        effective.Should().Contain(Permissions.Dashboard.View);
        effective.Should().Contain(Permissions.Settings.Employees);
    }
}
