using System.Reflection;
using AwesomeAssertions;
using Marketing.Application.DTOs.BusinessDiscovery;
using Marketing.Application.Interfaces;
using Marketing.Application.Services.BusinessDiscovery;

namespace Marketing.UnitTests.Services;

/// <summary>
/// Deciding whether a discovered business has a number that can be dialled.
/// </summary>
/// <remarks>
/// The provider hands back an international number when it has one and the local form when it does
/// not. The local form cannot be expanded without a country, and the helper that expands numbers
/// refuses by throwing - which, before this was fixed, failed a whole search page and aborted a whole
/// import over one listing.
/// </remarks>
public sealed class DiscoveredPhoneTests
{
    private static readonly MethodInfo NormaliseMethod =
        typeof(BusinessDiscoveryService).GetMethod("NormalisePhone", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ToResponseMethod =
        typeof(BusinessDiscoveryService).GetMethod(
            "ToResponse",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(ProviderBusiness), typeof(HashSet<string>)])!;

    private static string? Normalise(string? phone) => (string?)NormaliseMethod.Invoke(null, [phone]);

    private static DiscoveredBusinessResponse Map(string? phone) =>
        (DiscoveredBusinessResponse)ToResponseMethod.Invoke(
            null,
            [
                new ProviderBusiness("place-1", "Acme Tailors", 0, 0, phone, null, null, null, null, null),
                new HashSet<string>(StringComparer.Ordinal),
            ])!;

    [Fact]
    public void A_national_number_with_no_country_is_not_dialable_rather_than_an_error()
    {
        // The regression. This exact input used to throw straight out of the search.
        var act = () => Normalise("0336 7890092");

        act.Should().NotThrow();
        Normalise("0336 7890092").Should().BeNull();
    }

    [Fact]
    public void An_international_number_normalises_to_digits()
    {
        Normalise("+92 336 7890092").Should().Be("923367890092");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    public void No_number_or_an_implausible_one_is_not_dialable(string? phone)
    {
        Normalise(phone).Should().BeNull();
    }

    [Fact]
    public void A_dialable_number_is_reported_in_e164_with_its_plus()
    {
        var business = Map("+92 336 7890092");

        business.PhoneE164.Should().Be("+923367890092");

        // The display phone keeps the meaning it already had, so nothing reading it today changes.
        business.Phone.Should().Be("923367890092");
    }

    [Fact]
    public void A_number_that_cannot_be_dialled_keeps_its_display_form_and_has_no_e164()
    {
        var business = Map("0336 7890092");

        business.PhoneE164.Should().BeNull("the client should not offer to import a number nobody can dial");
        business.Phone.Should().Be("0336 7890092", "the review screen still shows what the provider had");

        // Null rather than false: with no number there is nothing to match against existing contacts.
        business.ExistsInContacts.Should().BeNull();
    }
}
