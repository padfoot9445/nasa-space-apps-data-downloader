namespace FireData.Cli;

internal readonly record struct LongitudeInterval(double West, double East);
internal readonly record struct SpatialEnvelope(
    double South,
    double North,
    IReadOnlyList<LongitudeInterval> LongitudeIntervals);

internal static class SpatialMath
{
    private const double MeanEarthRadiusMeters = 6_371_008.8;

    public static long ToUnixMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return checked((utc.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10);
    }

    public static double DistanceMeters(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var lat1 = DegreesToRadians(latitude1);
        var lat2 = DegreesToRadians(latitude2);
        var deltaLat = lat2 - lat1;
        var deltaLon = DegreesToRadians(longitude2 - longitude1);
        var sinLat = Math.Sin(deltaLat / 2);
        var sinLon = Math.Sin(deltaLon / 2);
        var a = sinLat * sinLat + Math.Cos(lat1) * Math.Cos(lat2) * sinLon * sinLon;
        var boundedA = Math.Clamp(a, 0, 1);
        return 2 * MeanEarthRadiusMeters * Math.Asin(Math.Sqrt(boundedA));
    }

    public static SpatialEnvelope EnvelopeForRadius(
        double latitude,
        double longitude,
        double radiusMeters)
    {
        if (latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), "Latitude must be within [-90, 90].");
        }

        if (longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), "Longitude must be within [-180, 180].");
        }

        if (!double.IsFinite(radiusMeters) || radiusMeters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusMeters), "Radius must be non-negative.");
        }

        var angularRadius = radiusMeters / MeanEarthRadiusMeters;
        var latitudeRadians = DegreesToRadians(latitude);
        var latitudeDelta = angularRadius * (180 / Math.PI);
        var south = Math.Max(-90, latitude - latitudeDelta);
        var north = Math.Min(90, latitude + latitudeDelta);
        var reachesPole = angularRadius >= Math.PI / 2 - Math.Abs(latitudeRadians);
        if (reachesPole)
        {
            return new SpatialEnvelope(south, north, [new LongitudeInterval(-180, 180)]);
        }

        var longitudeDelta = Math.Asin(
            Math.Sin(angularRadius) / Math.Cos(latitudeRadians)) * (180 / Math.PI);
        if (longitudeDelta >= 180)
        {
            return new SpatialEnvelope(south, north, [new LongitudeInterval(-180, 180)]);
        }

        var west = longitude - longitudeDelta;
        var east = longitude + longitudeDelta;
        IReadOnlyList<LongitudeInterval> intervals = west < -180
            ? [new LongitudeInterval(-180, east), new LongitudeInterval(west + 360, 180)]
            : east > 180
                ? [new LongitudeInterval(-180, east - 360), new LongitudeInterval(west, 180)]
                : [new LongitudeInterval(west, east)];
        return new SpatialEnvelope(south, north, intervals);
    }

    private static double DegreesToRadians(double value) => value * (Math.PI / 180);
}
