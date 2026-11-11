using System.Net;
using System.Text;
using System.Text.Json;
using DeskWeave.Core.AiQuota;

namespace DeskWeave.Core.Tests;

public class AiQuotaForecastIntegrationTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Invalid_timestamp_from_one_provider_does_not_stop_other_provider_results()
    {
        var home = Directory.CreateTempSubdirectory("deskweave-invalid-quota-").FullName;

        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            File.WriteAllText(
                Path.Combine(home, ".claude", ".credentials.json"),
                """{"claudeAiOauth":{"accessToken":"test-token","subscriptionType":"pro","expiresAt":10000000000000000}}""");

            var sources = new QuotaSources(
                home,
                static _ => null,
                static (_, _, _) => Task.FromResult<CommandResult?>(
                    new CommandResult(0, """{"copilot_plan":"business","quota_snapshots":{"chat":{"unlimited":true}}}""")),
                static () => null);
            using var http = new HttpClient(new UsageHandler(Start.AddHours(5)));
            var service = new AiQuotaService(
                http,
                sources,
                Path.Combine(home, "ai-quota.json"),
                new ManualTimeProvider(Start));

            var quotas = await service.RefreshAsync(CancellationToken.None);

            var copilot = Assert.Single(quotas);
            Assert.Equal(QuotaProviders.Copilot, copilot.Id);
            Assert.True(Assert.Single(copilot.Limits).Unlimited);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task Service_persists_only_fresh_observations_and_recovers_a_recent_forecast()
    {
        var home = Directory.CreateTempSubdirectory("deskweave-forecast-").FullName;
        var cacheFile = Path.Combine(home, "ai-quota.json");
        var historyFile = Path.Combine(home, "ai-quota-history.json");
        var clock = new ManualTimeProvider(Start);
        var handler = new UsageHandler(Start.AddHours(5));

        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            File.WriteAllText(
                Path.Combine(home, ".claude", ".credentials.json"),
                """{"claudeAiOauth":{"accessToken":"test-token","subscriptionType":"pro"}}""");

            var sources = new QuotaSources(
                home,
                static _ => null,
                static (_, _, _) => Task.FromResult<CommandResult?>(null),
                static () => null);
            using var http = new HttpClient(handler);
            var service = new AiQuotaService(http, sources, cacheFile, clock, historyFile);

            IReadOnlyList<ProviderQuota> quotas = [];
            foreach (var minute in new[] { 0, 8, 16 })
            {
                clock.Set(Start.AddMinutes(minute));
                quotas = await service.RefreshAsync(CancellationToken.None);
            }

            var quota = Assert.Single(quotas);
            Assert.Equal(QuotaForecastKind.Estimate,
                service.Forecasts[new QuotaLimitKey(QuotaProviders.Claude, "Pro", "five_hour")].Kind);
            Assert.Equal(3, handler.RequestCount);

            var historyText = File.ReadAllText(historyFile);
            Assert.DoesNotContain("test-token", historyText, StringComparison.Ordinal);
            Assert.DoesNotContain("accessToken", historyText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("account", historyText, StringComparison.OrdinalIgnoreCase);

            var restored = new AiQuotaService(http, sources, cacheFile, clock, historyFile);
            Assert.Single(restored.LoadCached());
            Assert.Equal(QuotaForecastKind.Estimate,
                restored.Forecasts[new QuotaLimitKey(QuotaProviders.Claude, "Pro", "five_hour")].Kind);
            Assert.Equal(3, handler.RequestCount);

            clock.Set(Start.AddMinutes(27));
            Assert.Equal(QuotaForecastKind.Stale,
                restored.Forecasts[new QuotaLimitKey(QuotaProviders.Claude, "Pro", "five_hour")].Kind);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task History_write_failure_is_reported_without_failing_provider_refresh()
    {
        var home = Directory.CreateTempSubdirectory("deskweave-history-failure-").FullName;
        var cacheFile = Path.Combine(home, "ai-quota.json");
        var historyPathBlocker = Path.Combine(home, "not-a-directory");
        var clock = new ManualTimeProvider(Start);

        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            File.WriteAllText(
                Path.Combine(home, ".claude", ".credentials.json"),
                """{"claudeAiOauth":{"accessToken":"test-token","subscriptionType":"pro"}}""");
            File.WriteAllText(historyPathBlocker, "file");

            var sources = new QuotaSources(
                home,
                static _ => null,
                static (_, _, _) => Task.FromResult<CommandResult?>(null),
                static () => null);
            using var http = new HttpClient(new UsageHandler(Start.AddHours(5)));
            var service = new AiQuotaService(
                http,
                sources,
                cacheFile,
                clock,
                Path.Combine(historyPathBlocker, "ai-quota-history.json"));

            var quotas = await service.RefreshAsync(CancellationToken.None);

            var quota = Assert.Single(quotas);
            Assert.Equal(QuotaProviders.Claude, quota.Id);
            Assert.Equal(
                "Quota history could not be saved; forecasts may not survive restart.",
                service.HistoryWarning);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private sealed class UsageHandler(DateTimeOffset resetAt) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var usedPercent = (RequestCount++ + 1) * 10;
            var body = JsonSerializer.Serialize(new
            {
                five_hour = new { utilization = usedPercent, resets_at = resetAt },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Set(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
