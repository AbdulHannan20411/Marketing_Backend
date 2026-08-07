using AwesomeAssertions;
using Marketing.Application.Configurations;
using Marketing.Application.Services;
using Marketing.Common.Exceptions;
using Microsoft.Extensions.Options;

namespace Marketing.UnitTests.Services;

public sealed class PasswordPolicyTests
{
    private static PasswordPolicy CreatePolicy(int minimumLength = 12) =>
        new(Options.Create(new AuthenticationPolicyOptions { MinimumPasswordLength = minimumLength }));

    [Fact]
    public void A_long_passphrase_with_a_number_is_accepted()
    {
        var act = () => CreatePolicy().Validate("correct horse battery staple 7");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_password_is_rejected(string? password)
    {
        var act = () => CreatePolicy().Validate(password);

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void A_password_below_the_minimum_length_is_rejected()
    {
        var act = () => CreatePolicy().Validate("Short1");

        act.Should().Throw<ValidationException>()
            .Which.Errors["password"][0].Should().Contain("12 characters");
    }

    [Fact]
    public void A_password_with_no_digit_is_rejected()
    {
        var act = () => CreatePolicy().Validate("abcdefghijklmnop");

        act.Should().Throw<ValidationException>()
            .Which.Errors["password"][0].Should().Contain("number");
    }

    [Fact]
    public void A_password_with_no_letter_is_rejected()
    {
        var act = () => CreatePolicy().Validate("1234567890123456");

        act.Should().Throw<ValidationException>()
            .Which.Errors["password"][0].Should().Contain("letter");
    }

    [Theory]
    [InlineData("mypassword12345")]
    [InlineData("Marketing2026xx")]
    [InlineData("qwerty1234567890")]
    public void A_password_built_from_a_common_word_is_rejected_however_long(string password)
    {
        // Length alone is not strength. These all clear the length and character-class rules while
        // being among the first things any credential-stuffing list tries.
        var act = () => CreatePolicy().Validate(password);

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void Every_failure_is_reported_at_once()
    {
        var act = () => CreatePolicy().Validate("abc");

        // One message covering all of it, so the user fixes the password in a single attempt
        // rather than discovering the rules one rejection at a time.
        var message = act.Should().Throw<ValidationException>().Which.Errors["password"][0];

        message.Should().Contain("12 characters").And.Contain("number");
    }

    [Fact]
    public void The_error_is_attached_to_the_field_the_caller_names()
    {
        var act = () => CreatePolicy().Validate("abc", "newPassword");

        act.Should().Throw<ValidationException>()
            .Which.Errors.Should().ContainKey("newPassword");
    }
}
