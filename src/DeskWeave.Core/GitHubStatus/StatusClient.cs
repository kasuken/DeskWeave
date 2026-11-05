using System.Net;
using System.Text.Json;

namespace DeskWeave.Core.GitHubStatus;

/// <summary>Result of one poll. <see cref="Summary"/> is the latest data no older than 24 hours.</summary>
public sealed record StatusReading(StatusSummary? Summary, bool Online, bool Checking, DateTimeOffset CheckedAt);

/// <summary>Polls GitHub's public status API (no authentication) with a 24 hour on-disk cache.</summary>
public sealed class StatusClient(HttpClient http, string cacheFile, TimeProvider? time = null)
{
    public const string Endpoint = "https://www.githubstatus.com/api/v2/summary.json";
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(60);
    private const int MaxBytes = 2_000_000;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private StatusSummary? _last;

    public StatusReading LoadCached()
    {
        _last = ReadCache();
        return Publish(online: false, checking: true);
    }

    /// <summary>Fetches once and returns the reading plus the delay before the next poll.</summary>
    public async Task<(StatusReading Reading, TimeSpan NextDelay)> PollAsync(CancellationToken cancellationToken)
    {
        var delay = MinimumInterval;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("DeskWeave-GitHub-Status/1.0");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    delay = RetryAfter(response, delay);
                }

                return (Publish(online: false), delay);
            }

            var json = await ReadLimitedAsync(response.Content, cancellationToken);
            _last = StatusParser.Parse(json, _time.GetUtcNow());
            WriteCache(_last);
            return (Publish(online: true), delay);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (Publish(online: false), delay);
        }
    }

    private StatusReading Publish(bool online, bool checking = false)
    {
        var now = _time.GetUtcNow();
        if (_last is not null && now - _last.FetchedAt >= CacheLifetime)
        {
            _last = null;
            TryDelete();
        }

        return new StatusReading(_last, online, checking, now);
    }

    private TimeSpan RetryAfter(HttpResponseMessage response, TimeSpan fallback)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta
            ?? (header?.Date is { } date ? date - _time.GetUtcNow() : fallback);
        return wait > fallback ? wait : fallback;
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
            {
                throw new FormatException("Oversized status response");
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private StatusSummary? ReadCache()
    {
        try
        {
            if (!File.Exists(cacheFile))
            {
                return null;
            }

            var cached = JsonSerializer.Deserialize<StatusSummary>(File.ReadAllText(cacheFile));
            var age = _time.GetUtcNow() - (cached?.FetchedAt ?? DateTimeOffset.MinValue);
            if (cached?.Components is { Count: > 0 } && age >= TimeSpan.Zero && age < CacheLifetime)
            {
                return cached;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        TryDelete();
        return null;
    }

    private void WriteCache(StatusSummary summary)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var temporary = cacheFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(summary));
            File.Move(temporary, cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void TryDelete()
    {
        try
        {
            File.Delete(cacheFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
