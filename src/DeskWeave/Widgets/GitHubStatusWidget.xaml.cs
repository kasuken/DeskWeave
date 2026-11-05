using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;
using DeskWeave.Core.GitHubStatus;

namespace DeskWeave.Widgets;

public partial class GitHubStatusWidget : UserControl
{
    private const int MaxIncidents = 2;

    public GitHubStatusWidget()
    {
        InitializeComponent();
    }

    public void Update(StatusReading reading)
    {
        var summary = reading.Summary;
        var stale = !reading.Online;

        if (summary is null)
        {
            OverallDot.Foreground = Brush("MutedBrush");
            OverallText.Text = reading.Checking ? "Checking…" : "Status unavailable";
            Components.ItemsSource = null;
            IncidentsPanel.Visibility = MaintenancePanel.Visibility = System.Windows.Visibility.Collapsed;
            Footer.Text = reading.Checking ? "Contacting githubstatus.com" : "Offline · no recent data";
            return;
        }

        // Offline readings are gray so an old all-clear never looks like a current check.
        OverallDot.Foreground = stale ? Brush("MutedBrush") : IndicatorBrush(summary.Indicator);
        OverallText.Text = summary.Description;

        Components.ItemsSource = summary.Components
            .Select(c => new ComponentRow(c.Name, c.Label, stale ? Brush("MutedBrush") : StatusBrush(c.Status)))
            .ToList();

        Incidents.ItemsSource = summary.Incidents.Take(MaxIncidents).ToList();
        var extra = summary.Incidents.Count - MaxIncidents;
        MoreIncidents.Text = extra > 0 ? $"+{extra} more on githubstatus.com" : string.Empty;
        MoreIncidents.Visibility = extra > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        IncidentsPanel.Visibility = summary.Incidents.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        Maintenance.ItemsSource = summary.Maintenance
            .Select(m => new MaintenanceRow(m.Name, m.StartsAt is { } start
                ? $"{m.Status} · {start.ToLocalTime().ToString("ddd dd MMM HH:mm", CultureInfo.InvariantCulture)}"
                : m.Status))
            .ToList();
        MaintenancePanel.Visibility = summary.Maintenance.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        var fetched = summary.FetchedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        Footer.Text = stale
            ? $"Offline · cached from {summary.FetchedAt.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture)}"
            : $"Updated {fetched} · githubstatus.com";
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

    private Brush StatusBrush(string status) => status switch
    {
        "operational" => Brush("GreenBrush"),
        "under_maintenance" => Brush("BlueBrush"),
        "degraded_performance" => Brush("YellowBrush"),
        "partial_outage" => Brush("OrangeBrush"),
        "major_outage" => Brush("RedBrush"),
        _ => Brush("MutedBrush"),
    };

    private Brush Brush(string key) => (Brush)FindResource(key);

    public sealed record ComponentRow(string Name, string Label, Brush Brush);

    public sealed record MaintenanceRow(string Name, string Detail);
}
