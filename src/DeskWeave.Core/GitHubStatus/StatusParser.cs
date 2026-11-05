using System.Text.Json;

namespace DeskWeave.Core.GitHubStatus;

public sealed record ServiceComponent(string Name, string Status, string Label, int Severity, int Position);

public sealed record Incident(string Name, string Status, string Impact);

public sealed record Maintenance(string Name, string Status, DateTimeOffset? StartsAt);

public sealed record StatusSummary(
    string Description,
    string Indicator,
    IReadOnlyList<ServiceComponent> Components,
    IReadOnlyList<Incident> Incidents,
    IReadOnlyList<Maintenance> Maintenance,
    DateTimeOffset FetchedAt);

/// <summary>Normalizes GitHub's public Statuspage summary.json, mirroring the Omarchy widget rules.</summary>
public static class StatusParser
{
    private static readonly Dictionary<string, (string Label, int Severity)> States = new()
    {
        ["operational"] = ("Operational", 0),
        ["under_maintenance"] = ("Maintenance", 1),
        ["degraded_performance"] = ("Degraded", 2),
        ["partial_outage"] = ("Partial outage", 3),
        ["major_outage"] = ("Major outage", 4),
    };

    private static readonly HashSet<string> Indicators = ["none", "minor", "major", "critical", "maintenance"];

    public static StatusSummary Parse(string json, DateTimeOffset fetchedAt)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement, fetchedAt);
    }

    public static StatusSummary Parse(JsonElement root, DateTimeOffset fetchedAt)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Invalid status summary");
        }

        var rows = new List<ServiceComponent>();
        foreach (var entry in components.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || IsTrue(entry, "group"))
            {
                continue;
            }

            var name = Text(entry, "name", 100);
            if (name.Length == 0)
            {
                continue;
            }

            var raw = String(entry, "status") ?? "unknown";
            if (raw == "operational" && (IsFalse(entry, "showcase") || IsTrue(entry, "only_show_if_degraded")))
            {
                continue;
            }

            var known = States.TryGetValue(raw, out var state);
            var position = entry.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pos) ? pos : 999;
            rows.Add(new ServiceComponent(name, known ? raw : "unknown", known ? state.Label : "Unknown", known ? state.Severity : 5, position));
        }

        if (rows.Count == 0)
        {
            throw new FormatException("No service statuses in response");
        }

        rows = [.. rows.OrderByDescending(r => r.Severity).ThenBy(r => r.Position)];

        var incidents = new List<Incident>();
        foreach (var entry in Array(root, "incidents"))
        {
            var phase = String(entry, "status");
            if (phase is "resolved" or "postmortem")
            {
                continue;
            }

            incidents.Add(new Incident(
                Fallback(Text(entry, "name"), "Active incident"),
                Capitalize(Fallback(Text(entry, "status", 30), "investigating").Replace('_', ' ')),
                Fallback(Text(entry, "impact", 20), "minor")));
        }

        var maintenance = new List<Maintenance>();
        foreach (var entry in Array(root, "scheduled_maintenances"))
        {
            var phase = String(entry, "status");
            if (phase is not ("scheduled" or "in_progress" or "verifying"))
            {
                continue;
            }

            DateTimeOffset? startsAt = DateTimeOffset.TryParse(String(entry, "scheduled_for"), out var start) ? start : null;
            maintenance.Add(new Maintenance(
                Fallback(Text(entry, "name"), "Scheduled maintenance"),
                Capitalize(phase.Replace('_', ' ')),
                startsAt));
        }

        var indicator = String(status, "indicator") is { } i && Indicators.Contains(i) ? i : "unknown";
        var description = Fallback(Text(status, "description"), "Status unavailable");

        // Never turn an unrecognized component state into a green all-clear.
        if (indicator == "none" && rows.Any(r => r.Status == "unknown"))
        {
            indicator = "unknown";
            description = "Some service statuses unavailable";
        }

        return new StatusSummary(description, indicator, rows, incidents, maintenance, fetchedAt);
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsFalse(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;

    private static string Text(JsonElement element, string name, int maximum = 200)
    {
        var raw = String(element, name) ?? string.Empty;
        var collapsed = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > maximum ? collapsed[..maximum] : collapsed;
    }

    private static string Fallback(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
}
