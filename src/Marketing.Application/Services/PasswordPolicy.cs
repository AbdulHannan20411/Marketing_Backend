using Marketing.Application.Configurations;
using Marketing.Common.Exceptions;
using Microsoft.Extensions.Options;

namespace Marketing.Application.Services;

/// <summary>Validates a proposed password against platform policy.</summary>
public interface IPasswordPolicy
{
    /// <summary>
    /// Throws when a password does not meet policy.
    /// </summary>
    /// <param name="password">Proposed password.</param>
    /// <param name="propertyName">Field name to attach the error to, for inline rendering.</param>
    /// <exception cref="ValidationException">The password is not acceptable.</exception>
    public void Validate(string? password, string propertyName = "password");
}

/// <inheritdoc cref="IPasswordPolicy" />
public sealed class PasswordPolicy : IPasswordPolicy
{
    /// <summary>
    /// Passwords rejected outright regardless of length.
    /// <para>
    /// A tiny deny-list, not a substitute for a breach-corpus check. It exists because a long
    /// password made of one repeated word passes every character-class rule ever written.
    /// </para>
    /// </summary>
    private static readonly string[] ForbiddenFragments =
        ["password", "qwerty", "welcome", "letmein", "marketing", "whatsapp", "123456"];

    private readonly AuthenticationPolicyOptions _options;

    /// <summary>Initialises a new instance.</summary>
    /// <param name="options">Sign-in policy, which carries the minimum length.</param>
    public PasswordPolicy(IOptions<AuthenticationPolicyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public void Validate(string? password, string propertyName = "password")
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ValidationException(propertyName, "Enter a password.");
        }

        var failures = new List<string>();

        if (password.Length < _options.MinimumPasswordLength)
        {
            failures.Add($"be at least {_options.MinimumPasswordLength} characters");
        }

        // Length carries most of the strength, so the character-class rules are deliberately mild:
        // one letter and one digit. Demanding symbols and mixed case pushes people towards
        // "Password1!" and a sticky note, which is worse than a long passphrase.
        if (!password.Any(char.IsLetter))
        {
            failures.Add("contain a letter");
        }

        if (!password.Any(char.IsDigit))
        {
            failures.Add("contain a number");
        }

        if (ForbiddenFragments.Any(fragment =>
                password.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("not contain a common word such as \"password\" or the product name");
        }

        if (failures.Count == 0)
        {
            return;
        }

        // Every failure at once, so the user fixes the password in one attempt rather than
        // discovering the rules one rejection at a time.
        throw new ValidationException(propertyName, $"Your password must {string.Join(", ", failures)}.");
    }
}
