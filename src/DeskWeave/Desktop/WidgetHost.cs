using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using DeskWeave.Core.AiQuota;
using DeskWeave.Core.Display;
using DeskWeave.Core.GitHubStatus;
using DeskWeave.Services;
using Microsoft.Win32;

namespace DeskWeave.Desktop;

/// <summary>
/// Owns the data loops and one <see cref="WidgetSet"/> per target screen (see <see cref="MonitorMode"/>).
/// Sampling and polling run once; every set receives the same values. Screens and layout are recomputed whenever
/// monitors, resolution, DPI, work area (taskbar) or widget content size change.
/// </summary>
public sealed class WidgetHost : IDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    private static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskWeave");

    private readonly List<WidgetSet> _sets = [];
    private readonly DisplaySettingsStore _settings = new(Path.Combine(DataFolder, "settings.json"));
    private readonly CancellationTokenSource _stopping = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Quota requests carry bearer tokens, so redirects are never followed to another host.
    private readonly HttpClient _quotaHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    // Latest data, replayed into widget sets created later (e.g. when a monitor is connected).
    private SystemSample? _sample;
    private StatusReading? _reading;
    private (IReadOnlyList<ProviderQuota> Quotas, bool Refreshing, DateTimeOffset? UpdatedAt)? _quotas;

    private MonitorMode _mode;
    private bool _layoutPending;
    private bool _visible;

    public WidgetHost()
    {
        _mode = _settings.Load();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public bool IsVisible => _visible;

    /// <summary>
    /// The saved screen preference. When it cannot be honored (secondary disconnected) widgets temporarily use the
    /// primary screen, but the preference is kept and applies again once the monitor returns.
    /// </summary>
    public MonitorMode Mode
    {
        get => _mode;
        set
        {
            if (!Enum.IsDefined(value) || value == _mode)
            {
                return;
            }

            _mode = value;
            _settings.Save(value);
            Layout();
        }
    }

    /// <summary>Whether a mode can be honored with the monitors connected right now.</summary>
    public static bool IsModeAvailable(MonitorMode mode) => MonitorSelection.IsAvailable(mode, ScreenMetrics.All());

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
        foreach (var set in _sets)
        {
            set.Hide();
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
        SyncSets(MonitorSelection.Targets(_mode, ScreenMetrics.All()));
        if (!_visible)
        {
            return;
        }

        foreach (var set in _sets)
        {
            set.Layout();
        }
    }

    /// <summary>Keeps one widget set per target screen, reusing sets for screens that are still targeted.</summary>
    private void SyncSets(IReadOnlyList<DisplayInfo> targets)
    {
        var remaining = new List<WidgetSet>(_sets);
        _sets.Clear();
        foreach (var display in targets)
        {
            var set = remaining.Find(s => string.Equals(s.Display.DeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (set is not null)
            {
                remaining.Remove(set);
                set.Display = display;
            }
            else
            {
                set = CreateSet(display);
            }

            _sets.Add(set);
        }

        foreach (var set in remaining)
        {
            set.Close();
        }
    }

    private WidgetSet CreateSet(DisplayInfo display)
    {
        var set = new WidgetSet(display, ScheduleLayout);
        if (_sample is { } sample)
        {
            set.Update(sample);
        }

        if (_reading is { } reading)
        {
            set.Update(reading);
        }

        if (_quotas is { } quotas)
        {
            set.Update(quotas.Quotas, quotas.Refreshing, quotas.UpdatedAt);
        }

        return set;
    }

    private void Publish(SystemSample sample)
    {
        _sample = sample;
        foreach (var set in _sets)
        {
            set.Update(sample);
        }
    }

    private void Publish(StatusReading reading)
    {
        _reading = reading;
        foreach (var set in _sets)
        {
            set.Update(reading);
        }
    }

    private void Publish(IReadOnlyList<ProviderQuota> quotas, bool refreshing, DateTimeOffset? updatedAt)
    {
        _quotas = (quotas, refreshing, updatedAt);
        foreach (var set in _sets)
        {
            set.Update(quotas, refreshing, updatedAt);
        }
    }

    private async Task RunSystemMonitorAsync(CancellationToken cancellationToken)
    {
        using var sampler = new SystemMetricsSampler();
        using var timer = new PeriodicTimer(SampleInterval);
        do
        {
            Publish(await Task.Run(sampler.Sample, cancellationToken));
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task RunGitHubStatusAsync(CancellationToken cancellationToken)
    {
        var client = new StatusClient(_http, Path.Combine(DataFolder, "github-status.json"));
        Publish(client.LoadCached());
        while (!cancellationToken.IsCancellationRequested)
        {
            var (reading, delay) = await client.PollAsync(cancellationToken);
            Publish(reading);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task RunAiQuotaAsync(CancellationToken cancellationToken)
    {
        var service = new AiQuotaService(
            _quotaHttp,
            QuotaSources.Default(() => WindowsCredentialReader.ReadGeneric(WindowsCredentialReader.AntigravityTarget)),
            Path.Combine(DataFolder, "ai-quota.json"));
        var quotas = service.LoadCached();
        Publish(quotas, refreshing: true, updatedAt: null);
        ScheduleLayout();

        using var timer = new PeriodicTimer(AiQuotaService.RefreshInterval);
        do
        {
            Publish(quotas, refreshing: true, updatedAt: null);
            quotas = await Task.Run(() => service.RefreshAsync(cancellationToken), cancellationToken);
            Publish(quotas, refreshing: false, updatedAt: DateTimeOffset.Now);
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
        foreach (var set in _sets)
        {
            set.Close();
        }

        _sets.Clear();
        _http.Dispose();
        _quotaHttp.Dispose();
        _stopping.Dispose();
    }
}
