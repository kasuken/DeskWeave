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
public sealed class AiQuotaService(HttpClient http, QuotaSources sources, string cacheFile, TimeProvider? time = null)
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
    private IReadOnlyList<ProviderQuota> _current = [];

    public IReadOnlyList<ProviderQuota> LoadCached()
    {
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

        _current = QuotaReconciler.Reconcile(_current, outcomes, _time.GetUtcNow());
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
                                       or UnauthorizedAccessException or InvalidOperationException
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

    private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private sealed class QuotaAuthException : Exception;
}

/// <summary>Display-only cache so the widget has content immediately after a restart.</summary>
public static class QuotaCache
{
    public static IReadOnlyList<ProviderQuota> Read(string file, DateTimeOffset now)
    {
        try
        {
            if (File.Exists(file)
                && JsonSerializer.Deserialize<List<ProviderQuota>>(File.ReadAllText(file)) is { } cached)
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

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(quotas));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Starts a process without a shell or window and captures stdout, with a hard timeout.</summary>
public static class ProcessRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

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
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return new CommandResult(process.ExitCode, await output);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new IOException("Command timed out");
            }
            finally
            {
                _ = error.ContinueWith(static _ => { }, TaskScheduler.Default);
            }
        }
    }
}
