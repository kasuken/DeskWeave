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

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };
    private IReadOnlyList<ProviderQuota> _quotas = [];
    private bool _refreshing = true;
    private DateTimeOffset? _updatedAt;

    public AiQuotaWidget()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Render();
        Loaded += (_, _) => _tick.Start();
        Unloaded += (_, _) => _tick.Stop();
        Render();
    }

    public bool HasProviders => _quotas.Count > 0;

    public void Update(IReadOnlyList<ProviderQuota> quotas, bool refreshing, DateTimeOffset? updatedAt)
    {
        _quotas = quotas;
        _refreshing = refreshing;
        _updatedAt = updatedAt ?? _updatedAt;
        Render();
    }

    private void Render()
    {
        var now = DateTimeOffset.UtcNow;
        PlanCount.Text = _quotas.Count == 1 ? "1 plan" : $"{_quotas.Count} plans";
        Providers.ItemsSource = _quotas.Select(q => ToCard(q, now)).ToList();
        Footer.Text = _refreshing || _updatedAt is null
            ? "Refreshing subscription data…"
            : $"Updated {_updatedAt.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)} · every 5 min";
    }

    private ProviderCard ToCard(ProviderQuota quota, DateTimeOffset now)
    {
        var tint = Tint(quota.Id);
        var limits = quota.Limits.Select(limit =>
        {
            var hasMeter = QuotaText.HasMeter(limit, now);
            var critical = hasMeter && limit.UsedPercent >= CriticalPercent;
            var detail = QuotaText.Detail(limit, now);
            return new LimitRow(
                limit.Label,
                QuotaText.Value(limit, now),
                quota.Stale ? Brush("MutedBrush") : critical ? Brush("RedBrush") : Brush("ForegroundBrush"),
                limit.UsedPercent ?? 0,
                quota.Stale ? Brush("MutedBrush") : critical ? Brush("RedBrush") : tint,
                hasMeter ? Visibility.Visible : Visibility.Collapsed,
                detail,
                detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed);
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
            lastKnown,
            quota.Stale ? Visibility.Visible : Visibility.Collapsed);
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
        Visibility DetailVisibility);
}
