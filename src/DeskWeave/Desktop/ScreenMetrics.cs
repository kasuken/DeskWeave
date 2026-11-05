using System.Windows;
using static DeskWeave.Interop.NativeMethods;

namespace DeskWeave.Desktop;

/// <summary>Primary monitor work area (excluding the taskbar) in DIPs, refreshed on every call.</summary>
public readonly record struct ScreenMetrics(Rect WorkArea, double DpiScale)
{
    public static ScreenMetrics Primary()
    {
        var monitor = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return new ScreenMetrics(SystemParameters.WorkArea, 1);
        }

        var dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        var work = info.rcWork;
        return new ScreenMetrics(
            new Rect(work.Left / dpi, work.Top / dpi, (work.Right - work.Left) / dpi, (work.Bottom - work.Top) / dpi),
            dpi);
    }

    /// <summary>
    /// Content scale relative to a 1920x1080 logical desktop. Windows DPI scaling is applied on top by WPF,
    /// so this only compensates for how much logical space the screen offers.
    /// </summary>
    public double ContentScale => Math.Clamp(Math.Min(WorkArea.Width / 1920.0, (WorkArea.Height + 48) / 1080.0), 0.7, 1.8);
}
