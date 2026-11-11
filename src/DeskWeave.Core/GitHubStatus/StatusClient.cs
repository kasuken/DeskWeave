using System.Text.Json;

namespace DeskWeave.Core.GitHubStatus;

/// <summary>Result of one poll. <see cref="Summary"/> is the latest data no older than 24 hours.</summary>
public sealed record StatusReading(StatusSummary? Summary, bool Online, bool Checking, DateTimeOffset CheckedAt);

/// <summary>Polls a public status API (no authentication) with a 24 hour on-disk cache.</summary>
public sealed class StatusClient
{
    public const string Endpoint = "https://www.githubstatus.com/api/v2/summary.json";
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(60);
    private const int MaxBytes = 2_000_000;

    private readonly HttpClient _http;
    private readonly string _cacheFile;
    private readonly IReadOnlyList<string> _endpoints;
    private readonly Func<IReadOnlyList<string>, DateTimeOffset, StatusSummary> _parse;
    private readonly TimeProvider _time;
    private StatusSummary? _last;

    public StatusClient(HttpClient http, string cacheFile, TimeProvider? time = null)
        : this(http, cacheFile, Endpoint, StatusParser.Parse, time)
    {
    }

    public StatusClient(HttpClient http, string cacheFile, string endpoint,
        Func<string, DateTimeOffset, StatusSummary> parse, TimeProvider? time = null)
        : this(http, cacheFile, [endpoint], (bodies, fetchedAt) => parse(bodies[0], fetchedAt), time)
    {
    }

    public StatusClient(HttpClient http, string cacheFile, IReadOnlyList<string> endpoints,
        Func<IReadOnlyList<string>, DateTimeOffset, StatusSummary> parse, TimeProvider? time = null)
    {
        _http = http;
        _cacheFile = cacheFile;
        if (endpoints.Count == 0) throw new ArgumentException("At least one endpoint is required.", nameof(endpoints));
        _endpoints = endpoints;
        _parse = parse;
        _time = time ?? TimeProvider.System;
    }

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
            var bodies = new List<string>(_endpoints.Count);
            foreach (var endpoint in _endpoints)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.UserAgent.ParseAdd("DeskWeave-Service-Status/1.0");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.Headers.RetryAfter is not null)
                {
                    delay = RetryAfter(response, delay);
                }
                if (!response.IsSuccessStatusCode)
                {
                    return (Publish(online: false), delay);
                }
                bodies.Add(await ReadLimitedAsync(response.Content, cancellationToken));
            }

            _last = _parse(bodies, _time.GetUtcNow());
            WriteCache(_last);
            return (Publish(online: true), delay);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (Publish(online: false), delay);
        }
    }

    public StatusReading Publish(bool online, bool checking = false)
    {
        var now = _time.GetUtcNow();
        if (_last is not null && (now < _last.FetchedAt || now - _last.FetchedAt >= CacheLifetime))
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
            if (!File.Exists(_cacheFile))
            {
                return null;
            }

            if (new FileInfo(_cacheFile).Length > MaxBytes)
            {
                throw new FormatException("Oversized status cache");
            }

            var cached = JsonSerializer.Deserialize<StatusSummary>(File.ReadAllText(_cacheFile));
            var age = _time.GetUtcNow() - (cached?.FetchedAt ?? DateTimeOffset.MinValue);
            if (cached?.Components is { Count: > 0 } && cached.Incidents is not null && cached.Maintenance is not null
                && age >= TimeSpan.Zero && age < CacheLifetime)
            {
                return cached;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException or FormatException)
        {
        }

        TryDelete();
        return null;
    }

    private void WriteCache(StatusSummary summary)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var temporary = _cacheFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(summary));
            File.Move(temporary, _cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void TryDelete()
    {
        try
        {
            File.Delete(_cacheFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
