using Windows.Devices.Geolocation;

namespace AIClockBridge;

static class WindowsLocation
{
    public sealed record Result(double Latitude, double Longitude, string Label);

    public static async Task<Result> Locate()
    {
        var access = await Geolocator.RequestAccessAsync();
        if (access != GeolocationAccessStatus.Allowed)
            throw new InvalidOperationException("Windows 定位权限未开启，请在系统“位置”设置中允许桌面应用访问位置。 ");

        var locator = new Geolocator { DesiredAccuracyInMeters = 1000 };
        var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(12));
        var point = position.Coordinate.Point.Position;
        return new Result(point.Latitude, point.Longitude,
            $"{point.Latitude:F4}, {point.Longitude:F4}");
    }
}
