using FluentValidation;
using Marketing.Application.DTOs.Ai;

namespace Marketing.Application.Validators;

/// <summary>Validates <see cref="AiGenerateRequest"/>.</summary>
/// <remarks>
/// Runs before any provider call, so an empty or oversized prompt costs nothing.
/// </remarks>
public sealed class AiGenerateRequestValidator : AbstractValidator<AiGenerateRequest>
{
    /// <summary>Initialises a new instance.</summary>
    public AiGenerateRequestValidator()
    {
        RuleFor(request => request.Prompt)
            .Must(prompt => !string.IsNullOrWhiteSpace(prompt))
            .WithMessage("Describe what you would like the assistant to create.")
            .MaximumLength(AiGenerateRequest.MaxPromptLength)
            .WithMessage($"Keep the prompt under {AiGenerateRequest.MaxPromptLength:N0} characters.");
    }
}
