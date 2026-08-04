using FluentValidation;
using Marketing.Application.DTOs.Auth;

namespace Marketing.Application.Validators;

/// <summary>Validates <see cref="RefreshTokenRequest"/>.</summary>
public sealed class RefreshTokenRequestValidator : AbstractValidator<RefreshTokenRequest>
{
    /// <summary>Initialises a new instance.</summary>
    public RefreshTokenRequestValidator()
    {
        RuleFor(request => request.RefreshToken)
            .NotEmpty().WithMessage("Refresh token is required.")
            .MaximumLength(512).WithMessage("Refresh token is not valid.");
    }
}

/// <summary>Validates <see cref="RevokeTokenRequest"/>.</summary>
public sealed class RevokeTokenRequestValidator : AbstractValidator<RevokeTokenRequest>
{
    /// <summary>Initialises a new instance.</summary>
    public RevokeTokenRequestValidator()
    {
        RuleFor(request => request.RefreshToken)
            .NotEmpty().WithMessage("Refresh token is required.")
            .MaximumLength(512).WithMessage("Refresh token is not valid.");
    }
}
