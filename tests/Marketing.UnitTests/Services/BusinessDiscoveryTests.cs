using System.Reflection;
using AwesomeAssertions;
using Marketing.Application.DTOs.BusinessDiscovery;
using Marketing.Application.Services.BusinessDiscovery;
using Marketing.Common.Exceptions;

namespace Marketing.UnitTests.Services;

public sealed class BusinessCategoryTests
{
    [Fact]
    public void Every_category_maps_to_a_provider_type()
    {
        // A slug the picker offers but the provider cannot be asked about is a category that
        // silently returns nothing, which reads as "there are no barbers here".
        BusinessCategories.All.Should().NotBeEmpty();

        foreach (var category in BusinessCategories.All)
        {
            BusinessCategories.ToProviderType(category.Id).Should().NotBeNullOrWhiteSpace(
                $"category '{category.Id}' is offered to users");
        }
    }

    [Fact]
    public void Category_slugs_are_unique()
    {
        // Two entries sharing a slug means one is unreachable, and which one is decided by
        // dictionary ordering rather than by anybody's intent.
        BusinessCategories.All.Select(category => category.Id)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void An_unknown_category_is_rejected_rather_than_passed_through()
    {
        // Passing an unrecognised slug to the provider would spend a billable call to be told
        // nothing, and the user would see an empty result rather than "pick a category".
        BusinessCategories.IsKnown("not_a_category").Should().BeFalse();
        BusinessCategories.IsKnown(null).Should().BeFalse();
        BusinessCategories.IsKnown("  ").Should().BeFalse();
        BusinessCategories.ToProviderType("not_a_category").Should().BeNull();
    }

    [Fact]
    public void Slugs_are_matched_regardless_of_case()
    {
        BusinessCategories.IsKnown("BARBER").Should().BeTrue();
        BusinessCategories.ToProviderType("Barber").Should().Be(BusinessCategories.ToProviderType("barber"));
    }
}

public sealed class BusinessSearchValidationTests
{
    private static readonly MethodInfo ValidateMethod =
        typeof(BusinessDiscoveryService).GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo FingerprintMethod =
        typeof(BusinessDiscoveryService).GetMethod("Fingerprint", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void Validate(BusinessSearchRequest request)
    {
        try
        {
            ValidateMethod.Invoke(null, [request]);
        }
        catch (TargetInvocationException invocation) when (invocation.InnerException is not null)
        {
            throw invocation.InnerException;
        }
    }

    private static BusinessSearchRequest Request(
        double latitude = 31.5204,
        double longitude = 74.3587,
        double radiusKm = 5,
        string category = "barber",
        int page = 1,
        int pageSize = 25) =>
        new(latitude, longitude, radiusKm, category, page, pageSize);

    private static string Fingerprint(double latitude, double longitude) =>
        (string)FingerprintMethod.Invoke(null, [latitude, longitude, 5d, "barber", 1, 25])!;

    [Fact]
    public void A_radius_beyond_the_ceiling_is_refused_with_its_own_code()
    {
        // The client offers 1/2/5/10/20/50, but that dropdown is a convenience for the user, not a
        // constraint on the caller - and a 5,000 km radius is a very expensive provider call.
        var act = () => Validate(Request(radiusKm: 500));

        act.Should().Throw<BusinessRuleException>()
            .Which.ErrorCode.Should().Be("radius_too_large");
    }

    [Theory]
    [InlineData(91, 0)]
    [InlineData(-91, 0)]
    [InlineData(0, 181)]
    [InlineData(0, -181)]
    public void Coordinates_outside_the_world_are_refused(double latitude, double longitude)
    {
        var act = () => Validate(Request(latitude: latitude, longitude: longitude));

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void An_unknown_category_is_refused()
    {
        var act = () => Validate(Request(category: "spaceport"));

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void A_page_size_beyond_the_ceiling_is_clamped_rather_than_refused()
    {
        // Clamped, because an oversized page is the caller asking for too much rather than asking
        // for something wrong, and refusing would fail a search the user could otherwise have.
        Validate(Request(pageSize: 5000));
    }

    [Fact]
    public void A_pin_nudged_a_few_metres_reuses_the_same_search()
    {
        // Rounded to about a hundred metres. Without this every drag of the map marker is a fresh
        // provider charge for what is, to the person doing it, the same search.
        Fingerprint(31.52041, 74.35872).Should().Be(Fingerprint(31.52045, 74.35874));
    }

    [Fact]
    public void A_meaningfully_different_point_is_a_different_search()
    {
        Fingerprint(31.5204, 74.3587).Should().NotBe(Fingerprint(31.6204, 74.3587));
    }
}
