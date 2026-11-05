using System.Globalization;
using System.Text.RegularExpressions;

namespace DeskWeave.Core;

/// <summary>Adapter identity shared by DXCore and Windows performance counters.</summary>
public readonly record struct AdapterKey(uint High, uint Low);

/// <summary>A single process's contribution to a physical engine's utilization.</summary>
public sealed record EngineReading(AdapterKey Adapter, int Physical, int Engine, string Kind, double? Percent);

/// <summary>Available accelerator readings. Null means unsupported, warming up, or unreadable, never idle.</summary>
public sealed record AcceleratorSample(
    string Name, string Kind, AdapterKey Adapter,
    double? Utilization, IReadOnlyDictionary<string, double> Engines,
    double? DedicatedUsed, double? DedicatedCapacity,
    double? SharedUsed, double? SharedCapacity, double? Temperature,
    double? PowerPercent = null, double? FanRpm = null)
{
    /// <summary>Diagnostic failures, separate from unsupported/null metrics; never rendered as synthetic values.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Identity and static capacity alone do not make a live telemetry section visible.</summary>
    public bool HasTelemetry => Utilization.HasValue || Engines.Count > 0 || DedicatedUsed.HasValue || SharedUsed.HasValue
        || Temperature.HasValue || PowerPercent.HasValue || FanRpm.HasValue;
}

/// <summary>Parsing and aggregation of Windows GPU Engine counters (also used by supported NPUs).</summary>
public static partial class AcceleratorMetrics
{
    /// <summary>Normalizes WDDM's deci-Celsius, relative power and optional fan capability.</summary>
    public static (double? Temperature, double? PowerPercent, double? FanRpm) Sensors(
        uint temperature, uint power, uint fanRpm, uint? maxFanRpm) =>
        (temperature is > 0 and < 1500 ? temperature / 10d : null,
            power is > 0 and < 10000 ? power / 10d : null,
            maxFanRpm is > 0 && fanRpm <= maxFanRpm ? fanRpm : null);

    /// <summary>Reads the complete LUID, not a device ordinal or vendor/name heuristic.</summary>
    public static AdapterKey? ParseAdapter(string instance)
    {
        var match = AdapterPattern().Match(instance);
        return match.Success
            ? new AdapterKey(uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                uint.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>Rejects invalid samples without turning them into measured zero.</summary>
    public static EngineReading? ParseEngine(string instance, double percent)
    {
        var match = EnginePattern().Match(instance);
        var adapter = ParseAdapter(instance);
        if (adapter is null || !match.Success || !double.IsFinite(percent) || percent < 0
            || !int.TryParse(match.Groups[1].Value, out var physical)
            || !int.TryParse(match.Groups[2].Value, out var engine))
        {
            return null;
        }

        return new EngineReading(adapter.Value, physical, engine, match.Groups[3].Value, percent);
    }

    /// <summary>
    /// Sum processes on the same physical engine, then take the busiest engine.
    /// Different engines can execute concurrently and must not be added together.
    /// </summary>
    public static (double? Total, IReadOnlyDictionary<string, double> Engines) Aggregate(IEnumerable<EngineReading> readings)
    {
        var engines = readings.GroupBy(r => (r.Adapter, r.Physical, r.Engine))
            .Select(g => (Kind: g.First().Kind,
                Percent: g.All(r => r.Percent is { } p && double.IsFinite(p) && p >= 0)
                    ? (double?)Math.Min(100, g.Sum(r => r.Percent.GetValueOrDefault())) : null))
            .ToArray();
        var kinds = engines.Where(e => e.Kind.Length > 0)
            .GroupBy(e => e.Kind, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.All(e => e.Percent.HasValue))
            .ToDictionary(g => g.Key, g => g.Max(e => e.Percent.GetValueOrDefault()), StringComparer.OrdinalIgnoreCase);
        return (engines.Length == 0 || engines.Any(e => e.Percent is null) ? null : engines.Max(e => e.Percent), kinds);
    }

    [GeneratedRegex(@"(?:^|_)luid_0x([0-9a-f]{8})_0x([0-9a-f]{8})(?:_|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdapterPattern();

    [GeneratedRegex(@"_phys_(\d+)_eng_(\d+)_engtype_(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnginePattern();
}
