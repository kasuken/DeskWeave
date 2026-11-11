using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskWeave.Core.GitHubStatus;
using DeskWeave.Widgets;

namespace DeskWeave.Tests;

[Collection("WPF")]
public class GitHubStatusWidgetTests(WpfFixture fixture)
{
    [Fact]
    public void Update_HealthyStatusShowsProviderNameSeverityAndFreshness()
    {
        fixture.Run(() =>
        {
            var widget = new GitHubStatusWidget();
            widget.Update([new(StatusProviders.GitHub, "GitHub", Reading(Summary(
                "none",
                "All systems operational",
                [
                    Component("Copilot", "operational", "Operational", 0, 0),
                    Component("Copilot AI Model Providers", "operational", "Operational", 0, 1),
                ])))]);

            var card = SingleCard(widget);

            Assert.Equal("GitHub", card.Name);
            Assert.Equal("Operational", card.Status);
            Assert.Same(Brush(widget, "GreenBrush"), card.StatusBrush);
            Assert.Contains("Updated", card.Freshness);
            Assert.Contains("ago", card.Freshness);
        });
    }

    [Fact]
    public void Update_OpenAiFullOutageComponentUsesRedSeverityColor()
    {
        fixture.Run(() =>
        {
            var widget = new GitHubStatusWidget();
            widget.Update([new(StatusProviders.OpenAI, "OpenAI", Reading(Summary(
                "major",
                "Major service impact",
                [Component("API", "full_outage", "Major outage", 4, 0)])))]);

            var card = SingleCard(widget);

            Assert.Contains("API · Major outage", card.Detail);
            Assert.Same(Brush(widget, "RedBrush"), card.DetailBrush);
        });
    }

    [Fact]
    public void Update_StaleStatusMutesProviderAndAllStatusDetails()
    {
        fixture.Run(() =>
        {
            var widget = new GitHubStatusWidget();
            var summary = Summary(
                "critical",
                "Major outage",
                [Component("API", "major_outage", "Major outage", 4, 0)],
                [new Incident("API unavailable", "Investigating", "critical")],
                [new Maintenance("Database work", "In progress", DateTimeOffset.UtcNow)]);
            widget.Update([new(StatusProviders.GitHub, "GitHub", Reading(summary, online: false))]);

            var card = SingleCard(widget);
            var muted = Brush(widget, "MutedBrush");

            Assert.StartsWith("Stale", card.Freshness);
            Assert.Same(muted, card.NameBrush);
            Assert.Same(muted, card.StatusBrush);
            Assert.Same(muted, card.DetailBrush);
            Assert.Same(muted, Assert.Single(card.Components).Brush);
            Assert.Same(muted, Assert.Single(card.Components).NameBrush);
            Assert.Same(muted, Assert.Single(card.Incidents).Brush);
            Assert.Same(muted, Assert.Single(card.Incidents).NameBrush);
            Assert.Same(muted, Assert.Single(card.Maintenance).Brush);
            Assert.Same(muted, Assert.Single(card.Maintenance).NameBrush);
            Assert.Same(muted, card.IncidentsHeadingBrush);
            Assert.Same(muted, card.MaintenanceHeadingBrush);
        });
    }

    [Theory]
    [InlineData(true, "Checking…", "Checking · no recent success")]
    [InlineData(false, "Unavailable", "Unavailable · no recent success")]
    public void Update_ReadingWithoutSummaryShowsCheckingOrUnavailable(
        bool checking,
        string expectedStatus,
        string expectedFreshness)
    {
        fixture.Run(() =>
        {
            var widget = new GitHubStatusWidget();
            widget.Update([new(
                StatusProviders.OpenAI,
                "OpenAI",
                new StatusReading(
                    null,
                    Online: false,
                    Checking: checking,
                    CheckedAt: DateTimeOffset.UtcNow))]);

            var card = SingleCard(widget);

            Assert.Equal(expectedStatus, card.Status);
            Assert.Equal(expectedFreshness, card.Freshness);
            Assert.Same(Brush(widget, "MutedBrush"), card.StatusBrush);
            Assert.Equal(Visibility.Collapsed, card.GitHubDetailsVisibility);
        });
    }

    [Fact]
    public void Update_UnknownRawStatusesAreMutedInsteadOfHealthy()
    {
        fixture.Run(() =>
        {
            var widget = new GitHubStatusWidget();
            widget.Update([new(StatusProviders.GitHub, "GitHub", Reading(Summary(
                "unknown",
                "Status unavailable",
                [Component("Unknown service", "unknown", "Unknown", 5, 0)])))]);

            var card = SingleCard(widget);

            Assert.Same(Brush(widget, "MutedBrush"), card.StatusBrush);
            Assert.Equal("Unknown", card.Status);
            Assert.Same(Brush(widget, "MutedBrush"), Assert.Single(card.Components).Brush);
        });
    }

    [Fact]
    public void Update_GitHubPinsBothCopilotComponentsWithinBoundedComponentRows()
    {
        fixture.Run(() =>
        {
            var components = new List<ServiceComponent>
            {
                Component("Copilot", "operational", "Operational", 0, 8),
                Component("Copilot AI Model Providers", "operational", "Operational", 0, 9),
            };
            components.AddRange(Enumerable.Range(0, 8)
                .Select(index => Component($"Affected service {index}", "major_outage", "Major outage", 4, index)));

            var widget = new GitHubStatusWidget();
            widget.Update([new(StatusProviders.GitHub, "GitHub", Reading(Summary(
                "major",
                "Major service impact",
                components)))]);

            var card = SingleCard(widget);
            var displayedNames = card.Components.Select(component => component.Name).ToArray();

            Assert.Equal(5, card.Components.Count);
            Assert.Contains("Copilot", displayedNames);
            Assert.Contains("Copilot AI Model Providers", displayedNames);
            Assert.Equal("+5 more components", card.MoreComponents);
            Assert.All(card.Components, component => Assert.True(component.Name.Length <= 44));
        });
    }

    [Fact]
    public void Update_LongIncidentAndMaintenanceContentIsTruncatedWithExplicitOverflow()
    {
        fixture.Run(() =>
        {
            const string longName = "A very long service or incident title that must never grow the widget height";
            var summary = Summary(
                "major",
                "Major service impact",
                Enumerable.Range(0, 8)
                    .Select(index => Component($"{longName} {index}", "partial_outage", "Partial outage", 3, index))
                    .ToArray(),
                Enumerable.Range(0, 5)
                    .Select(index => new Incident($"{longName} {index}", "Investigating", "major"))
                    .ToArray(),
                Enumerable.Range(0, 4)
                    .Select(index => new Maintenance($"{longName} {index}", "Verifying", DateTimeOffset.UtcNow))
                    .ToArray());
            var widget = new GitHubStatusWidget();
            widget.Update([new(StatusProviders.GitHub, "GitHub", Reading(summary))]);
            widget.Measure(new Size(340, 520));
            widget.Arrange(new Rect(0, 0, 340, 520));
            widget.UpdateLayout();

            var card = SingleCard(widget);

            Assert.Equal(5, card.Components.Count);
            Assert.Equal("+3 more components", card.MoreComponents);
            Assert.Equal(2, card.Incidents.Count);
            Assert.Equal("+3 more incidents", card.MoreIncidents);
            Assert.Single(card.Maintenance);
            Assert.Equal("+3 more maintenances", card.MoreMaintenance);
            Assert.All(card.Components, component => Assert.True(component.Name.Length <= 44));
            Assert.All(card.Incidents, incident => Assert.True(incident.Name.Length <= 48));
            Assert.All(card.Maintenance, maintenance => Assert.True(maintenance.Name.Length <= 48));
            Assert.True(widget.DesiredSize.Height <= 520);
            Assert.True(widget.DesiredSize.Width <= 340);
            Assert.All(Descendants<TextBlock>(widget), text => Assert.Equal(TextWrapping.NoWrap, text.TextWrapping));
        });
    }

    [Fact]
    public void Update_GitHubShowsComponentIncidentAndDistinctMaintenanceDetails()
    {
        fixture.Run(() =>
        {
            var summary = Summary(
                "minor",
                "Degraded performance",
                [
                    Component("Copilot", "operational", "Operational", 0, 1),
                    Component("Actions", "degraded_performance", "Degraded", 2, 2),
                ],
                [new Incident("Actions delays", "Investigating", "minor")],
                [new Maintenance(
                    "Database upgrade",
                    "Verifying",
                    new DateTimeOffset(2026, 11, 11, 18, 30, 0, TimeSpan.Zero))]);
            var widget = new GitHubStatusWidget();
            widget.Update([new(StatusProviders.GitHub, "GitHub", Reading(summary))]);

            var card = SingleCard(widget);

            Assert.Contains(card.Components, component => component.Name == "Copilot");
            Assert.Contains(card.Components, component => component.Name == "Actions");
            Assert.Equal(string.Empty, card.Detail);
            Assert.Equal("Actions delays", Assert.Single(card.Incidents).Name);
            Assert.Equal("Database upgrade", Assert.Single(card.Maintenance).Name);
            Assert.StartsWith("Verifying ·", Assert.Single(card.Maintenance).Detail);
            Assert.Equal(Visibility.Visible, card.MaintenanceVisibility);
        });
    }

    [Fact]
    public void Update_AiProviderShowsIncidentOverflowAndMaintenanceSeparately()
    {
        fixture.Run(() =>
        {
            var summary = Summary("minor", "Degraded",
                [Component("API", "degraded_performance", "Degraded", 2, 0)],
                [new Incident("API delays", "Investigating", "degraded_performance"),
                 new Incident("Codex delays", "Identified", "partial_outage")],
                [new Maintenance("API upgrade", "Scheduled", DateTimeOffset.UtcNow.AddHours(1))]);
            var widget = new GitHubStatusWidget();
            widget.Update([new(StatusProviders.OpenAI, "OpenAI", Reading(summary))]);
            var card = SingleCard(widget);

            Assert.Contains("API delays", card.Detail);
            Assert.Same(Brush(widget, "YellowBrush"), card.DetailBrush);
            Assert.Equal("+1 more incident", card.MoreIncidents);
            Assert.Equal(Visibility.Visible, card.OtherIncidentOverflowVisibility);
            Assert.Equal("API upgrade", Assert.Single(card.Maintenance).Name);
            Assert.Equal(Visibility.Visible, card.MaintenanceVisibility);
        });
    }

    [Fact]
    public void Update_IncidentHeavyProvidersDoNotClipLaterProviderCards()
    {
        fixture.Run(() =>
        {
            var summary = Summary("major", "Major service impact",
                Enumerable.Range(0, 10).Select(index => Component($"Service {index}", "partial_outage", "Partial outage", 3, index)).ToArray(),
                Enumerable.Range(0, 5).Select(index => new Incident($"Incident {index}", "Investigating", "major")).ToArray(),
                [new Maintenance("Maintenance", "Scheduled", DateTimeOffset.UtcNow.AddHours(1))]);
            var widget = new GitHubStatusWidget();
            widget.Update([
                new(StatusProviders.GitHub, "GitHub", Reading(summary with
                {
                    Maintenance = [new Maintenance("Database upgrade", "Scheduled", DateTimeOffset.UtcNow.AddHours(1))],
                })),
                new(StatusProviders.Anthropic, "Claude", Reading(summary)),
                new(StatusProviders.OpenAI, "OpenAI", Reading(summary)),
            ]);
            widget.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widget.Arrange(new Rect(widget.DesiredSize));
            widget.UpdateLayout();
            widget.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widget.Arrange(new Rect(widget.DesiredSize));
            widget.UpdateLayout();

            var openAi = Descendants<TextBlock>(widget).Single(text => text.Text == "OpenAI");
            var bounds = openAi.TransformToAncestor(widget).TransformBounds(new Rect(openAi.RenderSize));
            Assert.True(bounds.Bottom <= widget.RenderSize.Height);
            var maintenance = Descendants<TextBlock>(widget).Single(text => text.Text == "Database upgrade");
            var maintenanceBounds = maintenance.TransformToAncestor(widget).TransformBounds(new Rect(maintenance.RenderSize));
            Assert.True(maintenanceBounds.Bottom <= widget.RenderSize.Height);
            Assert.True(widget.DesiredSize.Height < 1000);
        });
    }

    [Fact]
    public void Update_EmptyProviderSelectionShowsEmptyState()
    {
        fixture.Run(() =>
        {
            var widget = new GitHubStatusWidget();
            widget.Update([]);

            Assert.Empty(((ItemsControl)widget.FindName("Providers")).Items);
            Assert.Equal("0 providers", ((TextBlock)widget.FindName("ProviderCount")).Text);
            Assert.Equal(Visibility.Visible, ((TextBlock)widget.FindName("EmptyState")).Visibility);
        });
    }

    [Fact]
    public void Update_AlignsProviderNamesAndKeepsOverviewBeforeGitHubDetails()
    {
        fixture.Run(() =>
        {
            var github = Summary("none", "All systems operational",
                [Component("Copilot", "operational", "Operational", 0, 0)]);
            var widget = new GitHubStatusWidget();
            widget.Update([
                new(StatusProviders.GitHub, "GitHub", Reading(github)),
                new(StatusProviders.Anthropic, "Claude", Reading(Summary("none", "All systems operational"))),
                new(StatusProviders.OpenAI, "OpenAI", Reading(Summary("none", "All systems operational"))),
            ]);
            widget.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widget.Arrange(new Rect(widget.DesiredSize));
            widget.UpdateLayout();

            var texts = Descendants<TextBlock>(widget).ToArray();
            var githubName = texts.Single(text => text.Text == "GitHub");
            var claudeName = texts.Single(text => text.Text == "Claude");
            var openAiName = texts.Single(text => text.Text == "OpenAI");
            var detailsHeading = texts.Single(text => text.Text == "GITHUB COMPONENTS");
            var githubPoint = githubName.TransformToAncestor(widget).Transform(new Point());
            var claudePoint = claudeName.TransformToAncestor(widget).Transform(new Point());
            var openAiPoint = openAiName.TransformToAncestor(widget).Transform(new Point());
            var detailsPoint = detailsHeading.TransformToAncestor(widget).Transform(new Point());

            Assert.Equal(githubPoint.X, claudePoint.X, 1);
            Assert.Equal(githubPoint.X, openAiPoint.X, 1);
            Assert.True(githubPoint.Y < claudePoint.Y && claudePoint.Y < openAiPoint.Y);
            Assert.True(openAiPoint.Y + openAiName.ActualHeight <= detailsPoint.Y);

            widget.Update([new(StatusProviders.OpenAI, "OpenAI", Reading(Summary("none", "Operational")))]);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)widget.FindName("GitHubDetails")).Visibility);
        });
    }

    private static GitHubStatusWidget.ProviderCard SingleCard(GitHubStatusWidget widget) =>
        Assert.Single(
            ((ItemsControl)widget.FindName("Providers")).Items
                .Cast<GitHubStatusWidget.ProviderCard>());

    private static Brush Brush(GitHubStatusWidget widget, string resourceKey) =>
        Assert.IsAssignableFrom<Brush>(widget.FindResource(resourceKey));

    private static StatusReading Reading(
        StatusSummary summary,
        bool online = true,
        bool checking = false) =>
        new(summary, online, checking, DateTimeOffset.UtcNow);

    private static StatusSummary Summary(
        string indicator,
        string description,
        IReadOnlyList<ServiceComponent>? components = null,
        IReadOnlyList<Incident>? incidents = null,
        IReadOnlyList<Maintenance>? maintenance = null) =>
        new(
            description,
            indicator,
            components ?? [],
            incidents ?? [],
            maintenance ?? [],
            DateTimeOffset.UtcNow.AddMinutes(-4));

    private static ServiceComponent Component(
        string name,
        string status,
        string label,
        int severity,
        int position) =>
        new(name, status, label, severity, position);

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
