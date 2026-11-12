using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DeskWeave.Core.AgentActivity;
using DeskWeave.Desktop;

namespace DeskWeave.Tests;

public sealed class ClaudeHookAdapterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"DeskWeave hook test {Guid.NewGuid():N}");

    [Fact]
    public async Task Lifecycle_PublishesSafeMetadataAndPreservesTerminalTaskUntilNewPrompt()
    {
        var reader = new AgentActivityService(_directory);
        await reader.ReadAsync(["claude-code"], CancellationToken.None);
        await Hook("SessionStart");
        Assert.Equal(JsonValueKind.Null, Snapshot().GetProperty("task").ValueKind);
        await Hook("UserPromptSubmit");
        var firstTask = Snapshot().GetProperty("task");
        var started = firstTask.GetProperty("startedAt").GetString();
        var id = firstTask.GetProperty("id").GetString();
        var activity = await reader.ReadAsync(["claude-code"], CancellationToken.None);
        Assert.Equal(AgentTaskState.Running, Assert.Single(activity.Tasks).State);
        foreach (var (hook, state) in new[] {
            ("PermissionRequest", "needsApproval"), ("Elicitation", "needsInput"),
            ("PostToolUse", "running"), ("Stop", "completed"),
        })
        {
            await Hook(hook);
            var task = Snapshot().GetProperty("task");
            Assert.Equal(state, task.GetProperty("state").GetString());
            Assert.Equal(started, task.GetProperty("startedAt").GetString());
            Assert.Equal(id, task.GetProperty("id").GetString());
            activity = await reader.ReadAsync(["claude-code"], CancellationToken.None);
            Assert.Equal(1, activity.TotalCount);
            Assert.Equal(hook is "PermissionRequest" or "Elicitation" ? 1 : 0, activity.WaitingCount);
        }
        var sequence = Snapshot().GetProperty("sequence").GetInt64();
        await Hook("PostToolUse");
        await Hook("UserPromptSubmit");
        Assert.Equal(sequence, Snapshot().GetProperty("sequence").GetInt64());
        await Hook("UserPromptSubmit", turn: "new-turn");
        Assert.NotEqual(id, Snapshot().GetProperty("task").GetProperty("id").GetString());
        await Hook("StopFailure", turn: "new-turn");
        Assert.Equal("failed", Snapshot().GetProperty("task").GetProperty("state").GetString());
        activity = await reader.ReadAsync(["claude-code"], CancellationToken.None);
        Assert.Contains(activity.Tasks, task => task.State == AgentTaskState.Failed);
        await Hook("SessionEnd");
        Assert.False(Snapshot().GetProperty("connected").GetBoolean());
        Assert.DoesNotContain("private-marker", File.ReadAllText(Directory.GetFiles(_directory, "*.json").Single()));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentSessionsAndMalformedPayloads_AreIsolated()
    {
        await System.Threading.Tasks.Task.WhenAll(Hook("UserPromptSubmit", session: "a"), Hook("UserPromptSubmit", session: "b"));
        Assert.Equal(2, Directory.GetFiles(_directory, "*.json").Length);
        var before = Directory.GetFiles(_directory, "*.json").Select(File.ReadAllText).Order().ToArray();
        var result = await Invoke("{ invalid private-marker }");
        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("private-marker", result.Error);
        Assert.Equal(before, Directory.GetFiles(_directory, "*.json").Select(File.ReadAllText).Order().ToArray());
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("\\\\server\\share")]
    [InlineData("C:\\Windows /select")]
    public void WorkspaceNavigation_RejectsNonlocalOrNonDirectoryDestinations(string destination) =>
        Assert.Throws<ArgumentException>(() => WorkspaceLauncher.Validate(destination));

    private async Task Hook(string hook, string session = "a", string turn = "turn")
    {
        var payload = JsonSerializer.Serialize(new
        {
            session_id = session,
            prompt_id = $"{session}-{turn}",
            hook_event_name = hook,
            tool_name = "Bash",
            prompt = "private-marker",
            transcript_path = "private-marker",
            tool_input = new { command = "private-marker" },
            last_assistant_message = "private-marker",
        });
        var result = await Invoke(payload);
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal(string.Empty, result.Output);
    }

    private async Task<(int ExitCode, string Output, string Error)> Invoke(string payload)
    {
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(AppContext.BaseDirectory, "integrations", "claude-code", "Publish-DeskWeaveActivity.ps1"),
            "-WorkspaceAlias", "Example", "-OutputDirectory", _directory })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Hook process did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync(payload);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private JsonElement Snapshot()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(_directory, "*.json").Single()));
        return json.RootElement.Clone();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
