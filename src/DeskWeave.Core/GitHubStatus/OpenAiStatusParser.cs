using System.Text.Json;

namespace DeskWeave.Core.GitHubStatus;

/// <summary>Combines OpenAI's compatibility feeds with its incident.io widget summary.</summary>
public static class OpenAiStatusParser
{
    public static StatusSummary Parse(IReadOnlyList<string> bodies, DateTimeOffset fetchedAt)
    {
        if (bodies.Count != 3) throw new FormatException("Incomplete OpenAI status feeds");
        using var status = JsonDocument.Parse(bodies[0]);
        using var components = JsonDocument.Parse(bodies[1]);
        using var widget = JsonDocument.Parse(bodies[2]);
        var summary = StatusParser.Parse(status.RootElement, components.RootElement, fetchedAt);
        var root = widget.RootElement;
        var incidents = RequiredArray(root, "ongoing_incidents");
        var scheduled = RequiredArray(root, "scheduled_maintenances");
        var inProgress = RequiredArray(root, "in_progress_maintenances");
        var incidentRows = new List<Incident>();
        var maintenanceRows = new List<Maintenance>();
        var seenIncidents = new HashSet<string>(StringComparer.Ordinal);
        var seenMaintenance = new HashSet<string>(StringComparer.Ordinal);
        var maxSeverity = IndicatorSeverity(summary.Indicator);
        var unknown = summary.Indicator == "unknown" || summary.Components.Any(c => c.Status == "unknown");
        foreach (var component in summary.Components)
        {
            AddSeverity(component.Status, ref maxSeverity, ref unknown);
        }

        foreach (var item in incidents.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Invalid OpenAI incident");
            var name = Text(item, "name", "Active incident");
            var id = Text(item, "id", name);
            if (!seenIncidents.Add(id)) continue;
            var phase = Text(item, "status", "unknown");
            if (phase is not ("investigating" or "identified" or "monitoring")) unknown = true;
            incidentRows.Add(new Incident(name, phase switch
            {
                "investigating" => "Investigating",
                "identified" => "Identified",
                "monitoring" => "Monitoring",
                _ => "Unknown",
            }, Text(item, "current_worst_impact", "unknown")));
            AddSeverity(Text(item, "current_worst_impact", "unknown"), ref maxSeverity, ref unknown);
            AddAffectedSeverity(item, ref maxSeverity, ref unknown);
        }

        foreach (var item in scheduled.EnumerateArray().Concat(inProgress.EnumerateArray()))
        {
            if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Invalid OpenAI maintenance");
            var name = Text(item, "name", "Scheduled maintenance");
            var id = Text(item, "id", name);
            if (!seenMaintenance.Add(id)) continue;
            var phase = Text(item, "status", "unknown");
            DateTimeOffset? start = DateTimeOffset.TryParse(Text(item, "starts_at", ""), out var scheduledAt)
                ? scheduledAt
                : DateTimeOffset.TryParse(Text(item, "started_at", ""), out var startedAt) ? startedAt : null;
            maintenanceRows.Add(new Maintenance(name, phase switch
            {
                "maintenance_scheduled" => "Scheduled",
                "maintenance_in_progress" => "In progress",
                _ => "Unknown",
            }, start));
            if (phase == "maintenance_in_progress") maxSeverity = Math.Max(maxSeverity, 1);
            else if (phase != "maintenance_scheduled") unknown = true;
            AddAffectedSeverity(item, ref maxSeverity, ref unknown);
        }

        var indicator = maxSeverity switch
        {
            >= 4 => "critical",
            3 => "major",
            2 => "minor",
            1 => "maintenance",
            _ when unknown => "unknown",
            _ => "none",
        };
        var description = indicator == summary.Indicator ? summary.Description : indicator switch
        {
            "critical" => "Major outage",
            "major" => "Partial outage",
            "minor" => "Degraded",
            "maintenance" => "Maintenance",
            _ => "Some service statuses unavailable",
        };

        return summary with
        {
            Indicator = indicator,
            Description = description,
            Incidents = incidentRows,
            Maintenance = maintenanceRows,
        };
    }

    private static JsonElement RequiredArray(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"Missing OpenAI {name}");
        }
        return value;
    }

    private static string Text(JsonElement item, string property, string fallback)
    {
        var raw = item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        var text = string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? fallback : text.Length <= 200 ? text : text[..200];
    }

    private static void AddAffectedSeverity(JsonElement item, ref int severity, ref bool unknown)
    {
        if (!item.TryGetProperty("affected_components", out var affected)) return;
        if (affected.ValueKind != JsonValueKind.Array) throw new FormatException("Invalid affected components");
        foreach (var component in affected.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object) throw new FormatException("Invalid affected component");
            AddSeverity(Text(component, "current_status", "unknown"), ref severity, ref unknown);
        }
    }

    private static void AddSeverity(string state, ref int severity, ref bool unknown)
    {
        var rank = state switch
        {
            "operational" => 0,
            "under_maintenance" => 1,
            "degraded_performance" => 2,
            "partial_outage" => 3,
            "full_outage" or "major_outage" => 4,
            _ => -1,
        };
        if (rank < 0) unknown = true;
        else severity = Math.Max(severity, rank);
    }

    private static int IndicatorSeverity(string indicator) => indicator switch
    {
        "maintenance" => 1,
        "minor" => 2,
        "major" => 3,
        "critical" => 4,
        _ => 0,
    };
}
