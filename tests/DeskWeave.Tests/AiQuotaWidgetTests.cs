using System.Windows;
using System.Windows.Controls;
using DeskWeave.Core.AiQuota;
using DeskWeave.Widgets;

namespace DeskWeave.Tests;

[Collection("WPF")]
public class AiQuotaWidgetTests(WpfFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Widget_renders_a_qualified_estimate_without_changing_the_meter_or_reset()
    {
        fixture.Run(() =>
        {
            var widget = new AiQuotaWidget();
            var quota = Quota(stale: false, limits: [Limit("5h", 40)]);
            var forecasts = Forecast(QuotaForecastKind.Estimate, "Estimate: 2h left; resets in 5h");

            widget.Update([quota], forecasts, refreshing: false, updatedAt: Now);

            var card = ReadCard(widget);
            var row = Assert.Single(card.Limits);
            Assert.Equal("40% used", row.Value);
            Assert.Equal("Estimate: 2h left; resets in 5h", row.Forecast);
            Assert.StartsWith("Resets in ", row.Detail, StringComparison.Ordinal);
            Assert.Equal(40, row.Used);
        });
    }

    [Fact]
    public void Widget_shows_collecting_history_instead_of_a_fabricated_forecast()
    {
        fixture.Run(() =>
        {
            var widget = new AiQuotaWidget();
            widget.Update(
                [Quota(stale: false, limits: [Limit("5h", 40)])],
                Forecast(QuotaForecastKind.Collecting, "Collecting history"),
                refreshing: false,
                updatedAt: Now);

            Assert.Equal("Collecting history", Assert.Single(ReadCard(widget).Limits).Forecast);
        });
    }

    [Fact]
    public void Widget_marks_stale_data_and_limits_visible_rows_with_an_overflow_summary()
    {
        fixture.Run(() =>
        {
            var widget = new AiQuotaWidget();
            var limits = Enumerable.Range(0, 8)
                .Select(index => Limit($"Limit {index}", index * 5, $"limit-{index}"))
                .ToArray();

            widget.Update([Quota(stale: true, limits)], Forecast(QuotaForecastKind.Estimate, "Estimate: 1h left"),
                refreshing: false, updatedAt: Now);

            var card = ReadCard(widget);
            Assert.Equal(3, card.Limits.Count);
            Assert.All(card.Limits, row => Assert.Equal("Estimate unavailable · stale data", row.Forecast));
            Assert.Equal("5 more limits", card.OverflowSummary);
            Assert.Equal(Visibility.Visible, card.OverflowVisibility);
        });
    }

    [Fact]
    public void Widget_suppresses_cached_forecast_when_reset_passes_between_polls()
    {
        fixture.Run(() =>
        {
            var clock = new ManualTimeProvider(Now);
            var widget = new AiQuotaWidget(clock);
            var resetAt = Now.AddMinutes(2);
            var quota = Quota(stale: false, limits: [Limit("5h", 40, resetAt: resetAt)]);

            widget.Update([quota], Forecast(QuotaForecastKind.Estimate, "Estimate: 2h left", 10),
                refreshing: false, updatedAt: Now);
            clock.Set(resetAt.AddMinutes(1));
            widget.RefreshDisplay();

            Assert.Equal(
                "Waiting for refreshed quota",
                Assert.Single(ReadCard(widget).Limits).Forecast);
        });
    }

    [Fact]
    public void Widget_suppresses_cached_forecast_when_observation_becomes_stale_between_polls()
    {
        fixture.Run(() =>
        {
            var clock = new ManualTimeProvider(Now);
            var widget = new AiQuotaWidget(clock);
            var quota = Quota(stale: false, limits: [Limit("5h", 40)]);

            widget.Update([quota], Forecast(QuotaForecastKind.Estimate, "Estimate: 2h left", 10),
                refreshing: false, updatedAt: Now);
            clock.Set(Now.Add(QuotaForecastEngine.MaximumSampleAge).AddSeconds(1));
            widget.RefreshDisplay();

            Assert.Equal(
                "Estimate unavailable · stale data",
                Assert.Single(ReadCard(widget).Limits).Forecast);
        });
    }

    private static AiQuotaWidget.ProviderCard ReadCard(AiQuotaWidget widget)
    {
        var providers = (ItemsControl)widget.FindName("Providers");
        return Assert.IsType<AiQuotaWidget.ProviderCard>(Assert.Single(providers.Items));
    }

    private static IReadOnlyDictionary<QuotaLimitKey, QuotaForecast> Forecast(
        QuotaForecastKind kind,
        string message,
        double? rate = null) =>
        new Dictionary<QuotaLimitKey, QuotaForecast>
        {
            [new QuotaLimitKey(QuotaProviders.Claude, "Pro", "five_hour")] =
                new(kind, message, rate ?? (kind is QuotaForecastKind.Estimate or QuotaForecastKind.LastsUntilReset ? 29.5 : null)),
        };

    private static ProviderQuota Quota(bool stale, IReadOnlyList<QuotaLimit> limits) =>
        new(QuotaProviders.Claude, "Claude", "Pro", limits, null, Now, stale);

    private static QuotaLimit Limit(
        string label,
        double used,
        string identity = "five_hour",
        DateTimeOffset? resetAt = null) =>
        new(label, used, resetAt ?? Now.AddHours(5), Identity: identity);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Set(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
