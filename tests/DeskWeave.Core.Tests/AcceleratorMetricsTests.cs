using DeskWeave.Core;

namespace DeskWeave.Core.Tests;

public class AcceleratorMetricsTests
{
    private static readonly AdapterKey Gpu = new(0, 0x114f2);

    [Theory]
    [InlineData("pid_12_luid_0x00000000_0x000114F2_phys_0_eng_1_engtype_VideoDecode", "VideoDecode")]
    [InlineData("pid_12_luid_0x00000000_0x000114f2_phys_0_eng_1_engtype_", "")]
    public void Engine_names_preserve_full_adapter_and_engine_identity(string name, string kind)
    {
        var reading = AcceleratorMetrics.ParseEngine(name, 0);
        Assert.NotNull(reading);
        Assert.Equal(Gpu, reading.Adapter);
        Assert.Equal(1, reading.Engine);
        Assert.Equal(kind, reading.Kind);
        Assert.Equal(0, reading.Percent);
    }

    [Theory]
    [InlineData("luid_0x00000000_0x000114f2_phys_0", 5)]
    [InlineData("invalid", 5)]
    [InlineData("pid_1_luid_0x00000000_0x000114f2_phys_0_eng_0_engtype_3D", -1)]
    [InlineData("pid_1_luid_0x00000000_0x000114f2_phys_0_eng_0_engtype_3D", double.NaN)]
    public void Invalid_engine_readings_are_unavailable(string name, double value) =>
        Assert.Null(AcceleratorMetrics.ParseEngine(name, value));

    [Fact]
    public void Processes_sum_per_engine_but_overlapping_engines_use_busiest()
    {
        EngineReading[] readings =
        [
            new(Gpu, 0, 0, "3D", 30), new(Gpu, 0, 0, "3D", 40),
            new(Gpu, 0, 1, "Compute", 65), new(Gpu, 0, 2, "Compute", 50),
            new(Gpu, 1, 0, "3D", 20),
        ];
        var (total, engines) = AcceleratorMetrics.Aggregate(readings);
        Assert.Equal(70, total);
        Assert.Equal(70, engines["3D"]);
        Assert.Equal(65, engines["Compute"]);
    }

    [Fact]
    public void Different_adapters_with_same_engine_number_are_not_added()
    {
        var (total, _) = AcceleratorMetrics.Aggregate(
            [new(Gpu, 0, 0, "3D", 60), new(new(1, 1), 0, 0, "3D", 60)]);
        Assert.Equal(60, total);
    }

    [Fact]
    public void Oversubscribed_process_totals_are_capped_at_100()
    {
        var (total, _) = AcceleratorMetrics.Aggregate([new(Gpu, 0, 0, "3D", 90), new(Gpu, 0, 0, "3D", 30)]);
        Assert.Equal(100, total);
    }

    [Fact]
    public void Empty_or_failed_readings_are_not_idle_zero()
    {
        Assert.Null(AcceleratorMetrics.Aggregate([]).Total);
        Assert.Null(AcceleratorMetrics.Aggregate([new(Gpu, 0, 0, "3D", double.PositiveInfinity)]).Total);
        Assert.Equal(0, AcceleratorMetrics.Aggregate([new(Gpu, 0, 0, "3D", 0)]).Total);
    }

    [Fact]
    public void One_unprimed_process_does_not_make_its_engine_or_overall_load_appear_idle()
    {
        var (total, engines) = AcceleratorMetrics.Aggregate(
            [new(Gpu, 0, 0, "3D", null), new(Gpu, 0, 0, "3D", 0), new(Gpu, 0, 1, "Copy", 0)]);
        Assert.Null(total);
        Assert.False(engines.ContainsKey("3D"));
        Assert.Equal(0, engines["Copy"]);
    }

    [Fact]
    public void Engine_only_available_telemetry_remains_visible()
    {
        var sample = Missing() with { Engines = new Dictionary<string, double> { ["Copy"] = 0 } };
        Assert.True(sample.HasTelemetry);
        Assert.Equal(new AcceleratorRow("Copy", "0%"), Assert.Single(AcceleratorPresentation.Rows(sample)));
    }

    [Fact]
    public void Static_identity_and_capacities_do_not_show_a_section()
    {
        var sample = Missing() with { DedicatedCapacity = 1024, SharedCapacity = 4096 };
        Assert.False(sample.HasTelemetry);
        Assert.Empty(AcceleratorPresentation.Rows(sample));
    }

    [Fact]
    public void Measured_idle_and_stopped_fan_remain_visible_without_unavailable_rows()
    {
        var sample = Missing() with { Utilization = 0, FanRpm = 0 };
        Assert.True(sample.HasTelemetry);
        Assert.Equal([new("Load", "0%"), new AcceleratorRow("Fan", "0 RPM")], AcceleratorPresentation.Rows(sample));
    }

    [Fact]
    public void Dedicated_and_shared_memory_are_separate_and_zero_is_available()
    {
        var sample = Missing() with { DedicatedUsed = 0, DedicatedCapacity = 1024, SharedUsed = 2048, SharedCapacity = 4096 };
        Assert.Equal([new("Dedicated", "0 B / 1.0 KB"), new AcceleratorRow("Shared", "2.0 KB / 4.0 KB")],
            AcceleratorPresentation.Rows(sample));
    }

    [Fact]
    public void Zero_capacity_is_not_presented_as_a_vram_limit()
    {
        var sample = Missing() with { DedicatedUsed = 0, DedicatedCapacity = 0 };
        Assert.Equal("0 B", Assert.Single(AcceleratorPresentation.Rows(sample)).Value);
    }

    [Fact]
    public void Sensor_units_and_stopped_fans_require_valid_capability()
    {
        Assert.Equal((42.5, 23.4, 0d), AcceleratorMetrics.Sensors(425, 234, 0, 4000));
        Assert.Equal((null, null, (double?)null), AcceleratorMetrics.Sensors(0, 0, 0, null));
        Assert.Equal((null, null, (double?)null), AcceleratorMetrics.Sensors(uint.MaxValue, uint.MaxValue, 9000, 4000));
    }

    private static AcceleratorSample Missing() => new("GPU", "GPU", Gpu, null,
        new Dictionary<string, double>(), null, null, null, null, null);
}
