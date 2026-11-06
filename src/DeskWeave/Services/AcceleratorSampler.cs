using System.ComponentModel;
using System.Diagnostics;
using DeskWeave.Core;
using DeskWeave.Interop;

namespace DeskWeave.Services;

/// <summary>Single-threaded, read-only accelerator telemetry used by the system sampling loop.</summary>
public sealed class AcceleratorSampler
{
    private IReadOnlyList<DxCoreAdapters.Adapter> _adapters = [];
    private readonly Dictionary<string, CounterSample> _previousEngines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _discoveredAt;
    private readonly Dictionary<string, string> _failures = [];

    /// <summary>Samples all discoverable hardware adapters. Failures are nullable and retried on subsequent polls.</summary>
    public IReadOnlyList<AcceleratorSample> Sample()
    {
        if (_discoveredAt is null || _clock.Elapsed - _discoveredAt.Value >= TimeSpan.FromSeconds(30))
        {
            _adapters = DxCoreAdapters.Discover();
            _discoveredAt = _clock.Elapsed;
        }

        if (_adapters.Count == 0) return [];
        var engineData = ReadCategory("GPU Engine");
        var memoryData = ReadCategory("GPU Adapter Memory");
        var readings = new List<EngineReading>();
        var next = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);
        var calculationFailed = false;
        if (engineData?["Utilization Percentage"] is { } utilization)
        {
            foreach (InstanceData instance in utilization.Values)
            {
                var current = instance.Sample;
                next[instance.InstanceName] = current;
                var identity = AcceleratorMetrics.ParseEngine(instance.InstanceName, 0);
                if (identity is null) continue;
                double? percent = null;
                if (_previousEngines.TryGetValue(instance.InstanceName, out var previous)
                    && current.TimeStamp > previous.TimeStamp && current.RawValue >= previous.RawValue
                    && current.CounterType == previous.CounterType)
                {
                    try
                    {
                        var value = CounterSample.Calculate(previous, current);
                        if (double.IsFinite(value) && value >= 0) percent = value;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ArgumentException)
                    {
                        calculationFailed = true;
                        ReportFailure("GPU Engine calculation", ex.Message);
                    }
                }
                // An unprimed/reset process makes its whole engine unknown, not artificially idle.
                readings.Add(identity with { Percent = percent });
            }
        }
        if (!calculationFailed) _failures.Remove("GPU Engine calculation");
        _previousEngines.Clear();
        foreach (var pair in next) _previousEngines.Add(pair.Key, pair.Value);

        return _adapters.Select(adapter =>
        {
            var (total, engines) = AcceleratorMetrics.Aggregate(readings.Where(r => r.Adapter == adapter.Key));
            var sensors = AdapterSensors.Read(adapter.Key);
            var sensorKey = $"Sensors {adapter.Key}";
            if (sensors.Status < 0)
                ReportFailure(sensorKey, $"WDDM query status 0x{sensors.Status:X8}");
            else
                _failures.Remove(sensorKey);
            return new AcceleratorSample(adapter.Name, adapter.Kind, adapter.Key, total, engines,
                Memory(memoryData, "Dedicated Usage", adapter.Key), adapter.Dedicated,
                Memory(memoryData, "Shared Usage", adapter.Key), adapter.Shared,
                sensors.Temperature, sensors.PowerPercent, sensors.FanRpm)
            {
                Errors = _failures.Where(f => f.Key.StartsWith("GPU ", StringComparison.Ordinal) || f.Key == sensorKey)
                    .Select(f => $"{f.Key}: {f.Value}").ToArray(),
            };
        }).ToArray();
    }

    private InstanceDataCollectionCollection? ReadCategory(string name)
    {
        try
        {
            var result = new PerformanceCounterCategory(name).ReadCategory();
            _failures.Remove(name);
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            ReportFailure(name, ex.Message);
            return null;
        }
    }

    private void ReportFailure(string source, string message)
    {
        if (!_failures.TryGetValue(source, out var previous) || previous != message)
            Trace.TraceWarning("{0} telemetry unavailable: {1}", source, message);
        _failures[source] = message;
    }

    private static double? Memory(InstanceDataCollectionCollection? data, string counter, AdapterKey adapter)
    {
        if (data?[counter] is not { } instances) return null;
        var values = instances.Values.Cast<InstanceData>()
            .Where(i => AcceleratorMetrics.ParseAdapter(i.InstanceName) == adapter)
            .Select(i => i.Sample.RawValue).ToArray();
        return values.Length > 0 && values.All(v => v >= 0) ? values.Sum(v => (double)v) : null;
    }
}
