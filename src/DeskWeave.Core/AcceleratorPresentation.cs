namespace DeskWeave.Core;

/// <summary>One available metric; unavailable rows are omitted rather than rendered as zero.</summary>
public sealed record AcceleratorRow(string Label, string Value);

/// <summary>Shared, testable visibility and formatting rules for accelerator cards.</summary>
public static class AcceleratorPresentation
{
    /// <summary>Builds only meaningful live rows. Dedicated and shared memory are never combined.</summary>
    public static IReadOnlyList<AcceleratorRow> Rows(AcceleratorSample sample)
    {
        var rows = new List<AcceleratorRow>();
        if (sample.Utilization is { } utilization)
            rows.Add(new("Load", Formatting.Percent(utilization)));
        foreach (var engine in sample.Engines.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            rows.Add(new(engine.Key, Formatting.Percent(engine.Value)));
        if (sample.DedicatedUsed is { } dedicated)
        {
            // Integrated GPUs can expose a system-memory reservation, not physical VRAM.
            rows.Add(new("Dedicated", Memory(dedicated, sample.DedicatedCapacity)));
        }
        if (sample.SharedUsed is { } shared)
            rows.Add(new("Shared", Memory(shared, sample.SharedCapacity)));
        if (sample.Temperature is { } temperature)
            rows.Add(new("Temperature", Formatting.Temperature(temperature)));
        if (sample.PowerPercent is { } power)
            rows.Add(new("Power (relative)", $"{power:0.#}%"));
        if (sample.FanRpm is { } fan)
            rows.Add(new("Fan", $"{fan:0} RPM"));
        return rows;
    }

    private static string Memory(double used, double? capacity) =>
        capacity is > 0 ? $"{Formatting.Bytes(used)} / {Formatting.Bytes(capacity)}" : Formatting.Bytes(used);
}
