using AwesomeAssertions;
using Marketing.Application.Services;
using Marketing.Common.Constants;
using Marketing.Common.Exceptions;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Refusing a permission grant that omits the read access its own entries depend on.
/// </summary>
/// <remarks>
/// Granting <c>contacts.create</c> without <c>contacts.view</c> produces an employee who can add a
/// contact and then cannot see it. Refused rather than quietly completed, because adding the
/// missing permission would hand somebody access the person granting it never chose.
/// </remarks>
public sealed class PermissionCoherenceTests
{
    [Fact]
    public void A_write_without_its_read_is_refused()
    {
        var act = () => EmployeeRules.EnsureCoherent([Permissions.Contacts.Create]);

        // The detail lives in Errors, keyed by field - Message is the generic envelope text, so
        // asserting on it would pass for any validation failure at all.
        act.Should().Throw<ValidationException>()
            .Which.Errors["permissions"].Should()
            .ContainMatch("*contacts.create requires contacts.view*");
    }

    [Fact]
    public void The_same_grant_with_its_read_is_accepted()
    {
        var act = () => EmployeeRules.EnsureCoherent(
            [Permissions.Contacts.View, Permissions.Contacts.Create]);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("whatsapp.inbox.reply", "whatsapp.inbox.view")]
    [InlineData("whatsapp.templates.sync", "whatsapp.templates.view")]
    [InlineData("reports.export", "reports.view")]
    [InlineData("dashboard.export", "dashboard.view")]
    public void The_dependency_is_derived_for_every_group_that_has_a_read(string granted, string required)
    {
        // Derived from the name, not from a hand-written table that would fall out of step with
        // the permissions themselves.
        var act = () => EmployeeRules.EnsureCoherent([granted]);

        act.Should().Throw<ValidationException>()
            .Which.Errors["permissions"].Should().ContainMatch($"*{granted} requires {required}*");
    }

    [Fact]
    public void A_group_with_no_read_of_its_own_invents_no_dependency()
    {
        // Campaigns are reached through several permissions rather than one, so there is no
        // whatsapp.campaigns.view to require. The rule must yield nothing rather than demand a
        // permission that does not exist.
        var act = () => EmployeeRules.EnsureCoherent(
            [Permissions.WhatsApp.CampaignsCreate, Permissions.WhatsApp.CampaignsSend]);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_view_permission_does_not_depend_on_itself()
    {
        var act = () => EmployeeRules.EnsureCoherent([Permissions.Contacts.View]);

        act.Should().NotThrow();
    }

    [Fact]
    public void An_empty_grant_is_coherent()
    {
        // The invite path relies on this: no permissions named is a valid request, and the floor
        // is applied afterwards rather than by this rule.
        var act = () => EmployeeRules.EnsureCoherent([]);

        act.Should().NotThrow();
    }

    [Fact]
    public void Every_missing_dependency_is_reported_at_once()
    {
        // One round trip per mistake is a poor way to fill in a form.
        var act = () => EmployeeRules.EnsureCoherent(
            [Permissions.Contacts.Create, Permissions.Reports.Export]);

        var detail = act.Should().Throw<ValidationException>()
            .Which.Errors["permissions"].Should().ContainSingle().Subject;

        detail.Should().Contain("contacts.create requires contacts.view")
            .And.Contain("reports.export requires reports.view");
    }

    [Fact]
    public void The_platform_default_grants_are_all_coherent()
    {
        // The rule has to accept what the seeder already produces, or a fresh install cannot
        // create the roles it ships with.
        foreach (var role in new[] { Roles.SuperAdmin, Roles.Admin, Roles.Employee })
        {
            var act = () => EmployeeRules.EnsureCoherent(Permissions.ForRole(role));

            act.Should().NotThrow($"the default grant for {role} must satisfy its own rule");
        }
    }
}
