using System.Runtime.InteropServices;
using System.Windows;
using DeskWeave.Core.Display;
using static DeskWeave.Interop.NativeMethods;

namespace DeskWeave.Desktop;

/// <summary>Enumerates connected monitors with their work areas (excluding the taskbar) and effective DPI.</summary>
internal static class ScreenMetrics
{
    /// <summary>All monitors, refreshed on every call. Never empty: falls back to the WPF primary work area.</summary>
    public static unsafe IReadOnlyList<DisplayInfo> All()
    {
        var displays = new List<DisplayInfo>();
        var handle = GCHandle.Alloc(displays);
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return displays.Count > 0 ? displays : [Fallback()];
    }

    [UnmanagedCallersOnly]
    private static unsafe int OnMonitor(IntPtr monitor, IntPtr hdc, RECT* clip, IntPtr data)
    {
        var displays = (List<DisplayInfo>)GCHandle.FromIntPtr(data).Target!;
        var info = new MONITORINFOEX { cbSize = (uint)sizeof(MONITORINFOEX) };
        if (GetMonitorInfo(monitor, ref info))
        {
            var dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
            displays.Add(new DisplayInfo(
                new string((char*)info.szDevice),
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                ToPixelRect(info.rcMonitor),
                ToPixelRect(info.rcWork),
                dpi));
        }

        return 1;
    }

    private static PixelRect ToPixelRect(RECT rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static DisplayInfo Fallback()
    {
        var work = SystemParameters.WorkArea;
        var rect = new PixelRect((int)work.Left, (int)work.Top, (int)work.Width, (int)work.Height);
        return new DisplayInfo("PRIMARY", true, rect, rect, 1);
    }
}
