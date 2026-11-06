using System.Runtime.InteropServices;
using DeskWeave.Core;

namespace DeskWeave.Interop;

/// <summary>Optional WDDM sensor queries through Windows, without vendor libraries or kernel drivers.</summary>
internal static class AdapterSensors
{
    internal readonly record struct Reading(double? Temperature, double? PowerPercent, double? FanRpm, int Status);

    internal static unsafe Reading Read(AdapterKey key)
    {
        var open = new OpenAdapter { Low = key.Low, High = key.High };
        var status = D3DKMTOpenAdapterFromLuid(ref open);
        if (status < 0)
        {
            return new(null, null, null, status);
        }

        try
        {
            var data = new PerformanceData();
            var query = new Query { Adapter = open.Handle, Type = 62, Data = (nint)(&data), Size = (uint)sizeof(PerformanceData) };
            status = D3DKMTQueryAdapterInfo(ref query);
            if (status < 0)
            {
                return new(null, null, null, status);
            }

            var caps = new PerformanceCaps();
            query.Type = 63;
            query.Data = (nint)(&caps);
            query.Size = (uint)sizeof(PerformanceCaps);
            var hasCaps = D3DKMTQueryAdapterInfo(ref query) >= 0;
            // Drivers commonly return zero placeholders. Fan capability permits a real stopped-fan reading.
            var values = AcceleratorMetrics.Sensors(data.Temperature, data.Power, data.FanRpm,
                hasCaps ? caps.MaxFanRpm : null);
            return new(values.Temperature, values.PowerPercent, values.FanRpm, 0);
        }
        finally
        {
            var handle = open.Handle;
            D3DKMTCloseAdapter(ref handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapter { internal uint Low, High, Handle; }

    // D3DKMT_PTR reserves eight bytes even in a 32-bit process.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Query
    {
        [FieldOffset(0)] internal uint Adapter;
        [FieldOffset(4)] internal uint Type;
        [FieldOffset(8)] internal nint Data;
        [FieldOffset(16)] internal uint Size;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PerformanceData
    {
        internal uint PhysicalIndex;
        internal ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOc, MemoryBandwidth, PcieBandwidth;
        internal uint FanRpm, Power, Temperature;
        internal byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PerformanceCaps
    {
        internal uint PhysicalIndex;
        internal ulong MaxMemoryBandwidth, MaxPcieBandwidth;
        internal uint MaxFanRpm, TemperatureMax, TemperatureWarning;
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapter data);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int D3DKMTQueryAdapterInfo(ref Query data);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int D3DKMTCloseAdapter(ref uint handle);
}
