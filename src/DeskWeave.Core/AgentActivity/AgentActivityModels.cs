namespace DeskWeave.Core.AgentActivity;

/// <summary>The last reported state of an agent task.</summary>
public enum AgentTaskState { Running, NeedsApproval, NeedsInput, Blocked, Completed, Failed }

/// <summary>Whether the task's session has recently reported a usable snapshot.</summary>
public enum AgentFreshness { Fresh, Unknown }

/// <summary>A privacy-filtered task, independent of any workspace path.</summary>
public sealed record AgentTaskView(
    string AdapterId, string SessionId, string TaskId, string WorkspaceAlias, string? TaskLabel,
    AgentTaskState State, AgentFreshness Freshness, DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt, TimeSpan Elapsed);

/// <summary>The current status and any sanitized warning for an integration.</summary>
public sealed record AgentSourceView(string Id, string Name, string Status, string? Warning);

/// <summary>A bounded, ordered view of agent activity.</summary>
public sealed record AgentActivitySnapshot(IReadOnlyList<AgentTaskView> Tasks, IReadOnlyList<AgentSourceView> Sources)
{
    /// <summary>An empty activity view.</summary>
    public static AgentActivitySnapshot Empty { get; } = new([], []);

    /// <summary>Fresh tasks requiring approval or input.</summary>
    public int WaitingCount => Tasks.Count(t => t.Freshness == AgentFreshness.Fresh &&
        t.State is AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput);

    /// <summary>Fresh running tasks.</summary>
    public int RunningCount => Tasks.Count(t => t.Freshness == AgentFreshness.Fresh && t.State == AgentTaskState.Running);

    /// <summary>All retained tasks, including unknown and terminal tasks.</summary>
    public int TotalCount => Tasks.Count;
}
