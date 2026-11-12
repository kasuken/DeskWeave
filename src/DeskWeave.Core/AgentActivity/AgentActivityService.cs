using System.Security.Cryptography;
using System.Text;

namespace DeskWeave.Core.AgentActivity;

/// <summary>Reads bounded per-session snapshots and maintains an in-memory activity view.</summary>
public sealed class AgentActivityService
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetainTerminalFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetainSessionFor = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(15);
    private const int MaxTerminalIdsPerSession = 4096;
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly List<HistoricalTask> _history = [];
    private readonly Dictionary<(string SessionId, string TaskId), TaskIdentity> _identities = [];
    private DateTimeOffset? _enabledAt;
    private string? _lastAttemptedFile;
    private long _identityOrder;

    /// <summary>
    /// Creates a reader. Producers must use the canonical filename
    /// <c>adapterId-sessionId.json</c>, with a lowercase 64-hex session hash.
    /// </summary>
    public AgentActivityService(string snapshotsDirectory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotsDirectory);
        _directory = Path.GetFullPath(snapshotsDirectory);
        if (_directory.StartsWith(@"\\", StringComparison.Ordinal) || _directory.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Snapshots must be stored locally.", nameof(snapshotsDirectory));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Reads enabled integrations. Disabled integrations are not read and their cached state is discarded.
    /// All returned warnings are sanitized and never contain snapshot contents or paths.
    /// File scanning has a cooperative 15-second deadline and resumes after the last attempted file.
    /// </summary>
    public async Task<AgentActivitySnapshot> ReadAsync(
        IReadOnlyCollection<string> enabledIntegrations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enabledIntegrations);
        if (enabledIntegrations.Count > AgentActivityValidation.MaxSettingsItems ||
            enabledIntegrations.Any(id => !AgentActivityValidation.AdapterId(id)) ||
            enabledIntegrations.Distinct(StringComparer.Ordinal).Count() != enabledIntegrations.Count)
            throw new ArgumentException("Invalid integration identifiers.", nameof(enabledIntegrations));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _time.GetUtcNow();
            bool enabled = enabledIntegrations.Contains("claude-code", StringComparer.Ordinal);
            if (!enabled)
            {
                _enabledAt = null;
                _sessions.Clear();
                _history.Clear();
                _identities.Clear();
                _lastAttemptedFile = null;
            }
            else
            {
                _enabledAt ??= now;
            }

            if (enabledIntegrations.Count == 0)
                return AgentActivitySnapshot.Empty;

            bool warning = false;
            foreach (string key in _sessions.Where(p => now - p.Value.Snapshot.ObservedAt >= RetainSessionFor)
                         .Select(p => p.Key).ToArray())
            {
                _sessions.Remove(key);
                foreach (var identity in _identities.Keys.Where(k => k.SessionId == key).ToArray())
                    _identities.Remove(identity);
            }
            foreach (var session in _sessions.Values)
                session.Seen = false;
            if (enabled)
                warning = await ReadFilesAsync(now, cancellationToken).ConfigureAwait(false);

            _history.RemoveAll(t => now - t.Task.UpdatedAt >= RetainTerminalFor);
            TrimHistory();

            var tasks = new List<AgentTaskView>();
            foreach (var session in _sessions.Values)
            {
                var snapshot = session.Snapshot;
                if (snapshot.Task is not { } task ||
                    (AgentActivityValidation.IsTerminal(task.State) && now - task.UpdatedAt >= RetainTerminalFor))
                    continue;
                bool fresh = session.Seen && snapshot.Connected && now - snapshot.ObservedAt <= FreshFor &&
                    (AgentActivityValidation.IsTerminal(task.State) || snapshot.ObservedAt > _enabledAt);
                tasks.Add(View(snapshot, task, fresh, now));
            }
            foreach (var historical in _history)
            {
                bool fresh = historical.Snapshot.Connected && historical.Snapshot.ObservedAt <= now &&
                    now - historical.Snapshot.ObservedAt <= FreshFor;
                tasks.Add(View(historical.Snapshot, historical.Task, fresh, now));
            }

            tasks.Sort(CompareTasks);
            var sources = new List<AgentSourceView>();
            foreach (string id in enabledIntegrations.Order(StringComparer.Ordinal))
            {
                if (id != "claude-code")
                {
                    sources.Add(new(id, id, "Unsupported", null));
                    continue;
                }
                var active = _sessions.Values.Where(s => s.Seen && now - s.Snapshot.ObservedAt <= FreshFor &&
                    s.Snapshot.ObservedAt > _enabledAt).ToArray();
                string status = active.Any(s => s.Snapshot.Connected) ? "Connected" :
                    active.Length > 0 ? "Disconnected" : "Unknown";
                sources.Add(new(id, "Claude Code", status,
                    warning ? "Some agent snapshots could not be accepted." : null));
            }
            return new(tasks.AsReadOnly(), sources.AsReadOnly());
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ReadFilesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        bool warning = false;
        long started = _time.GetTimestamp();
        using var deadline = new CancellationTokenSource(ScanTimeout, _time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            if (!AgentActivityValidation.HasNoReparseDirectories(_directory))
                return true;
            // Stop enumerating after the limit, including invalid filenames.
            var files = Directory.EnumerateFiles(_directory, "*.json").Take(AgentActivityValidation.MaxItems + 1).ToArray();
            if (files.Length > AgentActivityValidation.MaxItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }
            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length == 0)
            {
                _lastAttemptedFile = null;
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
            int start = _lastAttemptedFile is null ? 0 :
                Array.FindIndex(files, file => string.CompareOrdinal(file, _lastAttemptedFile) > 0);
            if (start < 0)
                start = 0;
            byte[] buffer = new byte[AgentActivityValidation.MaxBytes + 1];
            for (int offset = 0; offset < files.Length; offset++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_time.GetElapsedTime(started, _time.GetTimestamp()) >= ScanTimeout)
                {
                    warning = true;
                    break;
                }
                string file = files[(start + offset) % files.Length];
                _lastAttemptedFile = file;
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        warning = true;
                        continue;
                    }
                    await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    int count = 0;
                    while (count < buffer.Length)
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        count += read;
                    }
                    if (count > AgentActivityValidation.MaxBytes ||
                        !AgentSnapshotParser.TryParse(buffer.AsMemory(0, count), now, out var snapshot) ||
                        snapshot is null ||
                        !string.Equals(Path.GetFileName(file), $"{snapshot.AdapterId}-{snapshot.SessionId}.json", StringComparison.Ordinal) ||
                        !Accept(snapshot, now))
                        warning = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    warning = true;
                }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { warning = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            warning = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return warning;
    }

    private bool Accept(AgentWireSnapshot incoming, DateTimeOffset now)
    {
        if (now - incoming.ObservedAt >= RetainSessionFor)
            return true;
        if (!_sessions.TryGetValue(incoming.SessionId, out var session))
        {
            if (_sessions.Count >= AgentActivityValidation.MaxItems)
                return false;
            session = new Session(incoming);
            _sessions.Add(incoming.SessionId, session);
            session.RememberTerminal(incoming.Task);
            RememberTask(incoming);
            TrimHistory();
            return true;
        }
        var previous = session.Snapshot;
        if (incoming.Sequence == previous.Sequence)
        {
            if (incoming != previous) return false;
            session.Seen = true;
            return true;
        }
        if (incoming.Sequence < previous.Sequence || incoming.ObservedAt < previous.ObservedAt)
            return false;

        if (incoming.Task is { } next)
        {
            if (previous.Task?.Id != next.Id && session.HasTerminal(next.Id))
                return false;
            if (_identities.TryGetValue((incoming.SessionId, next.Id), out var identity) &&
                (next.StartedAt != identity.Task.StartedAt || next.UpdatedAt < identity.Task.UpdatedAt ||
                 (AgentActivityValidation.IsTerminal(identity.Task.State) && next != identity.Task)))
                return false;
            if (previous.Task?.Id != next.Id &&
                _history.Any(h => h.Snapshot.SessionId == incoming.SessionId && h.Task.Id == next.Id))
                return false;
        }
        if (previous.Task is { } oldTask && AgentActivityValidation.IsTerminal(oldTask.State) &&
            incoming.Task?.Id != oldTask.Id && now - oldTask.UpdatedAt < RetainTerminalFor)
            _history.Add(new(previous, oldTask));
        session.Snapshot = incoming;
        session.Seen = true;
        session.RememberTerminal(incoming.Task);
        RememberTask(incoming);
        TrimHistory();
        return true;
    }

    private void RememberTask(AgentWireSnapshot snapshot)
    {
        if (snapshot.Task is not { } task)
            return;
        var key = (snapshot.SessionId, task.Id);
        _identities[key] = new(task, _identityOrder++);
        if (_identities.Count <= AgentActivityValidation.MaxItems)
            return;

        var oldest = _identities
            .Where(pair => !_sessions.TryGetValue(pair.Key.SessionId, out var session) ||
                session.Snapshot.Task?.Id != pair.Key.TaskId)
            .MinBy(pair => pair.Value.Order);
        _identities.Remove(oldest.Key);
    }

    private void TrimHistory()
    {
        int currentTasks = _sessions.Values.Count(s => s.Snapshot.Task is not null);
        int excess = currentTasks + _history.Count - AgentActivityValidation.MaxItems;
        if (excess > 0)
        {
            _history.Sort((a, b) => a.Task.UpdatedAt.CompareTo(b.Task.UpdatedAt));
            _history.RemoveRange(0, excess);
        }
    }

    private static AgentTaskView View(AgentWireSnapshot snapshot, AgentWireTask task, bool fresh, DateTimeOffset now)
    {
        var end = AgentActivityValidation.IsTerminal(task.State) || !fresh ? task.UpdatedAt : now;
        return new(snapshot.AdapterId, snapshot.SessionId, task.Id, task.WorkspaceAlias, task.Label,
            task.State, fresh ? AgentFreshness.Fresh : AgentFreshness.Unknown,
            task.StartedAt, task.UpdatedAt, end - task.StartedAt);
    }

    private static int CompareTasks(AgentTaskView a, AgentTaskView b)
    {
        static int Rank(AgentTaskView task) => task.Freshness == AgentFreshness.Unknown ? 4 : task.State switch
        {
            AgentTaskState.NeedsApproval or AgentTaskState.NeedsInput => 0,
            AgentTaskState.Failed or AgentTaskState.Blocked => 1,
            AgentTaskState.Running => 2,
            _ => 3
        };
        int result = Rank(a).CompareTo(Rank(b));
        if (result != 0) return result;
        result = b.UpdatedAt.CompareTo(a.UpdatedAt);
        if (result != 0) return result;
        result = string.CompareOrdinal(a.SessionId, b.SessionId);
        return result != 0 ? result : string.CompareOrdinal(a.TaskId, b.TaskId);
    }

    private sealed class Session(AgentWireSnapshot snapshot)
    {
        private readonly HashSet<Guid> _terminalIds = [];
        private bool _terminalHistoryFull;

        public AgentWireSnapshot Snapshot { get; set; } = snapshot;
        public bool Seen { get; set; } = true;

        public bool HasTerminal(string taskId) =>
            _terminalHistoryFull || _terminalIds.Contains(HashTaskId(taskId));

        public void RememberTerminal(AgentWireTask? task)
        {
            if (task is null || !AgentActivityValidation.IsTerminal(task.State))
                return;
            Guid hash = HashTaskId(task.Id);
            if (_terminalIds.Contains(hash))
                return;
            _terminalIds.Add(hash);
            if (_terminalIds.Count == MaxTerminalIdsPerSession)
                _terminalHistoryFull = true;
        }

        private static Guid HashTaskId(string taskId) =>
            new(SHA256.HashData(Encoding.UTF8.GetBytes(taskId)).AsSpan(0, 16));
    }

    private sealed record HistoricalTask(AgentWireSnapshot Snapshot, AgentWireTask Task);
    private sealed record TaskIdentity(AgentWireTask Task, long Order);
}
