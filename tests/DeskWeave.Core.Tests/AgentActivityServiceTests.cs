using System.Text.Json.Nodes;
using DeskWeave.Core.AgentActivity;

namespace DeskWeave.Core.Tests;

public sealed class AgentActivityServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DeskWeaveAgentTests", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly string _session = new('a', 64);
    private static readonly string[] Enabled = ["claude-code"];

    [Fact]
    public async Task Cached_live_task_is_unknown_until_a_new_snapshot_after_enable()
    {
        Write(_session, 1, "running");
        var service = Service();

        var cached = await Read(service);
        Assert.Equal(AgentFreshness.Unknown, Assert.Single(cached.Tasks).Freshness);
        Assert.Equal(0, cached.RunningCount);
        Assert.Equal("Unknown", Assert.Single(cached.Sources).Status);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(_session, 2, "running");
        var live = await Read(service);
        Assert.Equal(AgentFreshness.Fresh, Assert.Single(live.Tasks).Freshness);
        Assert.Equal(1, live.RunningCount);
        Assert.Equal("Connected", Assert.Single(live.Sources).Status);
    }

    [Fact]
    public async Task Repeated_snapshot_expires_without_a_heartbeat()
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(_session, 1, "running");
        Assert.Equal(1, (await Read(service)).RunningCount);

        _clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromTicks(1));
        var expired = await Read(service);
        Assert.Equal(AgentFreshness.Unknown, Assert.Single(expired.Tasks).Freshness);
        Assert.Equal(0, expired.RunningCount);
        Assert.Equal(TimeSpan.FromSeconds(1), expired.Tasks[0].Elapsed);
        Assert.Equal("Unknown", Assert.Single(expired.Sources).Status);
    }

    [Fact]
    public async Task Disable_and_reenable_clears_live_state_and_requires_new_snapshot()
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(_session, 1, "needsApproval");
        Assert.Equal(1, (await Read(service)).WaitingCount);
        Assert.Empty((await service.ReadAsync([], CancellationToken.None)).Tasks);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(AgentFreshness.Unknown, Assert.Single((await Read(service)).Tasks).Freshness);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(_session, 2, "needsInput");
        Assert.Equal(1, (await Read(service)).WaitingCount);
    }

    [Fact]
    public async Task Disconnected_task_is_unknown_and_counts_exclude_it()
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(_session, 1, "running", connected: false);

        var snapshot = await Read(service);

        Assert.Equal(AgentFreshness.Unknown, Assert.Single(snapshot.Tasks).Freshness);
        Assert.Equal(0, snapshot.RunningCount);
        Assert.Equal("Disconnected", Assert.Single(snapshot.Sources).Status);
    }

    [Fact]
    public async Task Separate_sessions_with_same_alias_are_independent_and_ordered()
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(new string('a', 64), 1, "running");
        Write(new string('b', 64), 1, "needsInput");
        Write(new string('c', 64), 1, "blocked");
        Write(new string('d', 64), 1, "completed");
        Write(new string('e', 64), 1, "running", connected: false);

        var snapshot = await Read(service);

        Assert.Equal([AgentTaskState.NeedsInput, AgentTaskState.Blocked, AgentTaskState.Running,
            AgentTaskState.Completed, AgentTaskState.Running], snapshot.Tasks.Select(t => t.State));
        Assert.Equal(5, snapshot.TotalCount);
        Assert.Equal(1, snapshot.WaitingCount);
        Assert.Equal(1, snapshot.RunningCount);
        Assert.Equal(5, snapshot.Tasks.Select(t => t.SessionId).Distinct().Count());
    }

    [Fact]
    public async Task Replacing_terminal_task_preserves_it_then_expires_it()
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(_session, 1, "completed", taskId: "first");
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(_session, 2, "running", taskId: "second");
        var replaced = await Read(service);
        Assert.Equal(["second", "first"], replaced.Tasks.Select(t => t.TaskId));

        _clock.Advance(TimeSpan.FromMinutes(10));
        var expired = await Read(service);
        Assert.Equal("second", Assert.Single(expired.Tasks).TaskId);
    }

    [Fact]
    public async Task Retained_terminals_never_exceed_total_task_limit()
    {
        var service = Service();
        await Read(service);
        for (int i = 1; i <= 130; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            Write(_session, i, "completed", taskId: $"task-{i}");
            await Read(service);
        }

        var snapshot = await Read(service);
        Assert.Equal(128, snapshot.TotalCount);
        Assert.DoesNotContain(snapshot.Tasks, task => task.TaskId == "task-1");
        Assert.Contains(snapshot.Tasks, task => task.TaskId == "task-130");
    }

    [Fact]
    public async Task Terminal_task_cannot_be_reopened_after_its_row_and_identity_are_evicted()
    {
        var service = Service();
        await Read(service);
        for (int i = 1; i <= 130; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            Write(_session, i, "completed", taskId: $"task-{i}");
            await Read(service);
        }
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(_session, 131, "running", taskId: "task-1");

        var snapshot = await Read(service);

        Assert.DoesNotContain(snapshot.Tasks, task => task.TaskId == "task-1");
        Assert.NotNull(Assert.Single(snapshot.Sources).Warning);
    }

    [Fact]
    public async Task Inactive_session_is_removed_after_thirty_minutes()
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(_session, 1, "running");
        await Read(service);
        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Empty((await Read(service)).Tasks);
    }

    [Theory]
    [InlineData("out-of-order")]
    [InlineData("duplicate-changed")]
    [InlineData("revert-terminal")]
    [InlineData("change-start")]
    public async Task Invalid_state_transitions_never_replace_accepted_state(string kind)
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(_session, 2, kind == "revert-terminal" ? "completed" : "running");
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var invalid = Make(_session, kind == "out-of-order" ? 1 : kind == "duplicate-changed" ? 2 : 3, "needsInput");
        if (kind == "change-start")
            invalid["task"]!["startedAt"] = _clock.GetUtcNow().AddSeconds(-1);
        Save(_session, invalid);

        var result = await Read(service);

        Assert.NotEqual(AgentTaskState.NeedsInput, Assert.Single(result.Tasks).State);
        Assert.Equal(AgentFreshness.Unknown, result.Tasks[0].Freshness);
        Assert.NotNull(Assert.Single(result.Sources).Warning);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("completed")]
    public async Task Clearing_task_does_not_allow_same_id_to_change_start_or_revert_terminal(string initialState)
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(_session, 1, initialState);
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var clear = Make(_session, 2, "running");
        clear["task"] = null;
        Save(_session, clear);
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var replacement = Make(_session, 3, "running");
        if (initialState == "running")
            replacement["task"]!["startedAt"] = _clock.GetUtcNow();
        Save(_session, replacement);

        var result = await Read(service);

        Assert.DoesNotContain(result.Tasks, task => task.State == AgentTaskState.Running);
        Assert.NotNull(Assert.Single(result.Sources).Warning);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("label")]
    [InlineData("alias")]
    [InlineData("updated")]
    public async Task Terminal_task_is_immutable_when_same_id_is_reported_again(string change)
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(_session, 1, "completed");
        var original = Assert.Single((await Read(service)).Tasks);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var next = Make(_session, 2, "completed");
        next["task"]!["updatedAt"] = original.UpdatedAt;
        switch (change)
        {
            case "state": next["task"]!["state"] = "failed"; break;
            case "label": next["task"]!["label"] = "Changed"; break;
            case "alias": next["task"]!["workspaceAlias"] = "Changed"; break;
            case "updated": next["task"]!["updatedAt"] = _clock.GetUtcNow(); break;
        }
        Save(_session, next);

        var result = await Read(service);

        var retained = Assert.Single(result.Tasks);
        Assert.Equal(original.State, retained.State);
        Assert.Equal(original.TaskLabel, retained.TaskLabel);
        Assert.Equal(original.WorkspaceAlias, retained.WorkspaceAlias);
        Assert.Equal(original.UpdatedAt, retained.UpdatedAt);
        Assert.NotNull(Assert.Single(result.Sources).Warning);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    public async Task Unchanged_terminal_task_can_be_reported_in_a_new_snapshot(string state)
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Write(_session, 1, state);
        var original = Assert.Single((await Read(service)).Tasks);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var next = Make(_session, 2, state);
        next["task"]!["updatedAt"] = original.UpdatedAt;
        Save(_session, next);

        var result = await Read(service);

        Assert.Equal(original.State, Assert.Single(result.Tasks).State);
        Assert.Null(Assert.Single(result.Sources).Warning);
    }

    [Theory]
    [InlineData("unknown-root")]
    [InlineData("unknown-task")]
    [InlineData("duplicate-property")]
    [InlineData("wrong-file")]
    [InlineData("future")]
    [InlineData("bad-time-order")]
    [InlineData("null-label")]
    [InlineData("bad-state")]
    [InlineData("path-label")]
    [InlineData("large")]
    public async Task Invalid_or_private_snapshot_is_rejected_with_sanitized_warning(string kind)
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var data = Make(_session, 1, "running");
        switch (kind)
        {
            case "unknown-root": data["secret"] = "private"; break;
            case "unknown-task": data["task"]!["transcript"] = "private"; break;
            case "future": data["observedAt"] = _clock.GetUtcNow().AddSeconds(1); break;
            case "bad-time-order": data["task"]!["startedAt"] = _clock.GetUtcNow().AddSeconds(1); break;
            case "null-label": data["task"]!["label"] = null; break;
            case "bad-state": data["task"]!["state"] = "waiting"; break;
            case "path-label": data["task"]!["label"] = @"C:\private"; break;
        }
        Directory.CreateDirectory(_folder);
        string path = FileName(kind == "wrong-file" ? new string('b', 64) : _session);
        string text = data.ToJsonString();
        if (kind == "large") text = new string('x', 16385);
        if (kind == "duplicate-property") text = text.Replace("\"sequence\":1", "\"sequence\":1,\"sequence\":1");
        File.WriteAllText(path, text);

        var result = await Read(service);

        Assert.Empty(result.Tasks);
        var warning = Assert.Single(result.Sources).Warning;
        Assert.NotNull(warning);
        Assert.DoesNotContain(_folder, warning);
        Assert.DoesNotContain("private", warning);
    }

    [Fact]
    public async Task Null_task_reports_connected_without_creating_a_task()
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var data = Make(_session, 1, "running");
        data["task"] = null;
        Save(_session, data);

        var result = await Read(service);
        Assert.Empty(result.Tasks);
        Assert.Equal("Connected", Assert.Single(result.Sources).Status);
    }

    [Fact]
    public async Task Unknown_integrations_are_unsupported_and_empty_configuration_never_reads()
    {
        string notADirectory = Path.Combine(_folder, "not-a-directory");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(notADirectory, "content");
        var service = new AgentActivityService(notADirectory, _clock);
        Assert.Empty((await service.ReadAsync([], CancellationToken.None)).Sources);
        var unsupported = await service.ReadAsync(["other-adapter"], CancellationToken.None);
        Assert.Equal("Unsupported", Assert.Single(unsupported.Sources).Status);
        Assert.Null(unsupported.Sources[0].Warning);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_to_warning()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ReadAsync(Enabled, cts.Token));
    }

    [Theory]
    [InlineData(128, 128, false)]
    [InlineData(129, 0, true)]
    public async Task File_and_task_count_are_bounded(int files, int expectedTasks, bool expectWarning)
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        for (int i = 0; i < files; i++)
            Write(i.ToString("x64"), 1, "running");

        var result = await Read(service);

        Assert.Equal(expectedTasks, result.TotalCount);
        Assert.Equal(expectWarning, Assert.Single(result.Sources).Warning is not null);
    }

    [Fact]
    public async Task Deadline_resumes_after_last_attempted_file_without_starving_later_sessions()
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        for (int i = 0; i < 5; i++)
            Write(i.ToString("x64"), 1, "running");
        _clock.TimestampStep = TimeSpan.FromSeconds(5);

        var first = await Read(service);
        var second = await Read(service);
        var third = await Read(service);

        Assert.Equal(2, first.TotalCount);
        Assert.Equal(4, second.TotalCount);
        Assert.Equal(5, third.TotalCount);
        Assert.NotNull(Assert.Single(first.Sources).Warning);
        Assert.NotNull(Assert.Single(second.Sources).Warning);
        Assert.NotNull(Assert.Single(third.Sources).Warning);

        _clock.TimestampStep = TimeSpan.Zero;
        var complete = await Read(service);
        Assert.Equal(5, complete.RunningCount);
        Assert.Null(Assert.Single(complete.Sources).Warning);
    }

    [Fact]
    public async Task Disabling_resets_the_partial_scan_cursor()
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        for (int i = 0; i < 5; i++)
            Write(i.ToString("x64"), 1, "running");
        _clock.TimestampStep = TimeSpan.FromSeconds(5);
        await Read(service);

        await service.ReadAsync([], CancellationToken.None);
        var reenabled = await Read(service);

        Assert.Equal([0.ToString("x64"), 1.ToString("x64")],
            reenabled.Tasks.Select(task => task.SessionId));
        Assert.NotNull(Assert.Single(reenabled.Sources).Warning);
    }

    [Fact]
    public async Task Deadline_cancelling_a_file_read_advances_the_scan_cursor()
    {
        var service = Service();
        await Read(service);
        _clock.Advance(TimeSpan.FromSeconds(1));
        for (int i = 0; i < 3; i++)
            Write(i.ToString("x64"), 1, "running");
        _clock.ManualTimers = true;
        _clock.FireTimerOnTimestampCall = 2;

        var interrupted = await Read(service);

        Assert.Empty(interrupted.Tasks);
        Assert.NotNull(Assert.Single(interrupted.Sources).Warning);
        _clock.TimestampStep = TimeSpan.FromSeconds(8);
        var resumed = await Read(service);
        Assert.Equal(1.ToString("x64"), Assert.Single(resumed.Tasks).SessionId);
        Assert.NotNull(Assert.Single(resumed.Sources).Warning);
    }

    private AgentActivityService Service() => new(_folder, _clock);
    private static Task<AgentActivitySnapshot> Read(AgentActivityService service) => service.ReadAsync(Enabled, CancellationToken.None);
    private string FileName(string session) => Path.Combine(_folder, $"claude-code-{session}.json");
    private void Save(string session, JsonObject data)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FileName(session), data.ToJsonString());
    }
    private void Write(string session, long sequence, string state, bool connected = true, string taskId = "task")
        => Save(session, Make(session, sequence, state, connected, taskId));

    private JsonObject Make(string session, long sequence, string state, bool connected = true, string taskId = "task") => new()
    {
        ["schemaVersion"] = 1,
        ["adapterId"] = "claude-code",
        ["sessionId"] = session,
        ["sequence"] = sequence,
        ["observedAt"] = _clock.GetUtcNow(),
        ["connected"] = connected,
        ["task"] = new JsonObject
        {
            ["id"] = taskId,
            ["workspaceAlias"] = "Workspace",
            ["state"] = state,
            ["startedAt"] = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ["updatedAt"] = _clock.GetUtcNow()
        }
    };

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;
        private ManualTimer? _timer;
        public TimeSpan TimestampStep { get; set; }
        public bool ManualTimers { get; set; }
        public int FireTimerOnTimestampCall { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp()
        {
            if (FireTimerOnTimestampCall > 0 && --FireTimerOnTimestampCall == 0)
                _timer?.Fire();
            long timestamp = _timestamp;
            _timestamp += TimestampStep.Ticks;
            return timestamp;
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (!ManualTimers)
                return base.CreateTimer(callback, state, dueTime, period);
            return _timer = new ManualTimer(callback, state);
        }
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        public void Fire()
        {
            if (!_disposed) callback(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
