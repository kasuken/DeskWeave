using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskWeave.Core.AgentActivity;

namespace DeskWeave.Widgets;

public partial class AgentActivityWidget : UserControl
{
    public const int MaximumVisibleTasks = 5;
    private TaskRow[] _rows = [];
    private SourceRow[] _sources = [];

    public AgentActivityWidget()
    {
        InitializeComponent();
        Update(AgentActivitySnapshot.Empty);
    }

    public bool HasSources { get; private set; }

    public void Update(AgentActivitySnapshot snapshot)
    {
        HasSources = snapshot.Sources.Count > 0;
        Attention.Text = $"{snapshot.WaitingCount} waiting on you";
        Summary.Text = $"{snapshot.RunningCount} running · {snapshot.TotalCount} recent tasks";
        var rows = snapshot.Tasks.Take(MaximumVisibleTasks).Select(task =>
        {
            var unknown = task.Freshness != AgentFreshness.Fresh;
            var state = unknown ? "Unknown" : task.State switch
            {
                AgentTaskState.NeedsApproval => "Approval needed",
                AgentTaskState.NeedsInput => "Input needed",
                AgentTaskState.Blocked => "Blocked",
                AgentTaskState.Failed => "Failed",
                AgentTaskState.Completed => "Turn finished",
                _ => "Running",
            };
            var color = unknown ? "MutedBrush" : task.State switch
            {
                AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput => "YellowBrush",
                AgentTaskState.Blocked or AgentTaskState.Failed => "RedBrush",
                AgentTaskState.Completed => "GreenBrush",
                _ => "AccentBrush",
            };
            var adapter = task.AdapterId == "claude-code" ? "Claude Code" : task.AdapterId;
            var label = string.IsNullOrEmpty(task.TaskLabel) ? "Coding task" : task.TaskLabel;
            var elapsed = task.Elapsed.TotalHours >= 1
                ? $"{(int)task.Elapsed.TotalHours}h {task.Elapsed.Minutes}m"
                : task.Elapsed.TotalMinutes >= 1 ? $"{(int)task.Elapsed.TotalMinutes}m" : "<1m";
            var timing = unknown
                ? $"Last event {task.UpdatedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)} · disconnected"
                : $"Started {task.StartedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)} · {elapsed}";
            return new TaskRow(task.WorkspaceAlias, state, (Brush)FindResource(color),
                $"{adapter} · {label} · {task.SessionId[..Math.Min(6, task.SessionId.Length)]}/{task.TaskId[..Math.Min(6, task.TaskId.Length)]}",
                timing);
        }).ToArray();
        if (!_rows.SequenceEqual(rows))
        {
            _rows = rows;
            Tasks.ItemsSource = rows;
        }
        EmptyMessage.Visibility = snapshot.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var overflow = snapshot.Tasks.Count - MaximumVisibleTasks;
        Overflow.Text = $"{overflow} more tasks · {snapshot.RunningCount} running";
        Overflow.Visibility = overflow > 0 ? Visibility.Visible : Visibility.Collapsed;
        var sources = snapshot.Sources
            .Select(source => new SourceRow($"{source.Name} · {source.Status}", source.Warning ?? string.Empty,
                source.Warning is null ? Visibility.Collapsed : Visibility.Visible)).ToArray();
        if (!_sources.SequenceEqual(sources))
        {
            _sources = sources;
            Sources.ItemsSource = sources;
        }
    }

    public sealed record TaskRow(string Workspace, string State, Brush Tint, string Detail, string Timing);
    public sealed record SourceRow(string Status, string Warning, Visibility WarningVisibility);
}
