using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using DeskWeave.Core.AiQuota;
using DeskWeave.Core.GitHubStatus;
using DeskWeave.Services;
using DeskWeave.Widgets;
using Microsoft.Win32;

namespace DeskWeave.Desktop;

/// <summary>
/// Owns the widget windows, their data loops, and the responsive layout on the primary monitor.
/// Layout is recomputed whenever the resolution, DPI, work area (taskbar) or widget content size changes.
/// </summary>
public sealed class WidgetHost : IDisposable
{
    private const double BaseMargin = 24;
    private const double BaseGap = 12;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    private readonly SystemMonitorWidget _monitor = new();
    private readonly GitHubStatusWidget _github = new();
    private readonly WorldClockWidget _clock = new();
    private readonly AiQuotaWidget _quota = new();
    private readonly DesktopWidgetWindow _monitorWindow;
    private readonly DesktopWidgetWindow _githubWindow;
    private readonly DesktopWidgetWindow _clockWindow;
    private readonly DesktopWidgetWindow _quotaWindow;
    private readonly CancellationTokenSource _stopping = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Quota requests carry bearer tokens, so redirects are never followed to another host.
    private readonly HttpClient _quotaHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private bool _layoutPending;
    private bool _visible;

    public WidgetHost()
    {
        _monitorWindow = new DesktopWidgetWindow(_monitor, "System");
        _githubWindow = new DesktopWidgetWindow(_github, "GitHub Status");
        _clockWindow = new DesktopWidgetWindow(_clock, "World Clock");
        _quotaWindow = new DesktopWidgetWindow(_quota, "AI Subscriptions");

        foreach (var window in Windows)
        {
            window.SizeChanged += (_, _) => ScheduleLayout();
            window.DpiChanged += (_, _) => ScheduleLayout();
        }

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public bool IsVisible => _visible;

    private IEnumerable<DesktopWidgetWindow> Windows => [_monitorWindow, _githubWindow, _clockWindow, _quotaWindow];

    public void Start()
    {
        Show();
        _ = RunSystemMonitorAsync(_stopping.Token);
        _ = RunGitHubStatusAsync(_stopping.Token);
        _ = RunAiQuotaAsync(_stopping.Token);
    }

    public void Show()
    {
        _visible = true;
        Layout();
    }

    public void Hide()
    {
        _visible = false;
        foreach (var window in Windows)
        {
            window.Hide();
        }
    }

    public void ScheduleLayout()
    {
        if (_layoutPending)
        {
            return;
        }

        _layoutPending = true;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _layoutPending = false;
            Layout();
        });
    }

    private void Layout()
    {
        if (!_visible)
        {
            return;
        }

        var screen = ScreenMetrics.Primary();
        var work = screen.WorkArea;
        var scale = screen.ContentScale;
        var margin = BaseMargin * scale;
        var gap = BaseGap * scale;

        foreach (var window in Windows)
        {
            window.Scale = scale;
        }

        // World clock: bottom center, wrapping to fit the screen width.
        _clock.SetAvailableWidth((work.Width - 2 * margin) / scale);
        var clockSize = _clockWindow.MeasureScaled();
        var clockTop = work.Bottom - margin - clockSize.Height;
        Place(_clockWindow, work.Left + (work.Width - clockSize.Width) / 2, clockTop);

        // System monitor: top right.
        var monitorSize = _monitorWindow.MeasureScaled();
        var availableHeight = clockTop - gap - work.Top - margin;
        if (monitorSize.Height > availableHeight && availableHeight > 0)
        {
            // Read-only widgets cannot scroll. Fit all adapters above the clock instead of clipping.
            _monitorWindow.Scale = scale * availableHeight / monitorSize.Height;
            monitorSize = _monitorWindow.MeasureScaled();
        }
        var monitorLeft = work.Right - margin - monitorSize.Width;
        var monitorBottom = work.Top + margin + monitorSize.Height;
        Place(_monitorWindow, monitorLeft, work.Top + margin);

        // AI subscriptions: top left, hidden when there are no paid plans or it does not fit above the clock.
        var limit = clockTop - gap;
        var leftBound = work.Left + margin;
        var quotaSize = _quotaWindow.MeasureScaled();
        if (_quota.HasProviders
            && work.Top + margin + quotaSize.Height <= limit
            && work.Left + margin + quotaSize.Width + gap <= monitorLeft)
        {
            Place(_quotaWindow, work.Left + margin, work.Top + margin);
            leftBound += quotaSize.Width + gap;
        }
        else
        {
            _quotaWindow.Hide();
        }

        // GitHub status: below the monitor when tall enough, beside it on short wide screens, otherwise hidden.
        var githubSize = _githubWindow.MeasureScaled();
        if (monitorBottom + gap + githubSize.Height <= limit)
        {
            Place(_githubWindow, work.Right - margin - githubSize.Width, monitorBottom + gap);
        }
        else if (work.Top + margin + githubSize.Height <= limit && monitorLeft - gap - githubSize.Width >= leftBound)
        {
            Place(_githubWindow, monitorLeft - gap - githubSize.Width, work.Top + margin);
        }
        else
        {
            _githubWindow.Hide();
        }
    }

    private static void Place(DesktopWidgetWindow window, double left, double top)
    {
        window.Left = Math.Round(left);
        window.Top = Math.Round(top);
        if (!window.IsVisible)
        {
            window.Show();
        }
    }

    private async Task RunSystemMonitorAsync(CancellationToken cancellationToken)
    {
        using var sampler = new SystemMetricsSampler();
        using var timer = new PeriodicTimer(SampleInterval);
        do
        {
            var sample = await Task.Run(sampler.Sample, cancellationToken);
            _monitor.Update(sample);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task RunGitHubStatusAsync(CancellationToken cancellationToken)
    {
        var cacheFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskWeave", "github-status.json");
        var client = new StatusClient(_http, cacheFile);
        _github.Update(client.LoadCached());
        while (!cancellationToken.IsCancellationRequested)
        {
            var (reading, delay) = await client.PollAsync(cancellationToken);
            _github.Update(reading);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task RunAiQuotaAsync(CancellationToken cancellationToken)
    {
        var cacheFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskWeave", "ai-quota.json");
        var service = new AiQuotaService(
            _quotaHttp,
            QuotaSources.Default(() => WindowsCredentialReader.ReadGeneric(WindowsCredentialReader.AntigravityTarget)),
            cacheFile);
        var quotas = service.LoadCached();
        _quota.Update(quotas, refreshing: true, updatedAt: null);
        ScheduleLayout();

        using var timer = new PeriodicTimer(AiQuotaService.RefreshInterval);
        do
        {
            _quota.Update(quotas, refreshing: true, updatedAt: null);
            quotas = await Task.Run(() => service.RefreshAsync(cancellationToken), cancellationToken);
            _quota.Update(quotas, refreshing: false, updatedAt: DateTimeOffset.Now);
            ScheduleLayout();
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => ScheduleLayoutFromAnyThread();

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Taskbar moves, resizes and auto-hide changes alter the work area.
        if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General)
        {
            ScheduleLayoutFromAnyThread();
        }
    }

    private void ScheduleLayoutFromAnyThread() =>
        Application.Current?.Dispatcher.BeginInvoke(ScheduleLayout);

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _stopping.Cancel();
        foreach (var window in Windows)
        {
            window.Close();
        }

        _http.Dispose();
        _quotaHttp.Dispose();
        _stopping.Dispose();
    }
}
