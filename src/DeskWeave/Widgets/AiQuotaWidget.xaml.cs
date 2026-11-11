using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeskWeave.Core.AiQuota;

namespace DeskWeave.Widgets;

public partial class AiQuotaWidget : UserControl
{
    private const double CriticalPercent = 90;
    private const int MaxVisibleLimitsPerProvider = 3;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly TimeProvider _time;
    private IReadOnlyList<ProviderQuota> _quotas = [];
    private IReadOnlyDictionary<QuotaLimitKey, QuotaForecast> _forecasts =
        new Dictionary<QuotaLimitKey, QuotaForecast>();
    private bool _refreshing = true;
    private DateTimeOffset? _updatedAt;

    public AiQuotaWidget() : this(TimeProvider.System)
    {
    }

    internal AiQuotaWidget(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        InitializeComponent();
        _tick.Tick += (_, _) => RefreshDisplay();
        Loaded += (_, _) => _tick.Start();
        Unloaded += (_, _) => _tick.Stop();
        RefreshDisplay();
    }

    public bool HasProviders => _quotas.Count > 0;

    public void Update(
        IReadOnlyList<ProviderQuota> quotas,
        IReadOnlyDictionary<QuotaLimitKey, QuotaForecast>? forecasts,
        bool refreshing,
        DateTimeOffset? updatedAt)
    {
        _quotas = quotas;
        _forecasts = forecasts ?? new Dictionary<QuotaLimitKey, QuotaForecast>();
        _refreshing = refreshing;
        _updatedAt = updatedAt ?? _updatedAt;
        RefreshDisplay();
    }

    public void Update(IReadOnlyList<ProviderQuota> quotas, bool refreshing, DateTimeOffset? updatedAt) =>
        Update(quotas, null, refreshing, updatedAt);

    internal void RefreshDisplay()
    {
        var now = _time.GetUtcNow();
        PlanCount.Text = _quotas.Count == 1 ? "1 plan" : $"{_quotas.Count} plans";
        Providers.ItemsSource = _quotas.Select(q => ToCard(q, now)).ToList();
        Footer.Text = _refreshing || _updatedAt is null
            ? "Refreshing subscription data…"
            : $"Updated {_updatedAt.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)} · every 5 min";
    }

    private ProviderCard ToCard(ProviderQuota quota, DateTimeOffset now)
    {
        var tint = Tint(quota.Id);
        var limits = quota.Limits.Take(MaxVisibleLimitsPerProvider).Select(limit =>
        {
            var hasMeter = QuotaText.HasMeter(limit, now);
            var critical = hasMeter && limit.UsedPercent >= CriticalPercent;
            var detail = QuotaText.Detail(limit, now);
            var forecast = Forecast(quota, limit, now);
            return new LimitRow(
                limit.Label,
                QuotaText.Value(limit, now),
                quota.Stale ? Brush("MutedBrush") : critical ? Brush("RedBrush") : Brush("ForegroundBrush"),
                limit.UsedPercent ?? 0,
                quota.Stale ? Brush("MutedBrush") : critical ? Brush("RedBrush") : tint,
                hasMeter ? Visibility.Visible : Visibility.Collapsed,
                detail,
                detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
                forecast,
                Visibility.Visible);
        }).ToList();

        var lastKnown = quota.Stale
            ? $"Last known · {quota.FetchedAt.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture)}"
            : string.Empty;

        return new ProviderCard(
            quota.Name,
            quota.Plan,
            tint,
            quota.Status ?? string.Empty,
            string.IsNullOrEmpty(quota.Status) ? Visibility.Collapsed : Visibility.Visible,
            limits,
            quota.Limits.Count > limits.Count ? $"{quota.Limits.Count - limits.Count} more limits" : string.Empty,
            quota.Limits.Count > limits.Count ? Visibility.Visible : Visibility.Collapsed,
            lastKnown,
            quota.Stale ? Visibility.Visible : Visibility.Collapsed);
    }

    private string Forecast(ProviderQuota quota, QuotaLimit limit, DateTimeOffset now)
    {
        if (limit.Identity is not { } identity)
        {
            return "Estimate unavailable";
        }

        var key = new QuotaLimitKey(quota.Id, quota.Plan, identity);
        var forecast = _forecasts.TryGetValue(key, out var existing)
            ? existing
            : new QuotaForecast(QuotaForecastKind.Collecting, "Collecting history");
        return QuotaForecastEngine.AtTime(quota, limit, forecast, now).Message;
    }

    private Brush Tint(string id) => id switch
    {
        QuotaProviders.Claude => Brush("OrangeBrush"),
        QuotaProviders.Copilot => Brush("GreenBrush"),
        QuotaProviders.Antigravity => Brush("BlueBrush"),
        _ => Brush("AccentBrush"),
    };

    private Brush Brush(string key) => (Brush)FindResource(key);

    public sealed record ProviderCard(
        string Name,
        string Plan,
        Brush Tint,
        string Status,
        Visibility StatusVisibility,
        IReadOnlyList<LimitRow> Limits,
        string OverflowSummary,
        Visibility OverflowVisibility,
        string LastKnown,
        Visibility LastKnownVisibility);

    public sealed record LimitRow(
        string Label,
        string Value,
        Brush ValueBrush,
        double Used,
        Brush MeterBrush,
        Visibility MeterVisibility,
        string Detail,
        Visibility DetailVisibility,
        string Forecast,
        Visibility ForecastVisibility);
}
