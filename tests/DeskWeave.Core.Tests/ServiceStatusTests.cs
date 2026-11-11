using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DeskWeave.Core.GitHubStatus;

namespace DeskWeave.Core.Tests;

public class ServiceStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private const string Healthy = """
        {"status":{"indicator":"none","description":"All Systems Operational"},
         "components":[{"name":"GitHub Copilot","status":"operational"}],
         "incidents":[],"scheduled_maintenances":[]}
        """;

    [Fact]
    public void Settings_DefaultToAllAndPersistEmptyAndSelectedProviders()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deskweave-status-{Guid.NewGuid():N}.json");
        try
        {
            var store = new StatusSettingsStore(path);
            Assert.Equal(StatusProviders.All.Select(p => p.Id).Order(), store.Load().Enabled.Order());
            store.Save([]);
            Assert.Empty(store.Load().Enabled);
            store.Save([StatusProviders.OpenAI, StatusProviders.GitHub]);
            Assert.Equal(new[] { StatusProviders.GitHub, StatusProviders.OpenAI }.Order(), store.Load().Enabled.Order());
            Assert.Null(store.Load().Warning);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("""{"Enabled":["unexpected"]}""")]
    [InlineData("""{"Enabled":null}""")]
    public void Settings_InvalidFileReportsWarningAndUsesDefaults(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"deskweave-status-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, contents);
            var settings = new StatusSettingsStore(path).Load();
            Assert.NotNull(settings.Warning);
            Assert.Equal(StatusProviders.All.Select(p => p.Id).Order(), settings.Enabled.Order());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Client_StaleCacheExpiresAtExactly24HoursAndHonorsRetryAfter()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deskweave-status-{Guid.NewGuid():N}.json");
        var clock = new TestClock(Now);
        try
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Healthy) });
            var client = new StatusClient(new HttpClient(handler), path, "https://example.test/summary",
                StatusParser.Parse, clock);
            var (fresh, delay) = await client.PollAsync(CancellationToken.None);
            Assert.True(fresh.Online);
            Assert.Equal(StatusClient.MinimumInterval, delay);
            Assert.Equal(Now, fresh.Summary!.FetchedAt);

            var offline = new StatusClient(new HttpClient(new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(90));
                return response;
            })), path, "https://example.test/summary", StatusParser.Parse, clock);
            Assert.NotNull(offline.LoadCached().Summary);
            var (stale, backoff) = await offline.PollAsync(CancellationToken.None);
            Assert.False(stale.Online);
            Assert.Equal(TimeSpan.FromMinutes(90), backoff);
            clock.UtcNow = Now.AddHours(24).AddTicks(-1);
            Assert.NotNull(offline.Publish(false).Summary);
            clock.UtcNow = Now.AddHours(24);
            Assert.Null(offline.Publish(false).Summary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Client_HonorsRetryAfterDateOnOtherErrorResponses()
    {
        var clock = new TestClock(Now);
        var path = Path.Combine(Path.GetTempPath(), $"deskweave-status-{Guid.NewGuid():N}.json");
        var client = new StatusClient(new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddMinutes(7));
            return response;
        })), path, "https://example.test/summary", StatusParser.Parse, clock);
        var (reading, delay) = await client.PollAsync(CancellationToken.None);
        Assert.False(reading.Online);
        Assert.Equal(TimeSpan.FromMinutes(7), delay);
    }

    [Fact]
    public void Client_RejectsFutureAndOversizedCachedReadings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deskweave-status-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(StatusParser.Parse(Healthy, Now.AddMinutes(1))));
            var client = new StatusClient(new HttpClient(new StubHandler(_ => throw new InvalidOperationException())),
                path, "https://example.test/summary", StatusParser.Parse, new TestClock(Now));
            Assert.Null(client.LoadCached().Summary);
            File.WriteAllText(path, new string('x', 2_000_001));
            Assert.Null(client.LoadCached().Summary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"status":{},"components":[]}""")]
    public async Task Client_InvalidResponseDoesNotReplaceLastKnownData(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"deskweave-status-{Guid.NewGuid():N}.json");
        try
        {
            var responses = new Queue<string>([Healthy, body]);
            var client = new StatusClient(new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses.Dequeue()) })), path);
            Assert.True((await client.PollAsync(CancellationToken.None)).Reading.Online);
            var reading = (await client.PollAsync(CancellationToken.None)).Reading;
            Assert.False(reading.Online);
            Assert.Equal("GitHub Copilot", Assert.Single(reading.Summary!.Components).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Client_RejectsOversizedResponseAndPropagatesCancellation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deskweave-status-{Guid.NewGuid():N}.json");
        try
        {
            var client = new StatusClient(new HttpClient(new StubHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2_000_001)) })), path);
            Assert.Null((await client.PollAsync(CancellationToken.None)).Reading.Summary);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PollAsync(cancellation.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
