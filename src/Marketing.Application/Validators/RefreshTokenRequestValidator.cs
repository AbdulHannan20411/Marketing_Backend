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

/// <summary>Validates <see cref="AcceptInvitationRequest"/>.</summary>
public sealed class AcceptInvitationRequestValidator : AbstractValidator<AcceptInvitationRequest>
{
    /// <summary>Initialises a new instance.</summary>
    public AcceptInvitationRequestValidator()
    {
        RuleFor(request => request.Token)
            .NotEmpty().WithMessage("The invitation link is missing its token.")
            .MaximumLength(512).WithMessage("The invitation link is not valid.");

        // Only presence is checked here. Strength is enforced by IPasswordPolicy, so the rules
        // live in one place and cannot drift between this endpoint and the reset endpoint.
        RuleFor(request => request.Password)
            .NotEmpty().WithMessage("Choose a password.")
            .MaximumLength(256).WithMessage("Password must not exceed 256 characters.");
    }
}

/// <summary>Validates <see cref="ResetPasswordRequest"/>.</summary>
public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    /// <summary>Initialises a new instance.</summary>
    public ResetPasswordRequestValidator()
    {
        RuleFor(request => request.Token)
            .NotEmpty().WithMessage("The reset link is missing its token.")
            .MaximumLength(512).WithMessage("The reset link is not valid.");

        RuleFor(request => request.Password)
            .NotEmpty().WithMessage("Choose a new password.")
            .MaximumLength(256).WithMessage("Password must not exceed 256 characters.");
    }
}

/// <summary>Validates <see cref="ChangePasswordRequest"/>.</summary>
public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    /// <summary>Initialises a new instance.</summary>
    public ChangePasswordRequestValidator()
    {
        RuleFor(request => request.CurrentPassword)
            .NotEmpty().WithMessage("Enter your current password.")
            .MaximumLength(256);

        RuleFor(request => request.NewPassword)
            .NotEmpty().WithMessage("Choose a new password.")
            .MaximumLength(256)
            .NotEqual(request => request.CurrentPassword)
            .WithMessage("Your new password must be different from your current one.");
    }
}
