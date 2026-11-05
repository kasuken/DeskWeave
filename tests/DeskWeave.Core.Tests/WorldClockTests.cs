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
        Assert.Equal(7, snapshot.Left.Count);
        Assert.Equal(8, snapshot.Right.Count);
        Assert.DoesNotContain(snapshot.Left.Concat(snapshot.Right), c => c.City == "Zurich");

        var offsets = snapshot.Left.Concat(snapshot.Right).Select(c => c.OffsetMinutes).ToList();
        Assert.Equal(offsets.Order(), offsets);
        Assert.Equal("San Francisco", snapshot.Left[0].City);
        Assert.Equal("Sydney", snapshot.Right[^1].City);
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
    public void Snapshot_KeepsSharedZoneCitiesAndNamesCenterFromZone()
    {
        var losAngeles = WorldClock.FindZone("America/Los_Angeles");
        var instant = new DateTimeOffset(2026, 7, 1, 20, 0, 0, TimeSpan.Zero);

        var snapshot = WorldClock.Snapshot(instant, losAngeles, "America/Los_Angeles", WorldClock.DefaultCities);

        Assert.Equal("Los Angeles", snapshot.Local.City);
        Assert.Equal("UTC\u221207:00", snapshot.Local.UtcOffset);
        Assert.Equal(16, snapshot.Left.Count + snapshot.Right.Count);
        Assert.Equal(1, snapshot.Right.Single(c => c.City == "Sydney").DayDelta);
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
