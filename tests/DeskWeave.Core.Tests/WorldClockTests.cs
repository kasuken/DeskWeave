using DeskWeave.Core;

namespace DeskWeave.Core.Tests;

public class WorldClockTests
{
    private static readonly TimeZoneInfo Zurich = WorldClock.FindZone("Europe/Zurich");

    [Fact]
    public void Snapshot_CentersUniqueLocalCityAndSplitsTheRest()
    {
        var instant = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        var snapshot = WorldClock.Snapshot(instant, Zurich, "Europe/Zurich", WorldClock.DefaultCities);

        Assert.Equal("Zurich", snapshot.Local.City);
        Assert.Equal("13:00:00", snapshot.Local.SecondsText);
        Assert.Equal("UTC+01:00", snapshot.Local.UtcOffset);
        Assert.Equal(8, snapshot.Left.Count);
        Assert.Equal(8, snapshot.Right.Count);
        Assert.DoesNotContain(snapshot.Left.Concat(snapshot.Right), c => c.City == "Zurich");

        var offsets = snapshot.Left.Concat(snapshot.Right).Select(c => c.OffsetMinutes).ToList();
        Assert.Equal(offsets.Order(), offsets);
        Assert.Equal("Honolulu", snapshot.Left[0].City);
        Assert.Equal("Auckland", snapshot.Right[^1].City);
    }

    [Fact]
    public void Snapshot_MarksDayDeltaRelativeToLocalDate()
    {
        var instant = new DateTimeOffset(2026, 1, 15, 22, 30, 0, TimeSpan.Zero);

        var snapshot = WorldClock.Snapshot(instant, Zurich, "Europe/Zurich", WorldClock.DefaultCities);
        var all = snapshot.Left.Concat(snapshot.Right).ToDictionary(c => c.City);

        Assert.Equal(1, all["Tokyo"].DayDelta);
        Assert.Equal("+1d", all["Tokyo"].DayDeltaText);
        Assert.Equal(0, all["New York"].DayDelta);
        Assert.Equal(string.Empty, all["New York"].DayDeltaText);
    }

    [Fact]
    public void Snapshot_CentersSanFranciscoWithoutDuplicatingItsZone()
    {
        var losAngeles = WorldClock.FindZone("America/Los_Angeles");
        var instant = new DateTimeOffset(2026, 7, 1, 20, 0, 0, TimeSpan.Zero);

        var snapshot = WorldClock.Snapshot(instant, losAngeles, "America/Los_Angeles", WorldClock.DefaultCities);

        Assert.Equal("San Francisco", snapshot.Local.City);
        Assert.Equal("UTC\u221207:00", snapshot.Local.UtcOffset);
        Assert.Equal(16, snapshot.Left.Count + snapshot.Right.Count);
        Assert.Equal(1, snapshot.Right.Single(c => c.City == "Sydney").DayDelta);
    }

    [Fact]
    public void Snapshot_PreservesPreviousDayDateAndMarker()
    {
        var instant = new DateTimeOffset(2026, 1, 15, 0, 30, 0, TimeSpan.Zero);

        var snapshot = WorldClock.Snapshot(instant, Zurich, "Europe/Zurich", WorldClock.DefaultCities);
        var honolulu = snapshot.Left.Single(c => c.City == "Honolulu");

        Assert.Equal("Thu 15 Jan", snapshot.Local.DateText);
        Assert.Equal("Wed 14 Jan", honolulu.DateText);
        Assert.Equal(-1, honolulu.DayDelta);
        Assert.Equal("-1d", honolulu.DayDeltaText);
    }

    public static IEnumerable<object[]> LocalZonesAndSeasons()
    {
        var zones = WorldClock.DefaultCities.Select(c => c.IanaId)
            .Concat(["Etc/UTC", "Asia/Kathmandu", "Pacific/Kiritimati", "Pacific/Pago_Pago"]);
        foreach (var zone in zones)
        {
            foreach (var month in new[] { 1, 3, 7, 10 })
            {
                yield return [zone, month];
            }
        }
    }

    [Theory]
    [MemberData(nameof(LocalZonesAndSeasons))]
    public void Snapshot_DefaultCitiesAlwaysProduceEightSortedClocksPerWing(string zoneId, int month)
    {
        var instant = new DateTimeOffset(2026, month, 28, 12, 0, 0, TimeSpan.Zero);
        var zone = WorldClock.FindZone(zoneId);

        var snapshot = WorldClock.Snapshot(instant, zone, zoneId, WorldClock.DefaultCities);
        var clocks = snapshot.Left.Concat(snapshot.Right).ToList();

        Assert.Equal(8, snapshot.Left.Count);
        Assert.Equal(8, snapshot.Right.Count);
        Assert.Equal(16, clocks.Select(c => c.Zone).Distinct().Count());
        Assert.DoesNotContain(clocks, c => c.Zone == zoneId);
        Assert.Equal(clocks.Select(c => c.OffsetMinutes).Order(), clocks.Select(c => c.OffsetMinutes));
    }

    [Fact]
    public void Snapshot_UnlistedLocalZoneOmitsNearestOffsetCity()
    {
        var instant = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        var snapshot = WorldClock.Snapshot(instant, TimeZoneInfo.Utc, null, WorldClock.DefaultCities);
        var clocks = snapshot.Left.Concat(snapshot.Right).ToList();

        Assert.Equal("UTC", snapshot.Local.City);
        Assert.DoesNotContain(clocks, c => c.City == "London");
        Assert.Contains(clocks, c => c.City == "Honolulu");
        Assert.Contains(clocks, c => c.City == "Auckland");
    }

    [Fact]
    public void Snapshot_SharedLocalZoneStillProducesBalancedWings()
    {
        City[] cities =
        [
            new("San Francisco", "America/Los_Angeles"),
            new("Seattle", "America/Los_Angeles"),
            new("Tokyo", "Asia/Tokyo"),
            new("Sydney", "Australia/Sydney"),
        ];
        var instant = new DateTimeOffset(2026, 7, 1, 20, 0, 0, TimeSpan.Zero);

        var snapshot = WorldClock.Snapshot(instant, WorldClock.FindZone("America/Los_Angeles"),
            "America/Los_Angeles", cities);

        Assert.Equal("Los Angeles", snapshot.Local.City);
        Assert.Equal(2, snapshot.Left.Count);
        Assert.Equal(2, snapshot.Right.Count);
        Assert.Equal(["San Francisco", "Seattle"], snapshot.Left.Select(c => c.City));
    }

    [Theory]
    [InlineData(1, "UTC+01:00", "UTC\u221205:00", "UTC+11:00")]
    [InlineData(7, "UTC+02:00", "UTC\u221204:00", "UTC+10:00")]
    public void Snapshot_PreservesSeasonalOffsets(int month, string localOffset, string newYorkOffset, string sydneyOffset)
    {
        var instant = new DateTimeOffset(2026, month, 15, 12, 0, 0, TimeSpan.Zero);

        var snapshot = WorldClock.Snapshot(instant, Zurich, "Europe/Zurich", WorldClock.DefaultCities);
        var clocks = snapshot.Left.Concat(snapshot.Right).ToDictionary(c => c.City);

        Assert.Equal(localOffset, snapshot.Local.UtcOffset);
        Assert.Equal(newYorkOffset, clocks["New York"].UtcOffset);
        Assert.Equal(sydneyOffset, clocks["Sydney"].UtcOffset);
    }

    [Theory]
    [InlineData(0, "UTC+00:00")]
    [InlineData(330, "UTC+05:30")]
    [InlineData(-420, "UTC\u221207:00")]
    public void OffsetText_FormatsSignedHoursAndMinutes(int minutes, string expected) =>
        Assert.Equal(expected, WorldClock.OffsetText(minutes));

    [Fact]
    public void LocalIanaId_UsesRegionSpecificMapping()
    {
        var westEurope = TimeZoneInfo.CreateCustomTimeZone("W. Europe Standard Time", TimeSpan.FromHours(1), "W. Europe", "W. Europe");

        Assert.Equal("Europe/Zurich", WorldClock.LocalIanaId(westEurope, "CH"));
        Assert.Equal("Europe/Berlin", WorldClock.LocalIanaId(westEurope, null));
    }
}
