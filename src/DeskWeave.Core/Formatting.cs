using System.Globalization;

namespace DeskWeave.Core;

public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public const string Unavailable = "N/A";

    public static string Bytes(double? bytes)
    {
        if (bytes is not { } value || double.IsNaN(value) || value < 0)
        {
            return Unavailable;
        }

        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 || value >= 100 ? "0" : "0.0";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string Rate(double? bytesPerSecond) =>
        bytesPerSecond is null ? Unavailable : Bytes(bytesPerSecond) + "/s";

    public static string Percent(double? percent) =>
        percent is { } value ? value.ToString("0", CultureInfo.InvariantCulture) + "%" : Unavailable;

    public static string Temperature(double? celsius) =>
        celsius is { } value ? value.ToString("0", CultureInfo.InvariantCulture) + "°C" : Unavailable;
}
