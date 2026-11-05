using DeskWeave.Core;

namespace DeskWeave.Core.Tests;

public class MetricsTests
{
    [Fact]
    public void PerSecond_ReturnsNullForFirstSampleAndCounterReset()
    {
        Assert.Null(Rates.PerSecond(null, 100, TimeSpan.FromSeconds(1)));
        Assert.Null(Rates.PerSecond(500, 100, TimeSpan.FromSeconds(1)));
        Assert.Equal(1024, Rates.PerSecond(0, 2048, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void CpuUsage_TreatsKernelTimeAsIncludingIdle()
    {
        var before = new CpuTimes(Idle: 0, Kernel: 0, User: 0);
        var after = new CpuTimes(Idle: 600, Kernel: 800, User: 200);

        Assert.Equal(40, Rates.CpuUsage(before, after));
        Assert.Null(Rates.CpuUsage(null, after));
        Assert.Null(Rates.CpuUsage(after, after));
    }

    [Fact]
    public void MetricHistory_KeepsNewestClampedSamples()
    {
        var history = new MetricHistory(3);
        foreach (var value in new double?[] { 10, null, 20, 150, -5 })
        {
            history.Add(value);
        }

        Assert.Equal([20, 100, 0], history.Samples);
    }

    [Theory]
    [InlineData(null, "N/A")]
    [InlineData(512d, "512 B")]
    [InlineData(1536d, "1.5 KB")]
    [InlineData(250d * 1024 * 1024, "250 MB")]
    public void Bytes_UsesBinaryUnits(double? bytes, string expected) =>
        Assert.Equal(expected, Formatting.Bytes(bytes));

    [Fact]
    public void Formatting_HandlesMissingValues()
    {
        Assert.Equal("N/A", Formatting.Rate(null));
        Assert.Equal("2.0 KB/s", Formatting.Rate(2048));
        Assert.Equal("42%", Formatting.Percent(42.4));
        Assert.Equal("N/A", Formatting.Temperature(null));
        Assert.Equal("55°C", Formatting.Temperature(54.6));
    }
}
