using System.ComponentModel;
using System.Diagnostics;

namespace DeskWeave.Services;

public sealed record ProcessUsage(int ProcessId, string Name, double? CpuPercent, long? WorkingSet, double? GpuPercent);

/// <summary>Samples per-process CPU time and working set without requiring administrator access.</summary>
public sealed class ProcessMetricsSampler
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<int, (TimeSpan CpuTime, string Name)> _previous = [];
    private readonly Dictionary<(int ProcessId, string Metric), string> _failures = [];
    private TimeSpan? _previousSampleAt;

    public IReadOnlyList<ProcessUsage> Sample(
        IReadOnlyDictionary<int, double?> gpuUtilization,
        bool gpuUtilizationAvailable)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning("Process telemetry unavailable: {0}", ex.Message);
            return [];
        }

        var now = _clock.Elapsed;
        var elapsed = _previousSampleAt is { } previousSampleAt ? now - previousSampleAt : TimeSpan.Zero;
        var current = new Dictionary<int, (TimeSpan CpuTime, string Name)>();
        var observed = new HashSet<int>();
        var usage = new List<ProcessUsage>(processes.Length);
        foreach (var process in processes)
        {
            using (process)
            {
                var processId = 0;
                try
                {
                    processId = process.Id;
                    var name = process.ProcessName;
                    observed.Add(processId);

                    TimeSpan? cpuTime = null;
                    try
                    {
                        cpuTime = process.TotalProcessorTime;
                        _failures.Remove((processId, "CPU"));
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                    {
                        ReportFailure(processId, "CPU", ex.Message);
                    }

                    long? workingSet = null;
                    try
                    {
                        workingSet = process.WorkingSet64;
                        _failures.Remove((processId, "memory"));
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                    {
                        ReportFailure(processId, "memory", ex.Message);
                    }

                    double? cpuPercent = null;
                    if (cpuTime is { } currentCpu && elapsed > TimeSpan.Zero
                        && _previous.TryGetValue(processId, out var previous)
                        && string.Equals(name, previous.Name, StringComparison.Ordinal)
                        && currentCpu >= previous.CpuTime)
                    {
                        cpuPercent = Math.Round(Math.Clamp(
                            100 * (currentCpu - previous.CpuTime).TotalMilliseconds
                            / (elapsed.TotalMilliseconds * Environment.ProcessorCount), 0, 100), 1);
                    }

                    if (cpuTime is { } measuredCpu)
                    {
                        current[processId] = (measuredCpu, name);
                    }

                    var gpuPercent = gpuUtilizationAvailable
                        ? gpuUtilization.TryGetValue(processId, out var percent) ? percent : 0
                        : null;
                    usage.Add(new ProcessUsage(processId, name, cpuPercent, workingSet, gpuPercent));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                {
                    ReportFailure(processId, "identity", ex.Message);
                }
            }
        }

        _previous.Clear();
        foreach (var pair in current)
        {
            _previous.Add(pair.Key, pair.Value);
        }

        foreach (var failure in _failures.Keys.Where(f => !observed.Contains(f.ProcessId)).ToArray())
        {
            _failures.Remove(failure);
        }

        _previousSampleAt = now;
        return usage;
    }

    private void ReportFailure(int processId, string metric, string message)
    {
        var key = (processId, metric);
        if (!_failures.TryGetValue(key, out var previous) || previous != message)
        {
            Trace.TraceWarning("Process {0} {1} telemetry unavailable: {2}", processId, metric, message);
        }

        _failures[key] = message;
    }
}
