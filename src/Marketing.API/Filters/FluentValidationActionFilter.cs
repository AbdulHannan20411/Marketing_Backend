using FluentValidation;
using Microsoft.AspNetCore.Mvc.Filters;
using ValidationException = Marketing.Common.Exceptions.ValidationException;

namespace Marketing.API.Filters;

/// <summary>
/// Validates every action argument that has a registered FluentValidation validator, before the
/// action runs.
/// <para>
/// Applied globally rather than per action. Validation that has to be remembered is validation that
/// eventually is not: a new endpoint is protected the moment its validator exists, and a controller
/// never contains an <c>if (!ModelState.IsValid)</c> block.
/// </para>
/// </summary>
public sealed class FluentValidationActionFilter : IAsyncActionFilter
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="serviceProvider">Scoped provider used to resolve validators by argument type.</param>
    public FluentValidationActionFilter(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var failures = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());

            if (_serviceProvider.GetService(validatorType) is not IValidator validator)
            {
                continue;
            }

            var validationContext = new ValidationContext<object>(argument);
            var result = await validator.ValidateAsync(validationContext, context.HttpContext.RequestAborted);

            if (result.IsValid)
            {
                continue;
            }

            foreach (var failure in result.Errors)
            {
                if (!failures.TryGetValue(failure.PropertyName, out var messages))
                {
                    messages = [];
                    failures[failure.PropertyName] = messages;
                }

                messages.Add(failure.ErrorMessage);
            }
        }

        if (failures.Count > 0)
        {
            // Thrown rather than short-circuited with a result, so the response shape is produced
            // by the single global handler and every validation failure looks identical.
            throw new ValidationException(
                failures.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray()));
        }

        await next();
    }
}
