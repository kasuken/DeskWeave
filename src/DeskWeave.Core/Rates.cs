namespace DeskWeave.Core;

public static class Rates
{
    /// <summary>Returns bytes per second, or null for first samples and counter resets.</summary>
    public static double? PerSecond(long? previous, long? current, TimeSpan elapsed)
    {
        if (previous is not { } before || current is not { } now || elapsed <= TimeSpan.Zero || now < before)
        {
            return null;
        }

        return Math.Round((now - before) / elapsed.TotalSeconds);
    }

    /// <summary>CPU usage from GetSystemTimes-style counters, where kernel time includes idle time.</summary>
    public static double? CpuUsage(CpuTimes? previous, CpuTimes? current)
    {
        if (previous is not { } before || current is not { } now)
        {
            return null;
        }

        var total = (now.Kernel - before.Kernel) + (now.User - before.User);
        var idle = now.Idle - before.Idle;
        if (total <= 0 || idle < 0)
        {
            return null;
        }

        return Math.Round(Math.Clamp(100.0 * (total - idle) / total, 0, 100), 1);
    }
}

public readonly record struct CpuTimes(long Idle, long Kernel, long User);
