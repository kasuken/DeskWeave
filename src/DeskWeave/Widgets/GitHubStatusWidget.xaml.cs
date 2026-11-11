using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskWeave.Core.GitHubStatus;

namespace DeskWeave.Widgets;

public partial class GitHubStatusWidget : UserControl
{
    private const int MaxProviders = 3;
    private const int MaxComponents = 5;
    private const int MaxIncidents = 2;
    private const int MaxMaintenance = 1;

    private static readonly HashSet<string> KnownProviderIds = new(StringComparer.OrdinalIgnoreCase)
    {
        StatusProviders.GitHub,
        StatusProviders.Anthropic,
        StatusProviders.OpenAI,
    };

    public GitHubStatusWidget()
    {
        InitializeComponent();
        Update([]);
    }

    /// <summary>Updates the widget with the selected providers' latest status readings.</summary>
    public void Update(IReadOnlyList<ProviderStatusReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var known = readings.Where(reading => KnownProviderIds.Contains(reading.Id)).ToArray();
        var selected = known.Take(MaxProviders).ToArray();
        var providers = selected.Select(CreateCard).ToArray();

        Providers.ItemsSource = providers;
        var gitHubIndex = Array.FindIndex(selected, provider =>
            StringComparer.OrdinalIgnoreCase.Equals(provider.Id, StatusProviders.GitHub));
        var gitHub = gitHubIndex >= 0 ? providers[gitHubIndex] : null;
        GitHubDetails.DataContext = gitHub;
        GitHubDetails.Visibility = gitHub is not null
            && (gitHub.ComponentsVisibility == Visibility.Visible
                || gitHub.IncidentsVisibility == Visibility.Visible
                || gitHub.MaintenanceVisibility == Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProviderCount.Text = providers.Length == 1 ? "1 provider" : $"{providers.Length} providers";
        EmptyState.Visibility = providers.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var extra = known.Length - providers.Length;
        ProviderOverflow.Text = Overflow(extra, "provider");
        ProviderOverflow.Visibility = extra > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ProviderCard CreateCard(ProviderStatusReading provider)
    {
        var isGitHub = StringComparer.OrdinalIgnoreCase.Equals(provider.Id, StatusProviders.GitHub);
        var name = Shorten(provider.Name, 24);
        var summary = provider.Reading.Summary;
        var stale = !provider.Reading.Online;

        if (summary is null)
        {
            var status = provider.Reading.Checking ? "Checking…" : "Unavailable";
            var freshnessText = provider.Reading.Checking
                ? "Checking · no recent success"
                : "Unavailable · no recent success";

            return new ProviderCard(
                name,
                Brush("MutedBrush"),
                status,
                Brush("MutedBrush"),
                freshnessText,
                Brush("MutedBrush"),
                string.Empty,
                Brush("MutedBrush"),
                Visibility.Collapsed,
                [],
                string.Empty,
                Visibility.Collapsed,
                Visibility.Collapsed,
                Brush("MutedBrush"),
                [],
                string.Empty,
                Visibility.Collapsed,
                Visibility.Collapsed,
                Brush("MutedBrush"),
                [],
                string.Empty,
                Visibility.Collapsed,
                Visibility.Collapsed,
                Brush("MutedBrush"),
                Visibility.Collapsed,
                Visibility.Collapsed,
                Visibility.Collapsed);
        }

        var orderedComponents = summary.Components
            .OrderByDescending(component => component.Severity)
            .ThenBy(component => component.Position)
            .ToArray();
        var componentRows = isGitHub
            ? SelectGitHubComponents(orderedComponents, stale)
            : [];
        var incidents = summary.Incidents.Take(MaxIncidents)
            .Select(incident => ToIncidentRow(incident, stale))
            .ToArray();
        var maintenance = summary.Maintenance.Take(MaxMaintenance)
            .Select(item => ToMaintenanceRow(item, stale))
            .ToArray();
        var (detail, detailBrush) = isGitHub && incidents.Length > 0
            ? (string.Empty, Brush("MutedBrush"))
            : Highlight(summary, orderedComponents);
        var age = DateTimeOffset.UtcNow - summary.FetchedAt;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        var freshnessState = !provider.Reading.Online
            ? provider.Reading.Checking ? "Stale · checking" : "Stale"
            : provider.Reading.Checking ? "Checking" : "Updated";
        var freshness = $"{freshnessState} {FormatAge(age)}";

        var extraComponents = summary.Components.Count - componentRows.Length;
        var extraIncidents = summary.Incidents.Count - (isGitHub ? incidents.Length : Math.Min(1, incidents.Length));
        var extraMaintenance = summary.Maintenance.Count - maintenance.Length;

        return new ProviderCard(
            name,
            stale ? Brush("MutedBrush") : Brush("ForegroundBrush"),
            StatusLabel(summary.Indicator),
            stale ? Brush("MutedBrush") : IndicatorBrush(summary.Indicator),
            freshness,
            Brush("MutedBrush"),
            detail,
            stale ? Brush("MutedBrush") : detailBrush,
            detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            componentRows,
            Overflow(extraComponents, "component"),
            extraComponents > 0 ? Visibility.Visible : Visibility.Collapsed,
            isGitHub && componentRows.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            Brush("MutedBrush"),
            incidents,
            Overflow(extraIncidents, "incident"),
            extraIncidents > 0 ? Visibility.Visible : Visibility.Collapsed,
            isGitHub && incidents.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            stale ? Brush("MutedBrush") : Brush("OrangeBrush"),
            maintenance,
            Overflow(extraMaintenance, "maintenance"),
            extraMaintenance > 0 ? Visibility.Visible : Visibility.Collapsed,
            maintenance.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            stale ? Brush("MutedBrush") : Brush("BlueBrush"),
            isGitHub ? Visibility.Visible : Visibility.Collapsed,
            !isGitHub && maintenance.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            !isGitHub && extraIncidents > 0 ? Visibility.Visible : Visibility.Collapsed);
    }

    private ComponentRow[] SelectGitHubComponents(IReadOnlyList<ServiceComponent> components, bool stale)
    {
        var prioritized = components.Where(component => IsPinnedGitHubComponent(component.Name)).ToList();
        var selected = prioritized.Take(MaxComponents).ToList();
        selected.AddRange(components
            .Where(component => !IsPinnedGitHubComponent(component.Name))
            .Take(Math.Max(0, MaxComponents - selected.Count)));

        return selected.Select(component => new ComponentRow(
            Shorten(component.Name, 44),
            Shorten(component.Label, 20),
            stale ? Brush("MutedBrush") : component.Status == "operational" ? Brush("DimBrush") : StatusBrush(component.Status),
            stale ? Brush("MutedBrush") : Brush("ForegroundBrush"))).ToArray();
    }

    private static bool IsPinnedGitHubComponent(string name) =>
        StringComparer.OrdinalIgnoreCase.Equals(name, "Copilot")
        || StringComparer.OrdinalIgnoreCase.Equals(name, "Copilot AI Model Providers");

    private (string Detail, Brush Brush) Highlight(
        StatusSummary summary,
        IReadOnlyList<ServiceComponent> components)
    {
        if (summary.Incidents.FirstOrDefault() is { } incident)
        {
            return (
                Shorten($"{incident.Name} · {incident.Status}", 68),
                IncidentBrush(incident.Impact));
        }

        if (components.FirstOrDefault(component =>
                component.Severity > 0 || !IsKnownComponentStatus(component.Status)) is { } component)
        {
            return (
                Shorten($"{component.Name} · {component.Label}", 68),
                StatusBrush(component.Status));
        }

        return (string.Empty, Brush("MutedBrush"));
    }

    private IncidentRow ToIncidentRow(Incident incident, bool stale) =>
        new(
            Shorten(incident.Name, 48),
            Shorten($"{incident.Status} · {incident.Impact}", 36),
            stale ? Brush("MutedBrush") : IncidentBrush(incident.Impact),
            stale ? Brush("MutedBrush") : Brush("ForegroundBrush"));

    private MaintenanceRow ToMaintenanceRow(Maintenance maintenance, bool stale)
    {
        var detail = maintenance.StartsAt is { } start
            ? $"{Shorten(maintenance.Status, 18)} · {start.ToLocalTime().ToString("ddd dd MMM HH:mm", CultureInfo.InvariantCulture)}"
            : Shorten(maintenance.Status, 36);

        return new MaintenanceRow(
            Shorten(maintenance.Name, 48),
            detail,
            stale ? Brush("MutedBrush") : Brush("BlueBrush"),
            stale ? Brush("MutedBrush") : Brush("ForegroundBrush"));
    }

    private Brush IndicatorBrush(string indicator) => indicator switch
    {
        "none" => Brush("GreenBrush"),
        "minor" => Brush("YellowBrush"),
        "major" => Brush("OrangeBrush"),
        "critical" => Brush("RedBrush"),
        "maintenance" => Brush("BlueBrush"),
        _ => Brush("MutedBrush"),
    };

    private static string StatusLabel(string indicator) => indicator switch
    {
        "none" => "Operational",
        "minor" => "Degraded",
        "major" => "Partial outage",
        "critical" => "Major outage",
        "maintenance" => "Maintenance",
        _ => "Unknown",
    };

    private Brush StatusBrush(string status) => status switch
    {
        "operational" => Brush("GreenBrush"),
        "under_maintenance" => Brush("BlueBrush"),
        "degraded_performance" => Brush("YellowBrush"),
        "partial_outage" => Brush("OrangeBrush"),
        "major_outage" or "full_outage" => Brush("RedBrush"),
        _ => Brush("MutedBrush"),
    };

    private Brush IncidentBrush(string impact) => impact switch
    {
        "minor" or "degraded_performance" => Brush("YellowBrush"),
        "major" or "partial_outage" => Brush("OrangeBrush"),
        "critical" or "major_outage" or "full_outage" => Brush("RedBrush"),
        "under_maintenance" => Brush("BlueBrush"),
        _ => Brush("MutedBrush"),
    };

    private static bool IsKnownComponentStatus(string status) => status is
        "operational" or "under_maintenance" or "degraded_performance" or "partial_outage" or "major_outage" or "full_outage";

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours}h ago";
        }

        return $"{(int)age.TotalDays}d ago";
    }

    private static string Overflow(int count, string item) => count switch
    {
        <= 0 => string.Empty,
        1 => "+1 more " + item,
        _ => $"+{count.ToString(CultureInfo.InvariantCulture)} more {item}s",
    };

    private static string Shorten(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength
            ? normalized
            : $"{normalized[..(maximumLength - 1)]}…";
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    public sealed record ComponentRow(string Name, string Label, Brush Brush, Brush NameBrush);

    public sealed record IncidentRow(string Name, string Detail, Brush Brush, Brush NameBrush);

    public sealed record MaintenanceRow(string Name, string Detail, Brush Brush, Brush NameBrush);

    public sealed record ProviderCard(
        string Name,
        Brush NameBrush,
        string Status,
        Brush StatusBrush,
        string Freshness,
        Brush FreshnessBrush,
        string Detail,
        Brush DetailBrush,
        Visibility DetailVisibility,
        IReadOnlyList<ComponentRow> Components,
        string MoreComponents,
        Visibility MoreComponentsVisibility,
        Visibility ComponentsVisibility,
        Brush ComponentsHeadingBrush,
        IReadOnlyList<IncidentRow> Incidents,
        string MoreIncidents,
        Visibility MoreIncidentsVisibility,
        Visibility IncidentsVisibility,
        Brush IncidentsHeadingBrush,
        IReadOnlyList<MaintenanceRow> Maintenance,
        string MoreMaintenance,
        Visibility MoreMaintenanceVisibility,
        Visibility MaintenanceVisibility,
        Brush MaintenanceHeadingBrush,
        Visibility GitHubDetailsVisibility,
        Visibility OtherMaintenanceVisibility,
        Visibility OtherIncidentOverflowVisibility);
}
