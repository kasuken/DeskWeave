using DeskWeave.Core.AiQuota;

namespace DeskWeave.Core.Tests;

public class QuotaForecastEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DefaultReset = Start.AddHours(5);

    private readonly ManualTimeProvider _clock = new(Start);
    private readonly QuotaForecastEngine _engine;

    public QuotaForecastEngineTests() => _engine = new QuotaForecastEngine(_clock);

    [Fact]
    public void Forecast_requires_three_samples_spanning_at_least_fifteen_minutes()
    {
        Add(20, Start);
        Add(30, Start.AddMinutes(7.5));

        Assert.Equal(QuotaForecastKind.Collecting, Forecast(30, Start.AddMinutes(7.5)).Kind);

        Add(40, Start.AddMinutes(15));

        var estimate = Forecast(40, Start.AddMinutes(15));
        Assert.Equal(QuotaForecastKind.Estimate, estimate.Kind);
        Assert.Equal(80, estimate.PercentagePointsPerHour!.Value, 8);
        Assert.Equal(TimeSpan.FromMinutes(45), estimate.TimeToExhaustion);
        Assert.Equal("Estimate: 45m left; resets in 4h 45m", estimate.Message);
    }

    [Fact]
    public void Forecast_does_not_use_three_samples_below_the_minimum_span()
    {
        Add(20, Start);
        Add(30, Start.AddMinutes(7));
        Add(40, Start.AddMinutes(14));

        Assert.Equal(QuotaForecastKind.InsufficientHistory, Forecast(40, Start.AddMinutes(14)).Kind);
    }

    [Fact]
    public void Forecast_compares_exhaustion_time_with_the_known_reset()
    {
        var reset = Start.AddHours(1);
        Add(10, Start, reset);
        Add(15, Start.AddMinutes(7.5), reset);
        Add(20, Start.AddMinutes(15), reset);

        var estimate = Forecast(20, Start.AddMinutes(15), reset);

        Assert.Equal(QuotaForecastKind.LastsUntilReset, estimate.Kind);
        Assert.Equal("Estimate: lasts until reset", estimate.Message);
    }

    [Fact]
    public void Forecast_can_be_recalculated_between_polls_without_adding_an_observation()
    {
        Add(10, Start);
        Add(20, Start.AddMinutes(7.5));
        Add(30, Start.AddMinutes(15));

        var quota = Quota(30, Start.AddMinutes(15));
        var original = _engine.Forecast(quota, quota.Limits[0], Start.AddMinutes(15));
        var updated = QuotaForecastEngine.AtTime(quota, quota.Limits[0], original, Start.AddMinutes(20));

        Assert.Equal(QuotaForecastKind.Estimate, updated.Kind);
        Assert.Equal(80, updated.PercentagePointsPerHour!.Value, 8);
        Assert.Equal(TimeSpan.FromMinutes(47.5), updated.TimeToExhaustion);
        Assert.Equal("Estimate: 45m left; resets in 4h 40m", updated.Message);
        Assert.Equal(3, _engine.ObservationCount);
    }

    [Fact]
    public void Forecast_recalculation_suppresses_estimate_after_reset_without_new_observation()
    {
        var reset = Start.AddMinutes(18);
        Add(10, Start, reset);
        Add(20, Start.AddMinutes(7.5), reset);
        Add(30, Start.AddMinutes(15), reset);

        var quota = Quota(30, Start.AddMinutes(15), reset);
        var original = _engine.Forecast(quota, quota.Limits[0], Start.AddMinutes(15));
        var updated = QuotaForecastEngine.AtTime(quota, quota.Limits[0], original, reset.AddMinutes(1));

        Assert.Equal(QuotaForecastKind.ResetDue, updated.Kind);
        Assert.Equal("Waiting for refreshed quota", updated.Message);
        Assert.Equal(3, _engine.ObservationCount);
    }

    [Fact]
    public void Forecast_classifies_exhaustion_at_the_reset_as_lasting_until_reset()
    {
        var reset = Start.AddHours(1);
        Add(20, Start, reset);
        Add(30, Start.AddMinutes(7.5), reset);
        Add(40, Start.AddMinutes(15), reset);

        var forecast = Forecast(40, Start.AddMinutes(15), reset);

        Assert.Equal(QuotaForecastKind.LastsUntilReset, forecast.Kind);
    }

    [Fact]
    public void Samples_from_different_providers_are_never_combined()
    {
        Add(10, Start);
        Add(20, Start.AddMinutes(7.5));

        var codex = new ProviderQuota(
            QuotaProviders.Codex,
            "Codex",
            "Plus",
            [new QuotaLimit("5h", 30, DefaultReset, Identity: "primary_window:18000")],
            null,
            Start.AddMinutes(15));
        _clock.Set(Start.AddMinutes(15));
        _engine.Observe(codex, _clock.GetUtcNow());

        Assert.Equal(QuotaForecastKind.Collecting,
            _engine.Forecast(codex, codex.Limits[0], _clock.GetUtcNow()).Kind);
    }

    [Fact]
    public void Forecast_reports_no_recent_consumption_instead_of_unlimited()
    {
        Add(0, Start);
        Add(0, Start.AddMinutes(7.5));
        Add(0, Start.AddMinutes(15));

        var forecast = Forecast(0, Start.AddMinutes(15));

        Assert.Equal(QuotaForecastKind.NoRecentConsumption, forecast.Kind);
        Assert.Equal("No recent consumption", forecast.Message);
        Assert.Null(forecast.PercentagePointsPerHour);
    }

    [Fact]
    public void Forecast_rounds_short_runway_down_without_second_level_precision()
    {
        Add(0, Start);
        Add(49.5, Start.AddMinutes(7.5));
        Add(99, Start.AddMinutes(15));

        var forecast = Forecast(99, Start.AddMinutes(15));

        Assert.Equal(QuotaForecastKind.Estimate, forecast.Kind);
        Assert.True(forecast.TimeToExhaustion < TimeSpan.FromMinutes(5));
        Assert.StartsWith("Estimate: under 5m left;", forecast.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decreasing_usage_starts_a_new_series_without_a_negative_rate()
    {
        Add(20, Start);
        Add(30, Start.AddMinutes(10));
        Add(15, Start.AddMinutes(20));

        Assert.Equal(QuotaForecastKind.Collecting, Forecast(15, Start.AddMinutes(20)).Kind);

        Add(25, Start.AddMinutes(30));
        Add(35, Start.AddMinutes(40));
        var estimate = Forecast(35, Start.AddMinutes(40));

        Assert.Equal(QuotaForecastKind.Estimate, estimate.Kind);
        Assert.True(estimate.PercentagePointsPerHour > 0);
    }

    [Fact]
    public void Reset_rollover_plan_change_and_limit_change_never_mix_series()
    {
        Add(10, Start);
        Add(20, Start.AddMinutes(10));
        Add(30, Start.AddMinutes(20));

        var nextReset = Start.AddHours(6);
        Add(5, Start.AddMinutes(30), nextReset);
        Assert.Equal(QuotaForecastKind.Collecting, Forecast(5, Start.AddMinutes(30), nextReset).Kind);

        var newPlan = Quota(6, Start.AddMinutes(40), nextReset, plan: "Max");
        _clock.Set(Start.AddMinutes(40));
        _engine.Observe(newPlan, _clock.GetUtcNow());
        Assert.Equal(QuotaForecastKind.Collecting,
            _engine.Forecast(newPlan, newPlan.Limits[0], _clock.GetUtcNow()).Kind);

        var newLimit = Quota(7, Start.AddMinutes(50), nextReset, plan: "Max", identity: "weekly_window");
        _clock.Set(Start.AddMinutes(50));
        _engine.Observe(newLimit, _clock.GetUtcNow());
        Assert.Equal(QuotaForecastKind.Collecting,
            _engine.Forecast(newLimit, newLimit.Limits[0], _clock.GetUtcNow()).Kind);
    }

    [Fact]
    public void Invalid_out_of_order_and_future_observations_cannot_create_a_forecast()
    {
        Add(30, Start.AddMinutes(10));
        var outOfOrder = Quota(20, Start.AddMinutes(5));
        _engine.Observe(outOfOrder, Start.AddMinutes(10));
        Assert.Equal(1, _engine.ObservationCount);

        var future = Quota(50, Start.AddMinutes(11));
        _engine.Observe(future, Start.AddMinutes(10));
        Assert.Equal(0, _engine.ObservationCount);
        Assert.Equal(QuotaForecastKind.Stale, _engine.Forecast(future, future.Limits[0], Start.AddMinutes(10)).Kind);
    }

    [Fact]
    public void Forecast_has_explicit_safe_results_for_unknown_unlimited_exhausted_and_past_reset()
    {
        var unknown = Quota(null, Start);
        Assert.Equal(QuotaForecastKind.UsageUnknown, Forecast(unknown).Kind);

        var unlimited = Quota(null, Start, unlimited: true);
        Assert.Equal(QuotaForecastKind.Unlimited, Forecast(unlimited).Kind);

        var missingReset = Quota(25, Start, includeDefaultReset: false);
        Assert.Equal(QuotaForecastKind.ResetUnavailable, Forecast(missingReset).Kind);

        var exhausted = Quota(100, Start);
        Assert.Equal(QuotaForecastKind.Exhausted, Forecast(exhausted).Kind);

        var resetDue = Quota(80, Start, resetAt: Start);
        Assert.Equal(QuotaForecastKind.ResetDue, Forecast(resetDue).Kind);
    }

    [Fact]
    public void Stale_observations_clear_history_and_are_never_counted()
    {
        Add(20, Start);
        Add(30, Start.AddMinutes(10));
        Add(40, Start.AddMinutes(20));

        var stale = Quota(50, Start.AddMinutes(30), stale: true);
        _clock.Set(Start.AddMinutes(30));
        _engine.Observe(stale, _clock.GetUtcNow());

        Assert.Equal(0, _engine.ObservationCount);
        Assert.Equal(QuotaForecastKind.Stale, _engine.Forecast(stale, stale.Limits[0], _clock.GetUtcNow()).Kind);
    }

    [Fact]
    public void Persisted_history_recovers_only_while_the_current_observation_is_recent()
    {
        var file = Path.Combine(Path.GetTempPath(), $"deskweave-history-{Guid.NewGuid():N}.json");
        try
        {
            Add(10, Start);
            Add(20, Start.AddMinutes(10));
            Add(30, Start.AddMinutes(20));
            var current = Quota(30, Start.AddMinutes(20));
            _engine.Save(file);

            var restored = new QuotaForecastEngine(_clock);
            restored.Load(file);
            Assert.Equal(QuotaForecastKind.Estimate, restored.Forecast(current, current.Limits[0], _clock.GetUtcNow()).Kind);

            _clock.Set(Start.AddMinutes(31));
            var expired = restored.Forecast(current, current.Limits[0], _clock.GetUtcNow());
            Assert.Equal(QuotaForecastKind.Stale, expired.Kind);
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".tmp");
        }
    }

    [Fact]
    public void Malformed_history_returns_an_explicit_load_warning()
    {
        var file = Path.Combine(Path.GetTempPath(), $"deskweave-history-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, "{not-json");

            var warning = _engine.Load(file);

            Assert.Equal("Quota history could not be read; estimates will collect fresh samples.", warning);
            Assert.Equal(0, _engine.ObservationCount);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Observations\":[null],\"Providers\":[]}")]
    [InlineData("{\"Version\":1,\"Observations\":[],\"Providers\":[null]}")]
    public void Null_entries_in_history_return_an_explicit_load_warning(string json)
    {
        var file = Path.Combine(Path.GetTempPath(), $"deskweave-history-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, json);

            var warning = _engine.Load(file);

            Assert.Equal(
                "Quota history is invalid or unsupported; estimates will collect fresh samples.",
                warning);
            Assert.Equal(0, _engine.ObservationCount);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void History_save_failure_returns_an_explicit_warning()
    {
        var directory = Directory.CreateTempSubdirectory("deskweave-history-save-").FullName;
        var pathBlocker = Path.Combine(directory, "not-a-directory");
        try
        {
            File.WriteAllText(pathBlocker, "file");

            var warning = _engine.Save(Path.Combine(pathBlocker, "history.json"));

            Assert.Equal("Quota history could not be saved; forecasts may not survive restart.", warning);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void History_is_bounded_and_contains_no_credentials_or_display_labels()
    {
        var file = Path.Combine(Path.GetTempPath(), $"deskweave-history-{Guid.NewGuid():N}.json");
        try
        {
            var providers = new[]
            {
                (QuotaProviders.Claude, "Pro"),
                (QuotaProviders.Codex, "Plus"),
                (QuotaProviders.Copilot, "Pro"),
                (QuotaProviders.Antigravity, "Google AI Pro"),
            };
            for (var sample = 0; sample < 13; sample++)
            {
                var observedAt = Start.AddMinutes(sample * 5);
                var resetAt = Start.AddHours(3);
                _clock.Set(observedAt);
                foreach (var (provider, plan) in providers)
                {
                    var limits = Enumerable.Range(0, 64)
                        .Select(index => new QuotaLimit(
                            $"Translated label {index}",
                            10 + sample,
                            resetAt,
                            Identity: $"limit-{index}"))
                        .ToArray();
                    _engine.Observe(
                        new ProviderQuota(provider, QuotaProviders.Name(provider), plan, limits, null, observedAt),
                        observedAt);
                }
            }

            _engine.Save(file);
            var json = File.ReadAllText(file);

            Assert.Equal(QuotaForecastEngine.MaximumObservations, _engine.ObservationCount);
            Assert.True(new FileInfo(file).Length <= 512 * 1_024);
            Assert.DoesNotContain("Translated label", json, StringComparison.Ordinal);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("account", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".tmp");
        }
    }

    private void Add(
        double? used,
        DateTimeOffset observedAt,
        DateTimeOffset? resetAt = null,
        string plan = "Pro",
        string identity = "five_hour")
    {
        _clock.Set(observedAt);
        _engine.Observe(Quota(used, observedAt, resetAt, plan, identity), observedAt);
    }

    private QuotaForecast Forecast(double? used, DateTimeOffset fetchedAt, DateTimeOffset? resetAt = null)
    {
        var quota = Quota(used, fetchedAt, resetAt);
        return _engine.Forecast(quota, quota.Limits[0], fetchedAt);
    }

    private QuotaForecast Forecast(ProviderQuota quota) =>
        _engine.Forecast(quota, quota.Limits[0], _clock.GetUtcNow());

    private static ProviderQuota Quota(
        double? used,
        DateTimeOffset fetchedAt,
        DateTimeOffset? resetAt = null,
        string plan = "Pro",
        string identity = "five_hour",
        bool stale = false,
        bool unlimited = false,
        bool includeDefaultReset = true) =>
        new(QuotaProviders.Claude, "Claude", plan,
            [new QuotaLimit(
                "5h",
                used,
                resetAt ?? (includeDefaultReset ? DefaultReset : null),
                Unlimited: unlimited,
                Identity: identity)],
            null, fetchedAt, stale);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Set(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
