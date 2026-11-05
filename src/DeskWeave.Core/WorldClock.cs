using System.Globalization;

namespace DeskWeave.Core;

public sealed record City(string Name, string IanaId);

public sealed record ClockReading(
    string City,
    string Zone,
    string TimeText,
    string SecondsText,
    string DateText,
    string UtcOffset,
    int OffsetMinutes,
    int DayDelta)
{
    public string DayDeltaText => DayDelta switch
    {
        0 => string.Empty,
        > 0 => $"+{DayDelta}d",
        _ => $"{DayDelta}d",
    };
}

public sealed record WorldClockSnapshot(ClockReading Local, IReadOnlyList<ClockReading> Left, IReadOnlyList<ClockReading> Right);

public static class WorldClock
{
    public static IReadOnlyList<City> DefaultCities { get; } =
    [
        new("San Francisco", "America/Los_Angeles"),
        new("Seattle", "America/Los_Angeles"),
        new("New York", "America/New_York"),
        new("London", "Europe/London"),
        new("Madrid", "Europe/Madrid"),
        new("Rome", "Europe/Rome"),
        new("Prague", "Europe/Prague"),
        new("Zurich", "Europe/Zurich"),
        new("Warsaw", "Europe/Warsaw"),
        new("Bucharest", "Europe/Bucharest"),
        new("Moscow", "Europe/Moscow"),
        new("Dubai", "Asia/Dubai"),
        new("Mumbai", "Asia/Kolkata"),
        new("Bali", "Asia/Makassar"),
        new("Tokyo", "Asia/Tokyo"),
        new("Sydney", "Australia/Sydney"),
    ];

    public static TimeZoneInfo FindZone(string ianaId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (TimeZoneNotFoundException) when (TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaId, out var windowsId))
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
    }

    /// <summary>Resolves the IANA name of a (Windows) zone, preferring the region-specific mapping (e.g. CH → Europe/Zurich).</summary>
    public static string? LocalIanaId(TimeZoneInfo zone, string? region)
    {
        if (zone.HasIanaId)
        {
            return zone.Id;
        }

        if (!string.IsNullOrEmpty(region) && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, region, out var regional))
        {
            return regional;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var fallback) ? fallback : null;
    }

    public static WorldClockSnapshot Snapshot(DateTimeOffset instant, TimeZoneInfo localZone, string? localIanaId, IReadOnlyList<City> cities)
    {
        var localDate = TimeZoneInfo.ConvertTime(instant, localZone).Date;
        var zoneName = localIanaId ?? localZone.Id;
        var matches = cities.Where(c => c.IanaId == zoneName).ToList();
        var unique = matches.Count == 1;

        // A uniquely matching city becomes the centered clock instead of appearing twice.
        var localCity = unique ? matches[0].Name : zoneName.Split('/')[^1].Replace('_', ' ');
        var center = Read(localCity, zoneName, localZone, instant, localDate);

        var rows = cities
            .Where(c => !(unique && c.IanaId == zoneName))
            .Select(c => Read(c.Name, c.IanaId, FindZone(c.IanaId), instant, localDate))
            .OrderBy(r => r.OffsetMinutes)
            .ToList();

        var half = rows.Count / 2;
        return new WorldClockSnapshot(center, rows[..half], rows[half..]);
    }

    public static string OffsetText(int minutes)
    {
        var sign = minutes >= 0 ? "+" : "\u2212";
        var abs = Math.Abs(minutes);
        return $"UTC{sign}{abs / 60:00}:{abs % 60:00}";
    }

    private static ClockReading Read(string city, string zoneName, TimeZoneInfo zone, DateTimeOffset instant, DateTime localDate)
    {
        var civil = TimeZoneInfo.ConvertTime(instant, zone);
        var minutes = (int)Math.Round(civil.Offset.TotalMinutes);
        var culture = CultureInfo.InvariantCulture;
        return new ClockReading(
            city,
            zoneName,
            civil.ToString("HH:mm", culture),
            civil.ToString("HH:mm:ss", culture),
            civil.ToString("ddd dd MMM", culture),
            OffsetText(minutes),
            minutes,
            (civil.Date - localDate).Days);
    }
}
