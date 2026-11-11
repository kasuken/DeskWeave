using System.Windows.Interop;
using DeskWeave.Core.AiQuota;
using DeskWeave.Core.Display;
using DeskWeave.Core.GitHubStatus;
using DeskWeave.Services;
using DeskWeave.Widgets;
using static DeskWeave.Interop.NativeMethods;

namespace DeskWeave.Desktop;

/// <summary>
/// One complete set of widget windows laid out on a single monitor. Data is pushed in by <see cref="WidgetHost"/>,
/// so several sets share the same sampling and polling.
/// </summary>
internal sealed class WidgetSet
{
    private const double BaseMargin = 24;
    private const double BaseGap = 12;

    private readonly SystemMonitorWidget _monitor = new();
    private readonly TopProcessesWidget _processes = new();
    private readonly GitHubStatusWidget _github = new();
    private readonly WorldClockWidget _clock = new();
    private readonly AiQuotaWidget _quota = new();
    private readonly DesktopWidgetWindow _monitorWindow;
    private readonly DesktopWidgetWindow _processesWindow;
    private readonly DesktopWidgetWindow _githubWindow;
    private readonly DesktopWidgetWindow _clockWindow;
    private readonly DesktopWidgetWindow _quotaWindow;

    public WidgetSet(DisplayInfo display, Action layoutChanged)
    {
        Display = display;
        _monitorWindow = new DesktopWidgetWindow(_monitor, "System");
        _processesWindow = new DesktopWidgetWindow(_processes, "Top Processes");
        _githubWindow = new DesktopWidgetWindow(_github, "GitHub Status");
        _clockWindow = new DesktopWidgetWindow(_clock, "World Clock");
        _quotaWindow = new DesktopWidgetWindow(_quota, "AI Subscriptions");

        foreach (var window in Windows)
        {
            window.SizeChanged += (_, _) => layoutChanged();
            window.DpiChanged += (_, _) => layoutChanged();
        }
    }

    public DisplayInfo Display { get; set; }

    private IEnumerable<DesktopWidgetWindow> Windows =>
        [_monitorWindow, _processesWindow, _githubWindow, _clockWindow, _quotaWindow];

    public void Update(SystemSample sample)
    {
        _monitor.Update(sample);
        _processes.Update(sample.Processes);
    }

    public void Update(IReadOnlyList<ProviderStatusReading> readings) => _github.Update(readings);

    public void Update(IReadOnlyList<ProviderQuota> quotas, bool refreshing, DateTimeOffset? updatedAt) =>
        _quota.Update(quotas, refreshing, updatedAt);

    /// <summary>
    /// Lays the widgets out in DIPs of this monitor, relative to its work area, then converts to physical pixels.
    /// Positioning in pixels keeps placement exact on monitors whose DPI differs from the primary's.
    /// </summary>
    public void Layout()
    {
        var width = Display.WorkWidth;
        var height = Display.WorkHeight;
        var scale = Display.ContentScale;
        var margin = BaseMargin * scale;
        var gap = BaseGap * scale;

        foreach (var window in Windows)
        {
            window.Scale = scale;
        }

        // World clock: bottom center, wrapping to fit the screen width.
        _clock.SetAvailableWidth((width - 2 * margin) / scale);
        var clockSize = _clockWindow.MeasureScaled();
        var clockTop = height - margin - clockSize.Height;
        Place(_clockWindow, (width - clockSize.Width) / 2, clockTop);

        // System monitor: top right.
        var monitorSize = _monitorWindow.MeasureScaled();
        var availableHeight = clockTop - gap - margin;
        var processesSize = _processesWindow.MeasureScaled();
        var monitorHeightBudget = availableHeight - gap - processesSize.Height;
        var showProcessesBelowMonitor = monitorHeightBudget > 0;
        if (showProcessesBelowMonitor && monitorSize.Height > monitorHeightBudget)
        {
            var stackScale = scale * (availableHeight - gap) / (monitorSize.Height + processesSize.Height);
            _monitorWindow.Scale = stackScale;
            _processesWindow.Scale = stackScale;
            monitorSize = _monitorWindow.MeasureScaled();
            processesSize = _processesWindow.MeasureScaled();
        }
        else if (!showProcessesBelowMonitor && monitorSize.Height > availableHeight && availableHeight > 0)
        {
            // Read-only widgets cannot scroll. Fit all adapters above the clock instead of clipping.
            _monitorWindow.Scale = scale * availableHeight / monitorSize.Height;
            monitorSize = _monitorWindow.MeasureScaled();
        }
        var monitorLeft = width - margin - monitorSize.Width;
        var monitorBottom = margin + monitorSize.Height;
        Place(_monitorWindow, monitorLeft, margin);

        var processesBottom = monitorBottom;
        if (showProcessesBelowMonitor)
        {
            var processesTop = monitorBottom + gap;
            Place(_processesWindow, width - margin - processesSize.Width, processesTop);
            processesBottom = processesTop + processesSize.Height;
        }
        else
        {
            _processesWindow.Hide();
        }

        // AI subscriptions: top left, hidden when there are no paid plans or it does not fit above the clock.
        var limit = clockTop - gap;
        var leftBound = margin;
        var quotaSize = _quotaWindow.MeasureScaled();
        if (_quota.HasProviders && margin + quotaSize.Height <= limit && margin + quotaSize.Width + gap <= monitorLeft)
        {
            Place(_quotaWindow, margin, margin);
            leftBound += quotaSize.Width + gap;
        }
        else
        {
            _quotaWindow.Hide();
        }

        // GitHub status: below both right-side panels when tall enough, beside them on short wide screens.
        var githubSize = _githubWindow.MeasureScaled();
        if (processesBottom + gap + githubSize.Height <= limit)
        {
            Place(_githubWindow, width - margin - githubSize.Width, processesBottom + gap);
        }
        else if (margin + githubSize.Height <= limit && monitorLeft - gap - githubSize.Width >= leftBound)
        {
            Place(_githubWindow, monitorLeft - gap - githubSize.Width, margin);
        }
        else
        {
            _githubWindow.Hide();
        }
    }

    public void Hide()
    {
        foreach (var window in Windows)
        {
            window.Hide();
        }
    }

    public void Close()
    {
        foreach (var window in Windows)
        {
            window.Close();
        }
    }

    private void Place(DesktopWidgetWindow window, double left, double top)
    {
        var (x, y) = Display.ToPixels(left, top);
        var handle = new WindowInteropHelper(window).EnsureHandle();
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        if (!window.IsVisible)
        {
            window.Show();

            // Showing can resize the window for its first DPI; pin the position again.
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }
}
