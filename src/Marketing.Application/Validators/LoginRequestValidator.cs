using FluentValidation;
using Marketing.Application.DTOs.Auth;

namespace Marketing.Application.Validators;

/// <summary>
/// Validates <see cref="LoginRequest"/>.
/// <para>
/// Only shape is checked here - presence, length, address form. Password <em>strength</em> rules
/// belong on registration and password change, never on sign-in: rejecting a login because the
/// stored password no longer meets current policy would lock legitimate users out of their own
/// accounts, and would tell an attacker which passwords are worth trying.
/// </para>
/// </summary>
public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    /// <summary>Initialises a new instance.</summary>
    public LoginRequestValidator()
    {
        RuleFor(request => request.Email)
            .NotEmpty().WithMessage("Email address is required.")
            .MaximumLength(320).WithMessage("Email address must not exceed 320 characters.")
            .EmailAddress().WithMessage("Enter a valid email address.");

        RuleFor(request => request.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MaximumLength(256).WithMessage("Password must not exceed 256 characters.");
    }
}
