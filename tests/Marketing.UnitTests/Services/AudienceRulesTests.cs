using AwesomeAssertions;
using Marketing.Application.DTOs.Contacts;
using Marketing.Common.Exceptions;
using Marketing.Common.Helpers;
using Marketing.Common.Requests;
using Marketing.Application.Services;
using static Marketing.Common.Constants.ContractEnums;

namespace Marketing.UnitTests.Services;

public sealed class PageRequestTests
{
    [Fact]
    public void The_page_property_is_what_the_client_sends()
    {
        // The contract and the Angular client both send ?page=. Binding matches on property name,
        // so a property called PageNumber never received it and every list returned page one.
        var request = new PageRequest { Page = 3, PageSize = 12 };

        request.Page.Should().Be(3);
        request.Skip.Should().Be(24);
        request.Take.Should().Be(12);
    }

    [Fact]
    public void The_longer_alias_still_works()
    {
        new PageRequest { PageNumber = 4 }.Page.Should().Be(4);
    }

    [Fact]
    public void The_alias_defaulting_to_zero_does_not_reset_an_explicit_page()
    {
        // Both properties are bound. When only ?page= is supplied the alias must not overwrite it
        // with its own default - which is exactly what an unguarded setter would do.
        var request = new PageRequest { Page = 5, PageNumber = 0 };

        request.Page.Should().Be(5);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    public void A_page_below_one_is_coerced(int requested, int expected)
    {
        new PageRequest { Page = requested }.Page.Should().Be(expected);
    }

    [Fact]
    public void A_page_size_beyond_the_cap_is_clamped_rather_than_refused()
    {
        // Clamped, not rejected: a caller asking for ten thousand rows gets the maximum page, and
        // no caller can turn a list endpoint into an unbounded table scan.
        new PageRequest { PageSize = 10_000 }.PageSize.Should().Be(PageRequest.MaxPageSize);
        PageRequest.MaxPageSize.Should().Be(100);
    }

    [Fact]
    public void The_contacts_query_defaults_to_the_table_page_size()
    {
        new ContactQuery().PageSize.Should().Be(12);
    }

    [Fact]
    public void An_explicit_page_size_overrides_the_contacts_default()
    {
        new ContactQuery { PageSize = 50 }.PageSize.Should().Be(50);
    }

    [Fact]
    public void The_contacts_query_defaults_every_filter_to_the_all_sentinel()
    {
        var query = new ContactQuery();

        query.Status.Should().Be("all");
        query.GroupId.Should().Be("all");
        query.TagId.Should().Be("all");
    }
}

public sealed class CountriesTests
{
    [Theory]
    [InlineData("GB", "United Kingdom")]
    [InlineData("gb", "United Kingdom")]
    [InlineData("IN", "India")]
    public void A_stored_code_is_returned_as_a_display_name(string stored, string expected)
    {
        // The client renders this verbatim, so "GB" would appear in the table where the operator
        // expects a country.
        Countries.ToDisplayName(stored).Should().Be(expected);
    }

    [Fact]
    public void A_value_that_is_already_a_name_is_left_alone()
    {
        Countries.ToDisplayName("United Kingdom").Should().Be("United Kingdom");
    }

    [Fact]
    public void An_empty_country_becomes_an_empty_string_not_null()
    {
        Countries.ToDisplayName(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("GB", "GB")]
    [InlineData("United Kingdom", "GB")]
    [InlineData("india", "IN")]
    public void A_code_or_a_name_normalises_to_a_code_for_storage(string supplied, string expected)
    {
        Countries.ToStorageCode(supplied).Should().Be(expected);
    }

    [Fact]
    public void An_unrecognised_country_returns_null_rather_than_a_guess()
    {
        Countries.ToStorageCode("Atlantis").Should().BeNull();
        Countries.ToStorageCode("ZZ").Should().BeNull();
    }

    [Theory]
    [InlineData("+44 7700 900123", "GB")]
    [InlineData("+91 98603 08123", "IN")]
    [InlineData("+1 415 555 0100", "US")]
    [InlineData("+971 50 123 4567", "AE")]
    public void The_country_is_inferred_from_the_dialling_prefix(string phone, string expected)
    {
        // Longest prefix wins. Matching short would resolve every number starting with a 1 to the
        // United States, and every +44 to whatever +4 happened to be.
        Countries.FromPhoneNumber(phone).Should().Be(expected);
    }

    [Fact]
    public void An_unknown_prefix_returns_null_so_the_caller_can_refuse()
    {
        Countries.FromPhoneNumber("+999 123456").Should().BeNull();
        Countries.FromPhoneNumber("").Should().BeNull();
    }
}

public sealed class ContactConsentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_subscribed_contact_carries_a_consent_stamp()
    {
        ContactRules.ConsentStampFor(ContactStatus.Subscribed, Now).Should().Be(Now);
    }

    [Theory]
    [InlineData(ContactStatus.Unsubscribed)]
    [InlineData(ContactStatus.Blocked)]
    public void Unsubscribing_or_blocking_clears_the_consent_stamp(ContactStatus status)
    {
        // Cleared, not left behind. A stale opt-in date on an unsubscribed contact reads to an
        // auditor as consent this platform no longer holds.
        ContactRules.ConsentStampFor(status, Now, existing: Now.AddYears(-1)).Should().BeNull();
    }

    [Fact]
    public void An_existing_consent_stamp_survives_a_status_that_is_still_subscribed()
    {
        var original = Now.AddYears(-1);

        ContactRules.ConsentStampFor(ContactStatus.Subscribed, Now, original).Should().Be(original);
    }

    [Fact]
    public void Moving_from_blocked_to_subscribed_is_refused()
    {
        var act = () => ContactRules.EnsureTransitionAllowed(ContactStatus.Blocked, ContactStatus.Subscribed);

        // The one transition that cannot be an administrative correction: it needs a fresh opt-in,
        // not an operator's say-so.
        act.Should().Throw<BusinessRuleException>();
    }

    [Theory]
    [InlineData(ContactStatus.Blocked, ContactStatus.Unsubscribed)]
    [InlineData(ContactStatus.Unsubscribed, ContactStatus.Subscribed)]
    [InlineData(ContactStatus.Subscribed, ContactStatus.Blocked)]
    public void Every_other_transition_is_allowed(ContactStatus from, ContactStatus to)
    {
        var act = () => ContactRules.EnsureTransitionAllowed(from, to);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_name_that_is_only_whitespace_is_refused()
    {
        var act = () => ContactRules.NormaliseName("   ");

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void A_name_beyond_the_limit_is_refused()
    {
        var act = () => ContactRules.NormaliseName(new string('a', 121));

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void An_email_is_optional_but_must_be_valid_when_present()
    {
        ContactRules.NormaliseEmail(null).Should().BeNull();
        ContactRules.NormaliseEmail("  ").Should().BeNull();
        ContactRules.NormaliseEmail(" a@b.com ").Should().Be("a@b.com");

        var act = () => ContactRules.NormaliseEmail("not-an-address");

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void A_country_that_cannot_be_worked_out_is_refused_rather_than_guessed()
    {
        var act = () => ContactRules.ResolveCountry(null, "999123456");

        // A wrong country is invisible until someone segments a campaign by it and messages the
        // wrong market.
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void A_country_is_derived_from_the_number_when_none_is_supplied()
    {
        ContactRules.ResolveCountry(null, "447700900123").Should().Be("GB");
    }
}
