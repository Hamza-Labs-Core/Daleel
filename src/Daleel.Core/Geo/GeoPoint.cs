namespace Daleel.Core.Geo;

/// <summary>A latitude/longitude coordinate used for proximity search.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    public override string ToString() =>
        $"{Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
        $"{Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
