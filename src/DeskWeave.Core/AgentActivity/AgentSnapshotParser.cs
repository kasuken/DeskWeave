using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace DeskWeave.Core.AgentActivity;

internal sealed record AgentWireTask(string Id, string? Label, string WorkspaceAlias, AgentTaskState State,
    DateTimeOffset StartedAt, DateTimeOffset UpdatedAt);

internal sealed record AgentWireSnapshot(string AdapterId, string SessionId, long Sequence,
    DateTimeOffset ObservedAt, bool Connected, AgentWireTask? Task);

internal static class AgentSnapshotParser
{
    internal static bool TryParse(ReadOnlyMemory<byte> bytes, DateTimeOffset now, out AgentWireSnapshot? snapshot)
    {
        snapshot = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (!Properties(root, ["schemaVersion", "adapterId", "sessionId", "sequence", "observedAt", "connected", "task"]) ||
                !root.GetProperty("schemaVersion").TryGetInt32(out int version) || version != 1 ||
                !String(root, "adapterId", out string? adapter) || adapter != "claude-code" ||
                !String(root, "sessionId", out string? session) || !AgentActivityValidation.SessionId(session) ||
                !root.GetProperty("sequence").TryGetInt64(out long sequence) || sequence <= 0 ||
                !Timestamp(root, "observedAt", out var observed) || observed > now ||
                root.GetProperty("connected").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return false;

            var taskElement = root.GetProperty("task");
            AgentWireTask? task = null;
            if (taskElement.ValueKind != JsonValueKind.Null)
            {
                if (!Properties(taskElement, ["id", "workspaceAlias", "state", "startedAt", "updatedAt"], ["label"]) ||
                    !String(taskElement, "id", out string? id) || !AgentActivityValidation.TaskId(id) ||
                    !String(taskElement, "workspaceAlias", out string? alias) || !AgentActivityValidation.DisplayText(alias, 48) ||
                    !String(taskElement, "state", out string? stateText) || !TryState(stateText, out var state) ||
                    !Timestamp(taskElement, "startedAt", out var started) ||
                    !Timestamp(taskElement, "updatedAt", out var updated) ||
                    started > updated || updated > observed)
                    return false;

                string? label = null;
                if (taskElement.TryGetProperty("label", out var labelElement))
                {
                    if (labelElement.ValueKind != JsonValueKind.String ||
                        !AgentActivityValidation.DisplayText(label = labelElement.GetString(), 80))
                        return false;
                }

                task = new(id, label, alias, state, started, updated);
            }

            snapshot = new(adapter, session, sequence, observed, root.GetProperty("connected").GetBoolean(), task);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    internal static bool Properties(JsonElement element, string[] required, string[]? optional = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name) ||
                (!required.Contains(property.Name, StringComparer.Ordinal) &&
                 !(optional?.Contains(property.Name, StringComparer.Ordinal) ?? false)))
                return false;
        }
        return required.All(seen.Contains);
    }

    private static bool String(JsonElement element, string name, [NotNullWhen(true)] out string? value)
    {
        var property = element.GetProperty(name);
        value = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return value is not null;
    }

    private static bool Timestamp(JsonElement element, string name, out DateTimeOffset value)
    {
        var property = element.GetProperty(name);
        value = default;
        return property.ValueKind == JsonValueKind.String &&
            property.TryGetDateTimeOffset(out value) && value.Offset == TimeSpan.Zero &&
            (property.GetString()?.EndsWith("Z", StringComparison.Ordinal) == true ||
             property.GetString()?.EndsWith("+00:00", StringComparison.Ordinal) == true);
    }

    private static bool TryState(string? value, out AgentTaskState state)
    {
        state = value switch
        {
            "running" => AgentTaskState.Running,
            "needsApproval" => AgentTaskState.NeedsApproval,
            "needsInput" => AgentTaskState.NeedsInput,
            "blocked" => AgentTaskState.Blocked,
            "completed" => AgentTaskState.Completed,
            "failed" => AgentTaskState.Failed,
            _ => (AgentTaskState)(-1)
        };
        return (int)state >= 0;
    }
}
