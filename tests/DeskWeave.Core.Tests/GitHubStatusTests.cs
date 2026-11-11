using System.Net;
using DeskWeave.Core.GitHubStatus;

namespace DeskWeave.Core.Tests;

public class GitHubStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private const string Sample = """
        {
          "status": { "indicator": "minor", "description": "Partially Degraded Service" },
          "components": [
            { "name": "Git Operations", "status": "operational", "position": 1 },
            { "name": "Actions", "status": "degraded_performance", "position": 5 },
            { "name": "Group", "status": "major_outage", "group": true, "position": 0 },
            { "name": "Hidden", "status": "operational", "showcase": false, "position": 2 },
            { "name": "Pages", "status": "partial_outage", "position": 9 },
            { "name": "Codespaces", "status": "under_maintenance", "position": 3 }
          ],
          "incidents": [
            { "name": "Actions delays", "status": "investigating", "impact": "minor" },
            { "name": "Old", "status": "resolved", "impact": "major" }
          ],
          "scheduled_maintenances": [
            { "name": "DB upgrade", "status": "verifying", "scheduled_for": "2026-11-05T10:00:00Z" },
            { "name": "Done", "status": "completed" }
          ]
        }
        """;

    [Fact]
    public void Parse_SortsBySeverityAndSkipsGroupsAndHiddenEntries()
    {
        var summary = StatusParser.Parse(Sample, Now);

        Assert.Equal(["Pages", "Actions", "Codespaces", "Git Operations"], summary.Components.Select(c => c.Name));
        Assert.Equal("Partial outage", summary.Components[0].Label);
        Assert.Equal("minor", summary.Indicator);
    }

    [Fact]
    public void Parse_KeepsActiveIncidentsAndMaintenance()
    {
        var summary = StatusParser.Parse(Sample, Now);

        var incident = Assert.Single(summary.Incidents);
        Assert.Equal("Investigating", incident.Status);
        var maintenance = Assert.Single(summary.Maintenance);
        Assert.Equal("Verifying", maintenance.Status);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero), maintenance.StartsAt);
    }

    [Fact]
    public void Parse_PreservesCopilotComponentAndDeduplicatesIncidentsById()
    {
        const string json = """
            { "status": { "indicator": "minor", "description": "Service disruption" },
              "components": [{ "name": "Copilot", "status": "operational", "showcase": false }],
              "incidents": [
                { "id": "i1", "name": "Incident", "status": "investigating" },
                { "id": "i1", "name": "Incident", "status": "investigating" },
                { "id": "i2", "name": "Incident", "status": "identified" }
              ] }
            """;

        var summary = StatusParser.Parse(json, Now);
        Assert.Equal("Copilot", Assert.Single(summary.Components).Name);
        Assert.Equal(2, summary.Incidents.Count);
    }

    [Fact]
    public void Parse_NeverReportsAllClearWithUnknownComponents()
    {
        const string json = """
            { "status": { "indicator": "none", "description": "All Systems Operational" },
              "components": [ { "name": "API", "status": "exploding" } ] }
            """;

        var summary = StatusParser.Parse(json, Now);

        Assert.Equal("unknown", summary.Indicator);
        Assert.Equal("Unknown", summary.Components[0].Label);
    }

    [Theory]
    [InlineData("degraded_performance", "minor", "Degraded")]
    [InlineData("partial_outage", "major", "Partial outage")]
    [InlineData("major_outage", "critical", "Major outage")]
    [InlineData("under_maintenance", "maintenance", "Maintenance")]
    [InlineData("new_status", "unknown", "Some service statuses unavailable")]
    public void Parse_ContradictoryAllClearDoesNotMaskComponentStatus(string status, string indicator, string description)
    {
        var json = $$"""
            { "status": { "indicator": "none", "description": "All Systems Operational" },
              "components": [ { "name": "Claude Code", "status": "{{status}}" } ] }
            """;
        var summary = StatusParser.Parse(json, Now);
        Assert.Equal(indicator, summary.Indicator);
        Assert.Equal(description, summary.Description);
    }

    [Fact]
    public void Parse_UnknownOverallIndicatorDoesNotDisplayAnAllClear()
    {
        const string json = """
            { "status": { "indicator": "new_indicator", "description": "All Systems Operational" },
              "components": [ { "name": "Claude API", "status": "operational" } ] }
            """;
        var summary = StatusParser.Parse(json, Now);
        Assert.Equal("unknown", summary.Indicator);
        Assert.Equal("Service status unknown", summary.Description);
    }

    [Fact]
    public void Parse_UnknownComponentDoesNotHideKnownOutage()
    {
        const string json = """
            { "status": { "indicator": "none", "description": "All Systems Operational" },
              "components": [ { "name": "Unknown", "status": "new_status" },
                              { "name": "API", "status": "major_outage" } ] }
            """;
        var summary = StatusParser.Parse(json, Now);
        Assert.Equal("critical", summary.Indicator);
        Assert.Equal("API", summary.Components[0].Name);
        Assert.Equal("Unknown", summary.Components[1].Label);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "status": {}, "components": [] }""")]
    public void Parse_RejectsInvalidPayloads(string json) =>
        Assert.Throws<FormatException>(() => StatusParser.Parse(json, Now));

    [Fact]
    public async Task Poll_CachesAndFallsBackToCacheWhenOffline()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"deskweave-{Guid.NewGuid():N}.json");
        try
        {
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sample) });
            var client = new StatusClient(new HttpClient(handler), cache);
            var (online, delay) = await client.PollAsync(CancellationToken.None);

            Assert.True(online.Online);
            Assert.Equal(StatusClient.MinimumInterval, delay);
            Assert.True(File.Exists(cache));

            var offlineHandler = new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
                return response;
            });
            var restarted = new StatusClient(new HttpClient(offlineHandler), cache);
            Assert.NotNull(restarted.LoadCached().Summary);

            var (offline, backoff) = await restarted.PollAsync(CancellationToken.None);
            Assert.False(offline.Online);
            Assert.NotNull(offline.Summary);
            Assert.Equal(TimeSpan.FromMinutes(5), backoff);
        }
        finally
        {
            File.Delete(cache);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
