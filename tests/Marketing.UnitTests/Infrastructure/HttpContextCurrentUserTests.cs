using System.Security.Claims;
using AwesomeAssertions;
using Marketing.Common.Constants;
using Marketing.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;

namespace Marketing.UnitTests.Infrastructure;

public sealed class HttpContextCurrentUserTests
{
    private static HttpContextCurrentUser CreateUser(params Claim[] claims)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")),
        };

        return new HttpContextCurrentUser(new HttpContextAccessor { HttpContext = context });
    }

    [Fact]
    public void Permissions_are_read_from_repeated_claims()
    {
        // The shape a validated bearer token actually produces: the handler expands the JSON array
        // written into the token into one claim per element. Reading only the first and trying to
        // deserialise it returned nothing, which denied every authorisation check for every user.
        var user = CreateUser(
            new Claim(AppConstants.Claims.Permissions, "dashboard.view"),
            new Claim(AppConstants.Claims.Permissions, "platform.tenants"),
            new Claim(AppConstants.Claims.Permissions, "contacts.view"));

        user.Permissions.Should().BeEquivalentTo(["dashboard.view", "platform.tenants", "contacts.view"]);
        user.HasPermission("platform.tenants").Should().BeTrue();
    }

    [Fact]
    public void A_single_permission_is_read_as_one_claim()
    {
        var user = CreateUser(new Claim(AppConstants.Claims.Permissions, "contacts.view"));

        user.Permissions.Should().BeEquivalentTo(["contacts.view"]);
        user.HasPermission("contacts.view").Should().BeTrue();
    }

    [Fact]
    public void Permissions_are_read_from_an_unexpanded_json_array()
    {
        // A principal built by hand - tests, background work - can still carry the written form.
        var user = CreateUser(
            new Claim(AppConstants.Claims.Permissions, """["dashboard.view","reports.view"]"""));

        user.Permissions.Should().BeEquivalentTo(["dashboard.view", "reports.view"]);
    }

    [Fact]
    public void A_permission_the_token_does_not_carry_is_refused()
    {
        var user = CreateUser(new Claim(AppConstants.Claims.Permissions, "contacts.view"));

        user.HasPermission("platform.tenants").Should().BeFalse();
    }

    [Fact]
    public void A_principal_with_no_permission_claim_holds_none()
    {
        CreateUser(new Claim(AppConstants.Claims.Name, "Nobody")).Permissions.Should().BeEmpty();
    }

    [Fact]
    public void A_malformed_array_fails_closed()
    {
        var user = CreateUser(new Claim(AppConstants.Claims.Permissions, "[not valid json"));

        // Empty rather than an exception: a corrupt token must grant nothing, not break every
        // request that touches an authorisation check.
        user.Permissions.Should().BeEmpty();
    }
}
