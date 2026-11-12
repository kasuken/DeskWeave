using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using DeskWeave.Core.AiQuota;
using DeskWeave.Core.AgentActivity;
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
    private readonly StatusSettingsStore _statusSettings = new(Path.Combine(DataFolder, "service-status-settings.json"));
    private readonly AgentActivitySettingsStore _agentSettingsStore = new(Path.Combine(DataFolder, "agent-activity-settings.json"));
    private AgentActivityService _agentActivity = new(Path.Combine(DataFolder, "agent-activity"));
    private AgentActivitySettings _agentSettings;
    private AgentActivitySnapshot _activity = AgentActivitySnapshot.Empty;
    private CancellationTokenSource? _agentReadCancellation;
    private int _agentSettingsGeneration;
    private readonly CancellationTokenSource _stopping = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Quota requests carry bearer tokens, so redirects are never followed to another host.
    private readonly HttpClient _quotaHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    // Latest data, replayed into widget sets created later (e.g. when a monitor is connected).
    private SystemSample? _sample;
    private readonly Dictionary<string, StatusClient> _statusClients = [];
    private readonly Dictionary<string, ProviderStatusReading> _statusReadings = [];
    private readonly Dictionary<string, CancellationTokenSource> _statusCancellation = [];
    private readonly Dictionary<string, Task> _statusTasks = [];
    private readonly Dictionary<string, DateTimeOffset> _statusNotBefore = [];
    private readonly HashSet<string> _enabledStatus;
    private (
        IReadOnlyList<ProviderQuota> Quotas,
        IReadOnlyDictionary<QuotaLimitKey, QuotaForecast> Forecasts,
        bool Refreshing,
        DateTimeOffset? UpdatedAt)? _quotas;

    private MonitorMode _mode;
    private bool _started;
    private bool _layoutPending;
    private bool _visible;

    public WidgetHost()
    {
        _mode = _settings.Load();
        _agentSettings = _agentSettingsStore.Load();
        var statusSettings = _statusSettings.Load();
        _enabledStatus = new HashSet<string>(statusSettings.Enabled, StringComparer.Ordinal);
        StatusSettingsWarning = statusSettings.Warning;
        foreach (var provider in StatusProviders.All)
        {
            var file = provider.Id == StatusProviders.GitHub ? "github-status.json" : $"{provider.Id}-status.json";
            _statusClients.Add(provider.Id, new StatusClient(_http, Path.Combine(DataFolder, file),
                provider.Endpoints, provider.Id == StatusProviders.OpenAI
                    ? OpenAiStatusParser.Parse
                    : (bodies, fetchedAt) => StatusParser.Parse(bodies[0], fetchedAt)));
        }
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public bool IsVisible => _visible;
    public string? StatusSettingsWarning { get; private set; }
    public string? QuotaHistoryWarning { get; private set; }
    public string? AgentSettingsWarning => _agentSettings.Warning;
    public IReadOnlyDictionary<string, string> AgentWorkspaces => _agentSettings.Workspaces;
    public IReadOnlyList<AgentSourceView> AgentSources => _activity.Sources;
    public static string AgentSettingsPath => Path.Combine(DataFolder, "agent-activity-settings.json");

    public bool IsAgentEnabled(string id) => _agentSettings.EnabledIntegrations.Contains(id, StringComparer.Ordinal);

    public void SetAgentEnabled(string id, bool enabled)
    {
        if (id != "claude-code") throw new ArgumentException("Unknown agent integration.", nameof(id));
        var selected = _agentSettings.EnabledIntegrations.ToHashSet(StringComparer.Ordinal);
        if (enabled) selected.Add(id);
        else selected.Remove(id);
        _agentSettingsStore.Save(selected, _agentSettings.Workspaces);
        _agentSettings = new AgentActivitySettings(selected.ToArray(), _agentSettings.Workspaces, null);
        AgentSettingsChanged();
    }

    public void ReloadAgentSettings()
    {
        _agentSettings = _agentSettingsStore.Load();
        AgentSettingsChanged();
    }

    public static void PrepareAgentSettingsFile()
    {
        if (!File.Exists(AgentSettingsPath))
        {
            new AgentActivitySettingsStore(AgentSettingsPath).Save([], new Dictionary<string, string>());
        }
    }

    private void AgentSettingsChanged()
    {
        _agentSettingsGeneration++;
        _agentReadCancellation?.Cancel();
        _agentActivity = new AgentActivityService(Path.Combine(DataFolder, "agent-activity"));
        PublishActivity(AgentActivitySnapshot.Empty);
    }

    public bool IsStatusEnabled(string id) => _enabledStatus.Contains(id);

    public void SetStatusEnabled(string id, bool enabled)
    {
        var provider = StatusProviders.All.FirstOrDefault(p => p.Id == id)
            ?? throw new ArgumentException("Unknown service status provider.", nameof(id));
        if (_enabledStatus.Contains(id) == enabled)
        {
            return;
        }

        var selected = new HashSet<string>(_enabledStatus, StringComparer.Ordinal);
        if (enabled) selected.Add(id);
        else selected.Remove(id);
        _statusSettings.Save(selected);
        StatusSettingsWarning = null;
        if (enabled)
        {
            _enabledStatus.Add(id);
            if (_started) StartStatusProvider(provider);
        }
        else
        {
            _enabledStatus.Remove(id);
            if (_statusCancellation.Remove(id, out var cancellation))
            {
                cancellation.Cancel();
            }
            _statusReadings.Remove(id);
            PublishStatuses();
        }
    }

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
        _started = true;
        _ = RunSystemMonitorAsync(_stopping.Token);
        foreach (var provider in StatusProviders.All.Where(p => _enabledStatus.Contains(p.Id)))
        {
            StartStatusProvider(provider);
        }
        PublishStatuses();
        _ = RunAiQuotaAsync(_stopping.Token);
        _ = RunAgentActivityAsync(_stopping.Token);
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

        set.Update(CurrentStatuses());
        set.Update(_activity);

        if (_quotas is { } quotas)
        {
            set.Update(quotas.Quotas, quotas.Forecasts, quotas.Refreshing, quotas.UpdatedAt);
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

    private IReadOnlyList<ProviderStatusReading> CurrentStatuses() =>
        StatusProviders.All.Where(p => _enabledStatus.Contains(p.Id) && _statusReadings.ContainsKey(p.Id))
            .Select(p => _statusReadings[p.Id]).ToArray();

    private void PublishStatuses()
    {
        var readings = CurrentStatuses();
        foreach (var set in _sets)
        {
            set.Update(readings);
        }
        ScheduleLayout();
    }

    private void PublishActivity(AgentActivitySnapshot snapshot)
    {
        _activity = snapshot;
        foreach (var set in _sets)
        {
            set.Update(snapshot);
        }
        ScheduleLayout();
    }

    private async Task RunAgentActivityAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SampleInterval);
        try
        {
            do
            {
                var generation = _agentSettingsGeneration;
                var enabled = _agentSettings.EnabledIntegrations.ToArray();
                var service = _agentActivity;
                using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _agentReadCancellation = read;
                try
                {
                    var snapshot = await Task.Run(() => service.ReadAsync(enabled, read.Token), read.Token);
                    if (generation == _agentSettingsGeneration && !read.IsCancellationRequested)
                    {
                        PublishActivity(snapshot);
                    }
                }
                catch (OperationCanceledException) when (read.IsCancellationRequested)
                {
                }
                finally
                {
                    _agentReadCancellation = null;
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Publish(
        IReadOnlyList<ProviderQuota> quotas,
        IReadOnlyDictionary<QuotaLimitKey, QuotaForecast> forecasts,
        bool refreshing,
        DateTimeOffset? updatedAt)
    {
        _quotas = (quotas, forecasts, refreshing, updatedAt);
        foreach (var set in _sets)
        {
            set.Update(quotas, forecasts, refreshing, updatedAt);
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

    private void StartStatusProvider(StatusProvider provider)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        _statusCancellation[provider.Id] = cancellation;
        var previous = _statusTasks.GetValueOrDefault(provider.Id, Task.CompletedTask);
        _statusTasks[provider.Id] = RunStatusProviderAsync(provider, previous, cancellation);
    }

    private async Task RunStatusProviderAsync(StatusProvider provider, Task previous, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await previous;
            token.ThrowIfCancellationRequested();
            var client = _statusClients[provider.Id];
            PublishStatus(provider, client.LoadCached());
            var lastOnline = false;
            while (true)
            {
                if (_statusNotBefore.TryGetValue(provider.Id, out var notBefore))
                {
                    while (DateTimeOffset.UtcNow < notBefore)
                    {
                        var remaining = notBefore - DateTimeOffset.UtcNow;
                        if (remaining <= TimeSpan.Zero) break;
                        if (!lastOnline && _statusReadings.TryGetValue(provider.Id, out var cached)
                            && cached.Reading.Summary is { } summary)
                        {
                            var untilExpiry = summary.FetchedAt + StatusClient.CacheLifetime - DateTimeOffset.UtcNow;
                            if (untilExpiry <= TimeSpan.Zero)
                            {
                                PublishStatus(provider, client.Publish(online: false));
                                continue;
                            }
                            if (untilExpiry < remaining) remaining = untilExpiry;
                        }
                        await Task.Delay(remaining < TimeSpan.FromMinutes(1) ? remaining : TimeSpan.FromMinutes(1), token);
                        token.ThrowIfCancellationRequested();
                        if (!lastOnline)
                        {
                            PublishStatus(provider, client.Publish(online: false));
                        }
                    }
                }

                _statusNotBefore[provider.Id] = DateTimeOffset.UtcNow + StatusClient.MinimumInterval;
                var (reading, delay) = await client.PollAsync(token);
                var now = DateTimeOffset.UtcNow;
                _statusNotBefore[provider.Id] = delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
                token.ThrowIfCancellationRequested();
                lastOnline = reading.Online;
                PublishStatus(provider, reading);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void PublishStatus(StatusProvider provider, StatusReading reading)
    {
        _statusReadings[provider.Id] = new ProviderStatusReading(provider.Id, provider.Name, reading);
        PublishStatuses();
    }

    private async Task RunAiQuotaAsync(CancellationToken cancellationToken)
    {
        var service = new AiQuotaService(
            _quotaHttp,
            QuotaSources.Default(() => WindowsCredentialReader.ReadGeneric(WindowsCredentialReader.AntigravityTarget)),
            Path.Combine(DataFolder, "ai-quota.json"),
            historyFile: Path.Combine(DataFolder, "ai-quota-history.json"));
        var quotas = service.LoadCached();
        QuotaHistoryWarning = service.HistoryWarning;
        Publish(quotas, service.Forecasts, refreshing: true, updatedAt: null);
        ScheduleLayout();

        using var timer = new PeriodicTimer(AiQuotaService.RefreshInterval);
        do
        {
            Publish(quotas, service.Forecasts, refreshing: true, updatedAt: null);
            quotas = await Task.Run(() => service.RefreshAsync(cancellationToken), cancellationToken);
            QuotaHistoryWarning = service.HistoryWarning;
            Publish(quotas, service.Forecasts, refreshing: false, updatedAt: DateTimeOffset.Now);
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
