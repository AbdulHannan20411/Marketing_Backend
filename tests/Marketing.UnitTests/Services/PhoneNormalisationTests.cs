using AwesomeAssertions;
using Marketing.Application.Services;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Turning a number as a person writes it into one that can actually be dialled.
/// </summary>
/// <remarks>
/// Written after a real send failed with Meta's <c>131030</c>. A contact entered as
/// <c>0336 7890092</c> was stored as <c>03367890092</c> - punctuation stripped, national format
/// intact - which looked right everywhere in the product and reached nobody.
/// </remarks>
public sealed class PhoneNormalisationTests
{
    [Theory]
    [InlineData("0336 7890092", "PK", "923367890092")]
    [InlineData("03367890092", "PK", "923367890092")]
    [InlineData("0336-789-0092", "PK", "923367890092")]
    [InlineData("07911 123456", "GB", "447911123456")]
    [InlineData("098765 43210", "IN", "919876543210")]
    public void A_nationally_written_number_becomes_international(string input, string country, string expected)
    {
        // The leading zero is a trunk prefix: it exists to dial within a country and never appears
        // in an international number. Dropping it and prefixing the dialling code is the conversion.
        ContactRules.NormalisePhone(input, country).Should().Be(expected);
    }

    [Theory]
    [InlineData("+92 336 7890092", "PK", "923367890092")]
    [InlineData("923367890092", "PK", "923367890092")]
    [InlineData("+1 415 555 2671", "US", "14155552671")]
    public void A_number_already_international_is_left_alone(string input, string country, string expected)
    {
        // Not second-guessed. Prefixing a country code onto a number that already carries one
        // produces a number that reaches nobody, which is the failure this method exists to stop.
        ContactRules.NormalisePhone(input, country).Should().Be(expected);
    }

    [Theory]
    [InlineData("00923367890092", "PK", "923367890092")]
    [InlineData("0092 336 7890092", "PK", "923367890092")]
    [InlineData("00447911123456", "GB", "447911123456")]
    [InlineData("00923367890092", null, "923367890092")]
    public void A_double_zero_is_an_exit_prefix_not_a_trunk_prefix(
        string input,
        string? country,
        string expected)
    {
        // 00 is how most of the world dials out: 0092336... already carries the country code, so
        // stripping it and prefixing another produced 9292336... - a number reaching nobody.
        // Because the country code is already present, this case needs no country at all.
        ContactRules.NormalisePhone(input, country).Should().Be(expected);
    }

    [Fact]
    public void A_single_zero_after_the_trunk_prefix_is_kept()
    {
        // Stripping every leading zero rather than the one trunk prefix would swallow a digit from
        // any national number whose subscriber part legitimately starts with one.
        ContactRules.NormalisePhone("0012345678", "PK").Should().Be("12345678");
        ContactRules.NormalisePhone("0087654321", "GB").Should().Be("87654321");
    }

    [Fact]
    public void A_national_number_with_no_country_is_refused()
    {
        // Refused rather than stored. Such a number cannot be dialled from anywhere else, and
        // accepting it defers the failure to the moment a campaign runs - by which point it is a
        // provider error code rather than a form field somebody can correct.
        var act = () => ContactRules.NormalisePhone("03367890092");

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void The_number_that_failed_in_production_now_works()
    {
        // The exact input that produced Meta's 131030, and the exact number that Meta accepted
        // when sent by hand.
        ContactRules.NormalisePhone("0336 7890092", "PK").Should().Be("923367890092");
    }

    [Fact]
    public void The_dialling_code_lookup_round_trips_with_the_forward_one()
    {
        // The two tables are built from one source, so they cannot disagree - but a number
        // resolving to a country whose code does not resolve back would silently break expansion.
        foreach (var code in new[] { "PK", "GB", "US", "IN", "AE", "SA" })
        {
            var prefix = Countries.ToDiallingCode(code);

            prefix.Should().NotBeNull($"{code} is a country the platform recognises");
            Countries.FromPhoneNumber(prefix + "5551234567").Should().Be(code);
        }
    }

    [Fact]
    public void An_unknown_country_yields_no_dialling_code()
    {
        Countries.ToDiallingCode("ZZ").Should().BeNull();
        Countries.ToDiallingCode(null).Should().BeNull();
        Countries.ToDiallingCode("  ").Should().BeNull();
    }

    [Fact]
    public void A_number_too_short_to_be_real_is_still_refused()
    {
        // The existing plausibility floor is unchanged: expansion applies to numbers that were
        // going to be accepted anyway.
        var act = () => ContactRules.NormalisePhone("0123", "PK");

        act.Should().Throw<ValidationException>();
    }
}
