using Marketing.Application.Services;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Marketing.API.Filters;

/// <summary>
/// Requires the resolved tenant's plan to include a feature module before the action runs.
/// <para>
/// Distinct from <see cref="RequirePermissionAttribute"/>, and both apply: a permission says what
/// this <em>user</em> may do, a module says what this <em>organisation</em> has bought. A tenant
/// owner holds every permission in the catalogue and still cannot use a module their plan excludes.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireModuleAttribute : Attribute, IAsyncActionFilter
{
    private readonly string _module;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="module">Module key from <c>PlanModules</c>.</param>
    public RequireModuleAttribute(string module)
    {
        _module = module;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Resolved after the tenant scope filter has run, so a Super Admin scoped to an Admin is
        // checked against that Admin's plan rather than against nothing.
        var guard = context.HttpContext.RequestServices.GetRequiredService<IPlanGuard>();

        await guard.EnsureModuleAsync(_module, context.HttpContext.RequestAborted);

        await next();
    }
}
