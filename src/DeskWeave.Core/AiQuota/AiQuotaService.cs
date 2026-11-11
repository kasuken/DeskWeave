using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DeskWeave.Core.AiQuota;

public sealed record CommandResult(int ExitCode, string Output);

/// <summary>Runs a command with arguments passed verbatim; returns null when the executable is not installed.</summary>
public delegate Task<CommandResult?> CommandRunner(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>Where the service looks for local sign-ins. Defaults match the official CLIs on Windows.</summary>
public sealed record QuotaSources(
    string Home,
    Func<string, string?> Environment,
    CommandRunner RunCommand,
    Func<string?> ReadAntigravityKeyring)
{
    public static QuotaSources Default(Func<string?> readAntigravityKeyring) => new(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        System.Environment.GetEnvironmentVariable,
        ProcessRunner.RunAsync,
        readAntigravityKeyring);

    public string ClaudeCredentials => Path.Combine(Environment("CLAUDE_CONFIG_DIR") ?? Path.Combine(Home, ".claude"), ".credentials.json");

    public string CodexAuth => Path.Combine(Environment("CODEX_HOME") ?? Path.Combine(Home, ".codex"), "auth.json");

    public string AntigravityTokenFile => Path.Combine(
        Environment("AGY_DIR") ?? Path.Combine(Home, ".gemini", "antigravity-cli"), "antigravity-oauth-token");
}

/// <summary>
/// Reads subscription quotas for Claude, Codex, GitHub Copilot and Antigravity using the sign-ins of their CLIs.
/// Tokens are read on every refresh, sent only to the provider's own HTTPS host and never logged or cached:
/// the on-disk cache holds only plan names, limits and reset times.
/// </summary>
public sealed class AiQuotaService(
    HttpClient http,
    QuotaSources sources,
    string cacheFile,
    TimeProvider? time = null,
    string? historyFile = null)
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private const string ClaudeUsage = "https://api.anthropic.com/api/oauth/usage";
    private const string CodexUsage = "https://chatgpt.com/backend-api/wham/usage";
    private const string AntigravityAssist = "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string AntigravitySummary = "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";
    private const int MaxBytes = 1_000_000;

    private static readonly HashSet<string> AllowedHosts =
        new(["api.anthropic.com", "chatgpt.com", "daily-cloudcode-pa.googleapis.com"], StringComparer.OrdinalIgnoreCase);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly string _historyFile = historyFile
        ?? Path.Combine(Path.GetDirectoryName(cacheFile) ?? string.Empty, "ai-quota-history.json");
    private readonly QuotaForecastEngine _forecastEngine = new(time);
    private IReadOnlyList<ProviderQuota> _current = [];
    private bool _historyLoadFailed;

    public IReadOnlyDictionary<QuotaLimitKey, QuotaForecast> Forecasts =>
        _forecastEngine.Forecasts(_current, _time.GetUtcNow());

    public string? HistoryWarning { get; private set; }

    public IReadOnlyList<ProviderQuota> LoadCached()
    {
        HistoryWarning = _forecastEngine.Load(_historyFile);
        _historyLoadFailed = HistoryWarning is not null;
        _current = QuotaCache.Read(cacheFile, _time.GetUtcNow());
        return _current;
    }

    public async Task<IReadOnlyList<ProviderQuota>> RefreshAsync(CancellationToken cancellationToken)
    {
        var outcomes = await Task.WhenAll(
            Guard(QuotaProviders.Claude, FetchClaudeAsync, cancellationToken),
            Guard(QuotaProviders.Codex, FetchCodexAsync, cancellationToken),
            Guard(QuotaProviders.Copilot, FetchCopilotAsync, cancellationToken),
            Guard(QuotaProviders.Antigravity, FetchAntigravityAsync, cancellationToken));

        var now = _time.GetUtcNow();
        foreach (var outcome in outcomes)
        {
            if (outcome.Kind == OutcomeKind.Active && outcome.Quota is { } fresh)
            {
                _forecastEngine.Observe(fresh, now);
            }
            else
            {
                _forecastEngine.ResetProvider(outcome.Id);
            }
        }

        var saveWarning = _forecastEngine.Save(_historyFile);
        if (saveWarning is not null)
        {
            HistoryWarning = saveWarning;
        }
        else if (!_historyLoadFailed)
        {
            HistoryWarning = null;
        }

        _current = QuotaReconciler.Reconcile(_current, outcomes, now);
        QuotaCache.Write(cacheFile, _current);
        return _current;
    }

    private static async Task<ProviderOutcome> Guard(
        string id, Func<CancellationToken, Task<ProviderOutcome>> fetch, CancellationToken cancellationToken)
    {
        try
        {
            return await fetch(cancellationToken);
        }
        catch (QuotaAuthException)
        {
            return ProviderOutcome.Auth(id);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or FormatException
                                       or UnauthorizedAccessException or InvalidOperationException or Win32Exception
                                       or ArgumentOutOfRangeException or OverflowException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return ProviderOutcome.Error(id);
        }
    }

    private async Task<ProviderOutcome> FetchClaudeAsync(CancellationToken cancellationToken)
    {
        var id = QuotaProviders.Claude;
        if (ReadFile(sources.ClaudeCredentials) is not { } json
            || QuotaParsers.ParseClaudeCredential(json) is not { Plan: { } plan } credential)
        {
            return ProviderOutcome.Absent(id);
        }

        if (credential.ExpiresAt is { } expires && expires <= _time.GetUtcNow())
        {
            return ProviderOutcome.Auth(id, plan);
        }

        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ClaudeUsage);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            body = await SendAsync(request, cancellationToken);
        }
        catch (QuotaAuthException)
        {
            return ProviderOutcome.Auth(id, plan);
        }

        var limits = QuotaParsers.ParseClaudeUsage(body);
        return Active(id, plan, limits, limits.Count == 0 ? "Quota unavailable" : null);
    }

    private async Task<ProviderOutcome> FetchCodexAsync(CancellationToken cancellationToken)
    {
        var id = QuotaProviders.Codex;
        if (ReadFile(sources.CodexAuth) is not { } json || QuotaParsers.ParseCodexCredential(json) is not { } credential)
        {
            return ProviderOutcome.Absent(id);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, CodexUsage);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        if (credential.AccountId is { } account)
        {
            request.Headers.Add("ChatGPT-Account-Id", account);
        }

        var parsed = QuotaParsers.ParseCodexUsage(await SendAsync(request, cancellationToken));
        return parsed is { NoAccess: false, Plan: { } plan }
            ? Active(id, plan, parsed.Limits, parsed.Limits.Count == 0 ? "Quota unavailable" : null)
            : ProviderOutcome.Absent(id);
    }

    private async Task<ProviderOutcome> FetchCopilotAsync(CancellationToken cancellationToken)
    {
        // gh keeps the GitHub token in its own credential store; DeskWeave only sees the response.
        var id = QuotaProviders.Copilot;
        var result = await sources.RunCommand("gh", ["api", "--hostname", "github.com", "/copilot_internal/user"], cancellationToken);
        if (result is null)
        {
            return ProviderOutcome.Absent(id);
        }

        if (result.ExitCode != 0)
        {
            return ProviderOutcome.Error(id);
        }

        var parsed = QuotaParsers.ParseCopilotUser(result.Output);
        return parsed is { NoAccess: false, Plan: { } plan }
            ? Active(id, plan, parsed.Limits, parsed.Status)
            : ProviderOutcome.Absent(id);
    }

    private async Task<ProviderOutcome> FetchAntigravityAsync(CancellationToken cancellationToken)
    {
        var id = QuotaProviders.Antigravity;
        var secret = sources.ReadAntigravityKeyring() ?? ReadFile(sources.AntigravityTokenFile);
        if (secret is null || QuotaParsers.ParseAntigravityCredential(secret) is not { } credential)
        {
            return ProviderOutcome.Absent(id);
        }

        if (credential.ExpiresAt is { } expires && expires <= _time.GetUtcNow())
        {
            return ProviderOutcome.Auth(id);
        }

        var (project, rawPlan) = QuotaParsers.ParseAntigravityAssist(
            await PostAntigravityAsync(AntigravityAssist, "{}", credential.AccessToken, cancellationToken));
        if (QuotaParsers.PlanLabel(id, rawPlan) is not { } plan)
        {
            return ProviderOutcome.Absent(id);
        }

        if (project is null)
        {
            return Active(id, plan, [], "Quota unavailable");
        }

        var limits = QuotaParsers.ParseAntigravitySummary(await PostAntigravityAsync(
            AntigravitySummary, JsonSerializer.Serialize(new { project }), credential.AccessToken, cancellationToken));
        return Active(id, plan, limits, limits.Count == 0 ? "Quota unavailable" : null);
    }

    private Task<string> PostAntigravityAsync(string url, string json, string token, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd("antigravity");
        return SendAndDisposeAsync(request, cancellationToken);
    }

    private async Task<string> SendAndDisposeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            return await SendAsync(request, cancellationToken);
        }
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { Scheme: "https" } uri || !AllowedHosts.Contains(uri.Host))
        {
            throw new InvalidOperationException("Quota request target is not allowed");
        }

        request.Headers.Accept.ParseAdd("application/json");
        if (request.Headers.UserAgent.Count == 0)
        {
            request.Headers.UserAgent.ParseAdd("DeskWeave-AI-Quota/1.0");
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new QuotaAuthException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Quota request failed with {(int)response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
            {
                throw new FormatException("Oversized quota response");
            }
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private ProviderOutcome Active(string id, string plan, IReadOnlyList<QuotaLimit> limits, string? status) =>
        ProviderOutcome.Active(new ProviderQuota(id, QuotaProviders.Name(id), plan, limits, status, _time.GetUtcNow()));

    private static string? ReadFile(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        using (stream)
        {
            if (stream.Length > MaxBytes)
            {
                throw new FormatException("Oversized credential file");
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    throw new FormatException("Oversized credential file");
                }

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('\uFEFF');
        }
    }

    private sealed class QuotaAuthException : Exception;
}

/// <summary>Display-only cache so the widget has content immediately after a restart.</summary>
public static class QuotaCache
{
    private const int MaxBytes = 1_000_000;

    public static IReadOnlyList<ProviderQuota> Read(string file, DateTimeOffset now)
    {
        try
        {
            if (ReadBounded(file) is { } json
                && JsonSerializer.Deserialize<List<ProviderQuota>>(json) is { } cached)
            {
                return cached
                    .Where(q => q is { Id: not null, Plan: not null, Limits: not null } && QuotaProviders.Order.Contains(q.Id)
                                && QuotaReconciler.IsFresh(q, now))
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        return [];
    }

    public static void Write(string file, IReadOnlyList<ProviderQuota> quotas)
    {
        try
        {
            if (quotas.Count == 0)
            {
                File.Delete(file);
                return;
            }

            if (Path.GetDirectoryName(file) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = file + ".tmp";
            var json = JsonSerializer.SerializeToUtf8Bytes(quotas);
            if (json.Length > MaxBytes)
            {
                return;
            }

            File.WriteAllBytes(temporary, json);
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? ReadBounded(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > MaxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('\uFEFF');
    }
}

/// <summary>Starts a process without a shell or window and captures stdout, with a hard timeout.</summary>
public static class ProcessRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private const int MaxOutputCharacters = 1_000_000;
    private const int MaxErrorCharacters = 32_000;

    public static async Task<CommandResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["GH_PROMPT_DISABLED"] = "1";
        info.Environment["NO_COLOR"] = "1";

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new IOException("Process did not start");
        }
        catch (Win32Exception)
        {
            return null;
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var output = ReadBoundedAsync(process.StandardOutput, MaxOutputCharacters, timeout.Token);
            var error = ReadBoundedAsync(process.StandardError, MaxErrorCharacters, timeout.Token);
            var wait = process.WaitForExitAsync(timeout.Token);
            try
            {
                var outputComplete = false;
                var errorComplete = false;
                while (!wait.IsCompleted)
                {
                    var pending = new List<Task> { wait };
                    if (!outputComplete) pending.Add(output);
                    if (!errorComplete) pending.Add(error);
                    var completed = await Task.WhenAny(pending);
                    if (completed == wait)
                    {
                        break;
                    }

                    await completed;
                    outputComplete |= completed == output;
                    errorComplete |= completed == error;
                }

                await Task.WhenAll(wait, output, error);
                return new CommandResult(process.ExitCode, await output);
            }
            catch (OperationCanceledException)
            {
                Kill(process);

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new IOException("Command timed out");
            }
            catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or UnauthorizedAccessException)
            {
                Kill(process);
                throw;
            }
            finally
            {
                ObserveCompletion(output);
                ObserveCompletion(error);
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder(Math.Min(maximumCharacters, 16 * 1024));
        var buffer = new char[8 * 1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (text.Length + read > maximumCharacters)
            {
                throw new FormatException("Command output exceeded the size limit");
            }

            text.Append(buffer, 0, read);
        }

        return text.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void ObserveCompletion(Task<string> task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
