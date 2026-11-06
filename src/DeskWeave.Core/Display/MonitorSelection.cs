namespace DeskWeave.Core.Display;

/// <summary>Which screens show the widgets.</summary>
public enum MonitorMode
{
    Primary,
    Secondary,
    Both,
}

/// <summary>A rectangle in physical (device) pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height);

/// <summary>A connected monitor: bounds and work area in physical pixels, and its effective DPI scale (1.0 = 96 DPI).</summary>
public sealed record DisplayInfo(string DeviceName, bool IsPrimary, PixelRect Bounds, PixelRect WorkArea, double DpiScale)
{
    /// <summary>Work area width in device-independent pixels on this monitor.</summary>
    public double WorkWidth => WorkArea.Width / DpiScale;

    /// <summary>Work area height in device-independent pixels on this monitor.</summary>
    public double WorkHeight => WorkArea.Height / DpiScale;

    /// <summary>
    /// Content scale relative to a 1920x1080 logical desktop. Windows DPI scaling is applied on top by WPF,
    /// so this only compensates for how much logical space the screen offers.
    /// </summary>
    public double ContentScale => Math.Clamp(Math.Min(WorkWidth / 1920.0, (WorkHeight + 48) / 1080.0), 0.7, 1.8);

    /// <summary>Converts a point in DIPs relative to the work area's top-left corner to physical screen pixels.</summary>
    public (int X, int Y) ToPixels(double x, double y) =>
        (WorkArea.Left + (int)Math.Round(x * DpiScale), WorkArea.Top + (int)Math.Round(y * DpiScale));
}

public static class MonitorSelection
{
    /// <summary>
    /// The secondary screen. With more than two monitors it is the non-primary one that is leftmost,
    /// then topmost, then first by device name, so the choice is stable across restarts.
    /// </summary>
    public static DisplayInfo? Secondary(IReadOnlyList<DisplayInfo> displays) => displays
        .Where(d => !d.IsPrimary)
        .OrderBy(d => d.Bounds.Left)
        .ThenBy(d => d.Bounds.Top)
        .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    public static DisplayInfo? Primary(IReadOnlyList<DisplayInfo> displays) =>
        displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();

    /// <summary>Whether <paramref name="mode"/> can be honored as chosen with the connected monitors.</summary>
    public static bool IsAvailable(MonitorMode mode, IReadOnlyList<DisplayInfo> displays) =>
        mode != MonitorMode.Secondary || Secondary(displays) is not null;

    /// <summary>
    /// Screens that get a widget set, primary first. A missing secondary falls back to the primary screen,
    /// so widgets never disappear when a monitor is disconnected; the saved mode itself is unchanged.
    /// </summary>
    public static IReadOnlyList<DisplayInfo> Targets(MonitorMode mode, IReadOnlyList<DisplayInfo> displays)
    {
        var primary = Primary(displays);
        if (primary is null)
        {
            return [];
        }

        var secondary = Secondary(displays);
        return mode switch
        {
            MonitorMode.Secondary => [secondary ?? primary],
            MonitorMode.Both when secondary is not null => [primary, secondary],
            _ => [primary],
        };
    }
}
