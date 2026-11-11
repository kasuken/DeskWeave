using System.Net;
using DeskWeave.Core.GitHubStatus;

namespace DeskWeave.Core.Tests;

public class OpenAiStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private const string Status = """{"status":{"indicator":"none","description":"All Systems Operational"}}""";
    private const string Components = """
        {"components":[{"id":"api","name":"API","status":"operational"},
                       {"id":"codex","name":"Codex API","status":"operational"}]}
        """;
    private const string EmptyWidget = """
        {"ongoing_incidents":[],"in_progress_maintenances":[],"scheduled_maintenances":[]}
        """;

    [Fact]
    public void Parse_CombinesCompleteComponentsAndActiveIncidentsWithoutDuplicates()
    {
        const string widget = """
            {"ongoing_incidents":[
                {"id":"i1","name":"API delays","status":"investigating","current_worst_impact":"partial_outage",
                 "affected_components":[{"id":"api","name":"API","current_status":"partial_outage"}]},
                {"id":"i1","name":"API delays","status":"investigating","current_worst_impact":"partial_outage"}
             ],
             "scheduled_maintenances":[{"id":"m1","name":"Upgrade","status":"maintenance_scheduled",
                 "starts_at":"2026-11-05T14:00:00Z","affected_components":[]}],
             "in_progress_maintenances":[]}
            """;

        var result = OpenAiStatusParser.Parse([Status, Components, widget], Now);
        Assert.Equal("major", result.Indicator);
        Assert.Equal(["API", "Codex API"], result.Components.Select(c => c.Name));
        Assert.Equal("API delays", Assert.Single(result.Incidents).Name);
        Assert.Equal("Scheduled", Assert.Single(result.Maintenance).Status);
        Assert.Equal(Now.AddHours(2), result.Maintenance[0].StartsAt);
    }

    [Theory]
    [InlineData("operational", "none")]
    [InlineData("degraded_performance", "minor")]
    [InlineData("partial_outage", "major")]
    [InlineData("full_outage", "critical")]
    [InlineData("not_a_known_status", "unknown")]
    public void Parse_MapsWidgetImpactConservatively(string impact, string indicator)
    {
        var widget = $$"""
            {"ongoing_incidents":[{"id":"i","name":"API","status":"monitoring","current_worst_impact":"{{impact}}"}],
             "in_progress_maintenances":[],"scheduled_maintenances":[]}
            """;
        Assert.Equal(indicator, OpenAiStatusParser.Parse([Status, Components, widget], Now).Indicator);
    }

    [Fact]
    public void Parse_UsesAllComponentsAndDistinguishesActiveMaintenance()
    {
        const string components = """
            {"components":[{"name":"Login","status":"operational"},
                           {"name":"Codex Web","status":"degraded_performance"},
                           {"name":"Login","status":"operational"}]}
            """;
        const string widget = """
            {"ongoing_incidents":[],"scheduled_maintenances":[],
             "in_progress_maintenances":[{"id":"m","name":"Upgrade","status":"maintenance_in_progress",
                 "started_at":"2026-11-05T11:00:00Z"}]}
            """;
        var result = OpenAiStatusParser.Parse([Status, components, widget], Now);
        Assert.Equal("minor", result.Indicator);
        Assert.Equal(3, result.Components.Count);
        Assert.Equal("Codex Web", result.Components[0].Name);
        Assert.Equal("In progress", Assert.Single(result.Maintenance).Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"ongoing_incidents":[],"in_progress_maintenances":[],"scheduled_maintenances":null}""")]
    public void Parse_MissingOrInvalidWidgetArraysAreNotAnAllClear(string widget) =>
        Assert.Throws<FormatException>(() => OpenAiStatusParser.Parse([Status, Components, widget], Now));

    [Fact]
    public async Task Client_UsesOnlyCompleteSnapshotsWhenOneOpenAiFeedFails()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"deskweave-status-{Guid.NewGuid():N}.json");
        try
        {
            var requests = new List<string>();
            var failWidget = false;
            using var http = new HttpClient(new StubHandler(request =>
            {
                var endpoint = request.RequestUri!.AbsolutePath;
                requests.Add(endpoint);
                if (endpoint == "/widget" && failWidget) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var body = endpoint switch { "/status" => Status, "/components" => Components, _ => EmptyWidget };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            }));
            var client = new StatusClient(http, cache,
                ["https://example.test/status", "https://example.test/components", "https://example.test/widget"],
                OpenAiStatusParser.Parse);
            Assert.True((await client.PollAsync(CancellationToken.None)).Reading.Online);
            failWidget = true;
            var stale = (await client.PollAsync(CancellationToken.None)).Reading;
            Assert.False(stale.Online);
            Assert.NotNull(stale.Summary);
            Assert.Equal(6, requests.Count);
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
