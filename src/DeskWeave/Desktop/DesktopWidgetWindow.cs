using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static DeskWeave.Interop.NativeMethods;

namespace DeskWeave.Desktop;

/// <summary>
/// Borderless, transparent, click-through window that stays at the bottom of the z-order:
/// above the wallpaper, behind every application window, never focused and hidden from Alt+Tab.
/// </summary>
public sealed class DesktopWidgetWindow : Window
{
    private readonly ScaleTransform _scale = new(1, 1);

    public DesktopWidgetWindow(FrameworkElement content, string title)
    {
        Title = $"DeskWeave · {title}";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        Widget = content;
        Content = new System.Windows.Controls.Decorator { Child = content, LayoutTransform = _scale };
    }

    public FrameworkElement Widget { get; }

    public double Scale
    {
        get => _scale.ScaleX;
        set
        {
            _scale.ScaleX = value;
            _scale.ScaleY = value;
        }
    }

    /// <summary>Size of the scaled widget in device-independent pixels, valid even while hidden.</summary>
    public Size MeasureScaled()
    {
        var root = (UIElement)Content;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return root.DesiredSize;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = (long)GetWindowLongPtr(handle, GWL_EXSTYLE);
        style = (style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(handle, GWL_EXSTYLE, (nint)style);
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        SetWindowPos(handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING:
                // Pin every z-order change to the bottom so the widget never covers applications.
                var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                position.hwndInsertAfter = HWND_BOTTOM;
                position.flags &= ~SWP_NOZORDER;
                Marshal.StructureToPtr(position, lParam, false);
                break;
            case WM_MOUSEACTIVATE:
                handled = true;
                return MA_NOACTIVATE;
        }

        return IntPtr.Zero;
    }
}
