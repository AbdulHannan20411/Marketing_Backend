namespace Marketing.Infrastructure.Places;

/// <summary>A latitude/longitude box, as the Places API expects a rectangle.</summary>
/// <param name="LowLatitude">Southern edge, in degrees.</param>
/// <param name="LowLongitude">Western edge, in degrees.</param>
/// <param name="HighLatitude">Northern edge, in degrees.</param>
/// <param name="HighLongitude">
/// Eastern edge, in degrees. Less than <paramref name="LowLongitude"/> when the box crosses the
/// 180th meridian, which is how Google expresses that.
/// </param>
public sealed record GeoRectangle(double LowLatitude, double LowLongitude, double HighLatitude, double HighLongitude);

/// <summary>
/// The circle a user searched within, and the rectangle a provider can actually be asked about.
/// </summary>
/// <remarks>
/// Google's Text Search only accepts a rectangle as a hard restriction - a circle there is rejected
/// outright as an unknown field. The rectangle that encloses the circle reaches past it at the
/// corners, up to about 41% further from the centre than the radius, so results are trimmed back to
/// the circle afterwards. Together the two keep "within 2 km" meaning within 2 km.
/// <para>
/// Spherical rather than ellipsoidal. At search radii of a few kilometres the difference is metres,
/// far inside what a map pin is accurate to.
/// </para>
/// </remarks>
public static class SearchArea
{
    /// <summary>Mean radius of the Earth, in kilometres.</summary>
    public const double EarthRadiusKm = 6371.0088;

    /// <summary>The smallest latitude/longitude rectangle containing a circle.</summary>
    /// <param name="latitude">Centre latitude, in degrees.</param>
    /// <param name="longitude">Centre longitude, in degrees.</param>
    /// <param name="radiusKm">Radius, in kilometres.</param>
    public static GeoRectangle Bounds(double latitude, double longitude, double radiusKm)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radiusKm);

        var angular = radiusKm / EarthRadiusKm;
        var centre = ToRadians(latitude);
        var low = centre - angular;
        var high = centre + angular;

        // A circle that reaches a pole contains every longitude on that parallel, so the box has to
        // span all of them.
        if (low <= -Math.PI / 2 || high >= Math.PI / 2)
        {
            return new GeoRectangle(
                Math.Max(ToDegrees(low), -90),
                -180,
                Math.Min(ToDegrees(high), 90),
                180);
        }

        // The widest point of the circle is not on the centre's parallel, which is why this is an
        // arcsine rather than the radius divided by the parallel's circumference.
        var spread = ToDegrees(Math.Asin(Math.Sin(angular) / Math.Cos(centre)));

        return new GeoRectangle(
            ToDegrees(low),
            WrapLongitude(longitude - spread),
            ToDegrees(high),
            WrapLongitude(longitude + spread));
    }

    /// <summary>Great-circle distance between two points, in kilometres.</summary>
    /// <param name="fromLatitude">First point's latitude, in degrees.</param>
    /// <param name="fromLongitude">First point's longitude, in degrees.</param>
    /// <param name="toLatitude">Second point's latitude, in degrees.</param>
    /// <param name="toLongitude">Second point's longitude, in degrees.</param>
    public static double DistanceKm(double fromLatitude, double fromLongitude, double toLatitude, double toLongitude)
    {
        var latitudeDelta = ToRadians(toLatitude - fromLatitude);
        var longitudeDelta = ToRadians(toLongitude - fromLongitude);

        var halfChord = (Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2))
                        + (Math.Cos(ToRadians(fromLatitude)) * Math.Cos(ToRadians(toLatitude))
                           * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2));

        // Clamped: rounding can push the chord a hair past 1 for near-antipodal points, and the
        // arcsine of that is not a number.
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(halfChord)));
    }

    /// <summary>Whether a point lies within a circle.</summary>
    /// <param name="centreLatitude">Centre latitude, in degrees.</param>
    /// <param name="centreLongitude">Centre longitude, in degrees.</param>
    /// <param name="radiusKm">Radius, in kilometres.</param>
    /// <param name="latitude">The point's latitude, in degrees.</param>
    /// <param name="longitude">The point's longitude, in degrees.</param>
    public static bool Contains(
        double centreLatitude,
        double centreLongitude,
        double radiusKm,
        double latitude,
        double longitude) =>
        DistanceKm(centreLatitude, centreLongitude, latitude, longitude) <= radiusKm;

    private static double WrapLongitude(double longitude)
    {
        var wrapped = (longitude + 180) % 360;

        if (wrapped < 0)
        {
            wrapped += 360;
        }

        return wrapped - 180;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    private static double ToDegrees(double radians) => radians * 180 / Math.PI;
}
