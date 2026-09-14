using AwesomeAssertions;
using Marketing.Infrastructure.Places;

namespace Marketing.UnitTests.Infrastructure;

/// <summary>
/// Turning a searched circle into the rectangle Google accepts, and trimming back to the circle.
/// </summary>
/// <remarks>
/// Written after every business search failed with a 400: the provider sent a circle as a location
/// restriction, which Google's Text Search rejects as an unknown field. It only restricts to a
/// rectangle - and a rectangle around a circle reaches past it at the corners.
/// </remarks>
public sealed class SearchAreaTests
{
    private const double Latitude = 31.408003575722404;
    private const double Longitude = 74.20416541636631;
    private const double RadiusKm = 2;

    /// <summary>The point a given distance away on a given bearing, on the same sphere the helper uses.</summary>
    private static (double Latitude, double Longitude) Destination(
        double latitude,
        double longitude,
        double distanceKm,
        double bearingDegrees)
    {
        var angular = distanceKm / SearchArea.EarthRadiusKm;
        var bearing = bearingDegrees * Math.PI / 180;
        var fromLatitude = latitude * Math.PI / 180;
        var fromLongitude = longitude * Math.PI / 180;

        var toLatitude = Math.Asin(
            (Math.Sin(fromLatitude) * Math.Cos(angular))
            + (Math.Cos(fromLatitude) * Math.Sin(angular) * Math.Cos(bearing)));

        var toLongitude = fromLongitude + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angular) * Math.Cos(fromLatitude),
            Math.Cos(angular) - (Math.Sin(fromLatitude) * Math.Sin(toLatitude)));

        return (toLatitude * 180 / Math.PI, toLongitude * 180 / Math.PI);
    }

    [Fact]
    public void Every_point_of_the_circle_lies_inside_the_rectangle()
    {
        // The property the request depends on: nothing the user asked for may fall outside what
        // Google is told to search.
        var bounds = SearchArea.Bounds(Latitude, Longitude, RadiusKm);

        for (var bearing = 0; bearing < 360; bearing += 5)
        {
            var (latitude, longitude) = Destination(Latitude, Longitude, RadiusKm * 0.999, bearing);

            latitude.Should().BeInRange(bounds.LowLatitude, bounds.HighLatitude, $"bearing {bearing}");
            longitude.Should().BeInRange(bounds.LowLongitude, bounds.HighLongitude, $"bearing {bearing}");
        }
    }

    [Fact]
    public void The_corners_of_the_rectangle_are_outside_the_circle()
    {
        // Why results have to be trimmed: a corner is roughly the radius times the square root of two
        // from the centre, well beyond what the user asked for.
        var bounds = SearchArea.Bounds(Latitude, Longitude, RadiusKm);

        SearchArea.Contains(Latitude, Longitude, RadiusKm, bounds.HighLatitude, bounds.HighLongitude).Should().BeFalse();

        SearchArea.DistanceKm(Latitude, Longitude, bounds.HighLatitude, bounds.HighLongitude)
            .Should().BeApproximately(RadiusKm * Math.Sqrt(2), RadiusKm * 0.02);
    }

    [Fact]
    public void A_degree_of_latitude_is_about_111_kilometres()
    {
        SearchArea.DistanceKm(31, 74, 32, 74).Should().BeApproximately(111.195, 0.05);
    }

    [Fact]
    public void A_point_just_inside_the_radius_is_kept_and_one_just_beyond_it_is_trimmed()
    {
        var inside = Destination(Latitude, Longitude, RadiusKm * 0.99, 45);
        var beyond = Destination(Latitude, Longitude, RadiusKm * 1.01, 45);

        SearchArea.Contains(Latitude, Longitude, RadiusKm, Latitude, Longitude).Should().BeTrue();
        SearchArea.Contains(Latitude, Longitude, RadiusKm, inside.Latitude, inside.Longitude).Should().BeTrue();
        SearchArea.Contains(Latitude, Longitude, RadiusKm, beyond.Latitude, beyond.Longitude).Should().BeFalse();
    }

    [Fact]
    public void A_place_with_no_coordinates_is_not_inside_a_search_elsewhere()
    {
        // The provider reads a missing location as (0, 0). A business that cannot be placed cannot be
        // shown to be inside the circle, so it must not survive the trim.
        SearchArea.Contains(Latitude, Longitude, RadiusKm, 0, 0).Should().BeFalse();
    }

    [Fact]
    public void Near_a_pole_the_rectangle_spans_every_longitude()
    {
        var bounds = SearchArea.Bounds(89.99, 10, 5);

        bounds.LowLongitude.Should().Be(-180);
        bounds.HighLongitude.Should().Be(180);
        bounds.HighLatitude.Should().Be(90);
    }

    [Fact]
    public void Across_the_180th_meridian_the_longitude_range_wraps()
    {
        // Google reads a low longitude greater than the high one as a box crossing the meridian.
        var bounds = SearchArea.Bounds(0, 179.99, 5);

        bounds.LowLongitude.Should().BeGreaterThan(bounds.HighLongitude);
        bounds.LowLongitude.Should().BeInRange(179, 180);
        bounds.HighLongitude.Should().BeInRange(-180, -179);
    }
}
