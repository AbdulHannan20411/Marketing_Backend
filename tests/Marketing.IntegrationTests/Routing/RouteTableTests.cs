using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Marketing.IntegrationTests.Routing;

/// <summary>
/// Guards the route table itself.
/// </summary>
/// <remarks>
/// Runs without containers. It builds MVC's own action table from the API assembly rather than
/// booting the host, so it needs no database and runs anywhere the unit tests do.
/// <para>
/// Written after two controllers both declared <c>GET api/v1/contacts/export</c>. It compiled, the
/// application started, and the failure only surfaced when Swagger enumerated the routes and
/// answered 500. Nothing in the build was in a position to notice.
/// </para>
/// </remarks>
public sealed partial class RouteTableTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex RouteParameter();

    [Fact]
    public void No_two_actions_answer_the_same_method_and_path()
    {
        var routes = RoutesIn(typeof(Program).Assembly);

        // Guards against a vacuous pass: an empty table would also have no collisions.
        routes.Should().NotBeEmpty("the API exposes controllers, so an empty table means discovery failed");

        Collisions(routes).Should().BeEmpty(
            "two actions sharing a method and path make the route table ambiguous, which breaks OpenAPI "
            + "generation at runtime and leaves which action answers up to the router");
    }

    [Fact]
    public void The_collision_check_catches_a_duplicate_it_is_shown()
    {
        // The check above passing proves nothing unless the check can fail. Two deliberately
        // conflicting controllers live in this test assembly for exactly that reason.
        var collisions = Collisions(RoutesIn(typeof(RouteTableTests).Assembly));

        collisions.Should().ContainSingle()
            .Which.Should().Contain("GET route-table-probe/duplicate")
            .And.Contain(nameof(DuplicateRouteProbeAController))
            .And.Contain(nameof(DuplicateRouteProbeBController));
    }

    [Fact]
    public async Task A_literal_segment_wins_over_a_parameter_whichever_is_registered_first()
    {
        // Settles a recurring worry that /campaigns/summary must be registered before
        // /campaigns/{id}. ASP.NET Core ranks routes by precedence, not by declaration order, and a
        // literal segment outranks a parameter. Minimal endpoints use the same matcher as attribute-
        // routed controllers, so this is the behaviour the API gets.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        await using var app = builder.Build();

        // The parameter route is mapped first on purpose. If order decided, "summary" would be read
        // as a campaign id.
        app.MapGet("/campaigns/{id}", (string id) => $"campaign {id}");
        app.MapGet("/campaigns/summary", () => "summary");

        await app.StartAsync(Ct);

        using var client = app.GetTestClient();

        (await client.GetStringAsync("/campaigns/summary", Ct)).Should().Be("summary");
        (await client.GetStringAsync("/campaigns/42", Ct)).Should().Be("campaign 42");
    }

    /// <summary>Every attribute route in an assembly, as method, normalised path and owning action.</summary>
    private static List<(string Key, string Action)> RoutesIn(Assembly assembly)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new DiagnosticListener(nameof(RouteTableTests)));
        services.AddSingleton<DiagnosticSource>(provider => provider.GetRequiredService<DiagnosticListener>());
        services.AddControllers().AddApplicationPart(assembly);

        using var provider = services.BuildServiceProvider();

        var routes = new List<(string Key, string Action)>();

        var actions = provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.ControllerTypeInfo.Assembly == assembly);

        foreach (var action in actions)
        {
            if (action.AttributeRouteInfo?.Template is not { } template)
            {
                continue;
            }

            // Parameter names and constraints are erased: {id} and {id:long} match the same requests.
            var path = RouteParameter().Replace(template, "{}").Trim('/').ToLowerInvariant();

            var methods = action.ActionConstraints?
                .OfType<HttpMethodActionConstraint>()
                .SelectMany(constraint => constraint.HttpMethods)
                .ToList() ?? [];

            if (methods.Count == 0)
            {
                methods.Add("ANY");
            }

            foreach (var method in methods)
            {
                routes.Add(($"{method} {path}", $"{action.ControllerTypeInfo.Name}.{action.MethodInfo.Name}"));
            }
        }

        return routes;
    }

    private static List<string> Collisions(List<(string Key, string Action)> routes) =>
        [.. routes
            .GroupBy(route => route.Key, StringComparer.Ordinal)
            .Where(group => group.Select(route => route.Action).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => $"{group.Key} -> {string.Join(", ", group.Select(route => route.Action))}")];
}

/// <summary>Half of a deliberate route conflict, so the collision check can be seen to fail.</summary>
[Route("route-table-probe")]
public sealed class DuplicateRouteProbeAController : ControllerBase
{
    [HttpGet("duplicate")]
    public IActionResult Get() => Ok();
}

/// <summary>The other half of the deliberate conflict.</summary>
[Route("route-table-probe")]
public sealed class DuplicateRouteProbeBController : ControllerBase
{
    [HttpGet("duplicate")]
    public IActionResult Get() => Ok();
}
