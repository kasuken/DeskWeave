using System.Globalization;
using System.Text.Json;

namespace DeskWeave.Core.AiQuota;

public enum QuotaForecastKind
{
    Collecting,
    InsufficientHistory,
    NoRecentConsumption,
    Estimate,
    LastsUntilReset,
    Exhausted,
    ResetDue,
    UsageUnknown,
    Unlimited,
    ResetUnavailable,
    Stale,
    Unavailable,
}

public readonly record struct QuotaLimitKey(string ProviderId, string Plan, string LimitIdentity);

public sealed record QuotaForecast(
    QuotaForecastKind Kind,
    string Message,
    double? PercentagePointsPerHour = null,
    TimeSpan? TimeToExhaustion = null);

/// <summary>
/// Keeps display-safe, bounded observations and makes a conservative usage forecast. Observations are
/// retained for at most 60 minutes and capped at 1,024 entries (512 KiB on disk). Continuity uses only the
/// provider, canonical plan, stable limit identity and reset time; account identifiers and credentials are never stored.
/// </summary>
public sealed class QuotaForecastEngine(TimeProvider? time = null)
{
    public static readonly TimeSpan ObservationWindow = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan MinimumSampleSpan = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumSampleAge = TimeSpan.FromMinutes(10);
    public const int MinimumSampleCount = 3;
    public const int MaximumObservations = 1_024;

    private const int MaximumHistoryBytes = 512 * 1_024;
    private const int MaximumLimitsPerProvider = 64;
    private const int MaximumIdentityLength = 128;
    private const int HistoryVersion = 1;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<QuotaUsageObservation> _observations = [];
    private readonly Dictionary<string, ProviderSeriesState> _providerStates = new(StringComparer.Ordinal);

    public int ObservationCount => _observations.Count;

    /// <summary>Loads only validated observations that are still inside the rolling history window.</summary>
    /// <returns>A safe warning when history could not be loaded; a missing file is not an error.</returns>
    public string? Load(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        _observations.Clear();
        _providerStates.Clear();
        var now = _time.GetUtcNow();

        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length is <= 0 or > MaximumHistoryBytes)
            {
                return "Quota history is invalid or too large; estimates will collect fresh samples.";
            }

            var history = JsonSerializer.Deserialize<QuotaHistoryDocument>(File.ReadAllText(file));
            if (history is not { Version: HistoryVersion, Observations: not null, Providers: not null }
                || history.Observations.Any(static observation => observation is null)
                || history.Providers.Any(static state => state is null))
            {
                return "Quota history is invalid or unsupported; estimates will collect fresh samples.";
            }

            foreach (var observation in history.Observations)
            {
                if (IsValidObservation(observation, now))
                {
                    _observations.Add(observation);
                }
            }

            foreach (var state in history.Providers)
            {
                if (IsValidProviderState(state))
                {
                    _providerStates[state.ProviderId] = state;
                }
            }

            NormalizeLoadedObservations();
            Prune(now);
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _observations.Clear();
            _providerStates.Clear();
            return "Quota history could not be read; estimates will collect fresh samples.";
        }
    }

    /// <summary>Persists only provider, plan, stable limit identity, reset time, timestamp and used percentage.</summary>
    /// <returns>A safe warning when history could not be persisted; otherwise <see langword="null"/>.</returns>
    public string? Save(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        Prune(_time.GetUtcNow());

        try
        {
            var history = new QuotaHistoryDocument(
                HistoryVersion,
                _observations,
                _providerStates.Values.OrderBy(state => state.ProviderId, StringComparer.Ordinal).ToList());
            var json = JsonSerializer.SerializeToUtf8Bytes(history);
            if (json.Length > MaximumHistoryBytes)
            {
                return "Quota history is too large to save; forecasts may not survive restart.";
            }

            var directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = file + ".tmp";
            File.WriteAllBytes(temporary, json);
            File.Move(temporary, file, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return "Quota history could not be saved; forecasts may not survive restart.";
        }
    }

    /// <summary>
    /// Adds an observation only from a fresh successful provider result. Failed or stale results clear the
    /// provider's series so a later success cannot bridge a period of unknown consumption.
    /// </summary>
    public void Observe(ProviderQuota quota, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(quota);
        if (!QuotaProviders.Order.Contains(quota.Id, StringComparer.Ordinal))
        {
            return;
        }

        if (quota.Stale || !IsCanonicalPlan(quota.Id, quota.Plan)
            || quota.FetchedAt > now || now - quota.FetchedAt > MaximumSampleAge
            || quota.Limits.Count is 0 or > MaximumLimitsPerProvider)
        {
            ResetProvider(quota.Id);
            return;
        }

        var identities = quota.Limits
            .Select(limit => NormalizeIdentity(limit.Identity))
            .ToArray();
        if (identities.Any(identity => identity is null)
            || identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
        {
            ResetProvider(quota.Id);
            return;
        }

        var normalizedIdentities = identities.Select(identity => identity!).ToArray();
        var state = new ProviderSeriesState(
            quota.Id, quota.Plan, normalizedIdentities.Order(StringComparer.Ordinal).ToList());
        if (_providerStates.TryGetValue(quota.Id, out var previousState))
        {
            if (previousState.Plan != state.Plan
                || !previousState.LimitIdentities.SequenceEqual(state.LimitIdentities, StringComparer.Ordinal))
            {
                RemoveProviderObservations(quota.Id);
            }
        }
        else if (_observations.Any(observation => observation.ProviderId == quota.Id))
        {
            // A history without its signature cannot establish plan/limit continuity.
            RemoveProviderObservations(quota.Id);
        }

        _providerStates[quota.Id] = state;

        foreach (var (limit, identity) in quota.Limits.Zip(normalizedIdentities))
        {
            if (limit.Unlimited || limit.UsedPercent is not { } used || !double.IsFinite(used) || used is < 0 or > 100
                || limit.ResetsAt is not { } resetAt || resetAt <= now)
            {
                RemoveLimitObservations(quota.Id, identity);
                continue;
            }

            var latest = LatestObservation(quota.Id, quota.Plan, identity);
            if (latest is not null)
            {
                if (latest.ResetAt != resetAt)
                {
                    RemoveLimitObservations(quota.Id, identity);
                }
                else if (quota.FetchedAt <= latest.ObservedAt)
                {
                    // Repeated same-time/same-value samples add no evidence; all other out-of-order
                    // observations are ignored rather than allowed to create a negative rate.
                    continue;
                }
                else if (quota.FetchedAt - latest.ObservedAt > MaximumSampleAge || used < latest.UsedPercent)
                {
                    RemoveLimitObservations(quota.Id, identity);
                }
            }

            _observations.Add(new QuotaUsageObservation(
                quota.Id, quota.Plan, identity, resetAt, quota.FetchedAt, used));
        }

        Prune(now);
    }

    /// <summary>Clears a provider's observations after an absent, failed or unauthorized refresh.</summary>
    public void ResetProvider(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        RemoveProviderObservations(providerId);
        _providerStates.Remove(providerId);
    }

    public IReadOnlyDictionary<QuotaLimitKey, QuotaForecast> Forecasts(
        IReadOnlyList<ProviderQuota> quotas,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(quotas);
        var forecasts = new Dictionary<QuotaLimitKey, QuotaForecast>();
        foreach (var quota in quotas)
        {
            foreach (var limit in quota.Limits)
            {
                if (NormalizeIdentity(limit.Identity) is not { } identity)
                {
                    continue;
                }

                forecasts[new QuotaLimitKey(quota.Id, quota.Plan, identity)] = Forecast(quota, limit, now);
            }
        }

        return forecasts;
    }

    public QuotaForecast Forecast(ProviderQuota quota, QuotaLimit limit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentNullException.ThrowIfNull(limit);

        if (quota.Stale || quota.FetchedAt > now || now - quota.FetchedAt > MaximumSampleAge)
        {
            return Result(QuotaForecastKind.Stale, "Estimate unavailable · stale data");
        }

        if (limit.Unlimited)
        {
            return Result(QuotaForecastKind.Unlimited, "Unlimited");
        }

        if (limit.UsedPercent is not { } used || !double.IsFinite(used) || used is < 0 or > 100)
        {
            return Result(QuotaForecastKind.UsageUnknown, "Usage unknown");
        }

        if (limit.ResetsAt is not { } resetAt)
        {
            return Result(QuotaForecastKind.ResetUnavailable, "Reset time unavailable");
        }

        if (resetAt <= now)
        {
            return Result(QuotaForecastKind.ResetDue, "Waiting for refreshed quota");
        }

        if (used >= 100)
        {
            return Result(QuotaForecastKind.Exhausted, "Quota exhausted");
        }

        if (!IsCanonicalPlan(quota.Id, quota.Plan) || NormalizeIdentity(limit.Identity) is not { } identity
            || !_providerStates.TryGetValue(quota.Id, out var state)
            || state.Plan != quota.Plan
            || !state.LimitIdentities.Contains(identity, StringComparer.Ordinal))
        {
            return Result(QuotaForecastKind.Collecting, "Collecting history");
        }

        var samples = _observations
            .Where(observation => observation.ProviderId == quota.Id
                                  && observation.Plan == quota.Plan
                                  && observation.LimitIdentity == identity
                                  && observation.ResetAt == resetAt
                                  && observation.ObservedAt <= now
                                  && now - observation.ObservedAt <= ObservationWindow)
            .OrderBy(observation => observation.ObservedAt)
            .ToList();

        if (samples.Count == 0
            || samples[^1].ObservedAt != quota.FetchedAt
            || samples[^1].UsedPercent != used)
        {
            return Result(QuotaForecastKind.Collecting, "Collecting history");
        }

        var firstContinuous = samples.Count - 1;
        while (firstContinuous > 0
               && samples[firstContinuous].ObservedAt - samples[firstContinuous - 1].ObservedAt <= MaximumSampleAge
               && samples[firstContinuous].UsedPercent >= samples[firstContinuous - 1].UsedPercent)
        {
            firstContinuous--;
        }

        var continuousSamples = samples.Skip(firstContinuous).ToArray();
        if (continuousSamples.Length < MinimumSampleCount)
        {
            return Result(QuotaForecastKind.Collecting, "Collecting history");
        }

        if (continuousSamples[^1].ObservedAt - continuousSamples[0].ObservedAt < MinimumSampleSpan)
        {
            return Result(QuotaForecastKind.InsufficientHistory, "Insufficient history · collecting samples");
        }

        var span = continuousSamples[^1].ObservedAt - continuousSamples[0].ObservedAt;
        var consumption = continuousSamples[^1].UsedPercent - continuousSamples[0].UsedPercent;
        if (consumption <= 0)
        {
            return Result(QuotaForecastKind.NoRecentConsumption, "No recent consumption");
        }

        var rate = consumption / span.TotalHours;
        if (!double.IsFinite(rate) || rate <= 0)
        {
            return Result(QuotaForecastKind.NoRecentConsumption, "No recent consumption");
        }

        return ForecastAtCurrentTime(used, resetAt, rate, quota.FetchedAt, now);
    }

    /// <summary>Re-evaluates an existing estimate against the current time without adding an observation.</summary>
    public static QuotaForecast AtTime(
        ProviderQuota quota,
        QuotaLimit limit,
        QuotaForecast forecast,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentNullException.ThrowIfNull(limit);
        ArgumentNullException.ThrowIfNull(forecast);

        if (quota.Stale || quota.FetchedAt > now || now - quota.FetchedAt > MaximumSampleAge)
        {
            return Result(QuotaForecastKind.Stale, "Estimate unavailable · stale data");
        }

        if (limit.Unlimited)
        {
            return Result(QuotaForecastKind.Unlimited, "Unlimited");
        }

        if (limit.UsedPercent is not { } used || !double.IsFinite(used) || used is < 0 or > 100)
        {
            return Result(QuotaForecastKind.UsageUnknown, "Usage unknown");
        }

        if (limit.ResetsAt is not { } resetAt)
        {
            return Result(QuotaForecastKind.ResetUnavailable, "Reset time unavailable");
        }

        if (resetAt <= now)
        {
            return Result(QuotaForecastKind.ResetDue, "Waiting for refreshed quota");
        }

        if (used >= 100)
        {
            return Result(QuotaForecastKind.Exhausted, "Quota exhausted");
        }

        if (forecast.Kind is not (QuotaForecastKind.Estimate or QuotaForecastKind.LastsUntilReset))
        {
            return forecast;
        }

        if (forecast.PercentagePointsPerHour is not { } rate || !double.IsFinite(rate) || rate <= 0)
        {
            return Result(QuotaForecastKind.Unavailable, "Estimate unavailable");
        }

        return ForecastAtCurrentTime(used, resetAt, rate, quota.FetchedAt, now);
    }

    private static QuotaForecast ForecastAtCurrentTime(
        double used,
        DateTimeOffset resetAt,
        double rate,
        DateTimeOffset observedAt,
        DateTimeOffset now)
    {
        var hoursToExhaustion = (100 - used) / rate - (now - observedAt).TotalHours;
        if (!double.IsFinite(hoursToExhaustion))
        {
            return Result(QuotaForecastKind.Unavailable, "Estimate unavailable");
        }

        if (hoursToExhaustion <= 0)
        {
            return Result(
                QuotaForecastKind.Estimate,
                $"Estimate: may be exhausted; resets in {FormatDuration(resetAt - now, roundUp: true)}",
                rate,
                TimeSpan.Zero);
        }

        if (hoursToExhaustion >= (resetAt - now).TotalHours)
        {
            return Result(QuotaForecastKind.LastsUntilReset, "Estimate: lasts until reset", rate);
        }

        var timeToExhaustion = TimeSpan.FromHours(Math.Max(0, hoursToExhaustion));
        var message =
            $"Estimate: {FormatDuration(timeToExhaustion, roundUp: false)} left; resets in {FormatDuration(resetAt - now, roundUp: true)}";
        return Result(QuotaForecastKind.Estimate, message, rate, timeToExhaustion);
    }

    private void NormalizeLoadedObservations()
    {
        var normalized = new List<QuotaUsageObservation>();
        foreach (var group in _observations
                     .GroupBy(observation => new
                     {
                         observation.ProviderId,
                         observation.Plan,
                         observation.LimitIdentity,
                         observation.ResetAt,
                     }))
        {
            var ordered = group.OrderBy(observation => observation.ObservedAt).ToArray();
            var start = 0;
            for (var index = 1; index < ordered.Length; index++)
            {
                if (ordered[index].ObservedAt - ordered[index - 1].ObservedAt > MaximumSampleAge
                    || ordered[index].UsedPercent < ordered[index - 1].UsedPercent)
                {
                    start = index;
                }
            }

            normalized.AddRange(ordered.Skip(start));
        }

        _observations.Clear();
        _observations.AddRange(normalized);
    }

    private void Prune(DateTimeOffset now)
    {
        _observations.RemoveAll(observation =>
            observation.ObservedAt > now || now - observation.ObservedAt > ObservationWindow);
        if (_observations.Count > MaximumObservations)
        {
            var retained = _observations
                .OrderByDescending(observation => observation.ObservedAt)
                .Take(MaximumObservations)
                .ToArray();
            _observations.Clear();
            _observations.AddRange(retained);
            _observations.Sort(static (left, right) => left.ObservedAt.CompareTo(right.ObservedAt));
        }

        var providersWithObservations = _observations.Select(observation => observation.ProviderId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var providerId in _providerStates.Keys.Where(id => !providersWithObservations.Contains(id)).ToArray())
        {
            _providerStates.Remove(providerId);
        }
    }

    private void RemoveProviderObservations(string providerId) =>
        _observations.RemoveAll(observation => observation.ProviderId == providerId);

    private void RemoveLimitObservations(string providerId, string identity) =>
        _observations.RemoveAll(observation => observation.ProviderId == providerId
                                               && observation.LimitIdentity == identity);

    private QuotaUsageObservation? LatestObservation(string providerId, string plan, string identity) =>
        _observations
            .Where(observation => observation.ProviderId == providerId
                                  && observation.Plan == plan
                                  && observation.LimitIdentity == identity)
            .MaxBy(observation => observation.ObservedAt);

    private bool IsValidObservation(QuotaUsageObservation observation, DateTimeOffset now) =>
        QuotaProviders.Order.Contains(observation.ProviderId, StringComparer.Ordinal)
        && IsCanonicalPlan(observation.ProviderId, observation.Plan)
        && NormalizeIdentity(observation.LimitIdentity) is not null
        && observation.ResetAt > observation.ObservedAt
        && observation.ObservedAt <= now
        && now - observation.ObservedAt <= ObservationWindow
        && double.IsFinite(observation.UsedPercent)
        && observation.UsedPercent is >= 0 and <= 100;

    private static bool IsValidProviderState(ProviderSeriesState state) =>
        QuotaProviders.Order.Contains(state.ProviderId, StringComparer.Ordinal)
        && IsCanonicalPlan(state.ProviderId, state.Plan)
        && state.LimitIdentities is { Count: > 0 and <= MaximumLimitsPerProvider }
        && state.LimitIdentities.All(identity => NormalizeIdentity(identity) is not null)
        && state.LimitIdentities.Distinct(StringComparer.Ordinal).Count() == state.LimitIdentities.Count;

    private static bool IsCanonicalPlan(string providerId, string plan) =>
        QuotaParsers.PlanLabel(providerId, plan) is { } canonical && canonical == plan;

    private static string? NormalizeIdentity(string? identity) =>
        identity is { Length: > 0 and <= MaximumIdentityLength }
        && identity.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-')
            ? identity
            : null;

    private static QuotaForecast Result(
        QuotaForecastKind kind,
        string message,
        double? rate = null,
        TimeSpan? timeToExhaustion = null) =>
        new(kind, message, rate, timeToExhaustion);

    private static string FormatDuration(TimeSpan duration, bool roundUp)
    {
        if (duration < TimeSpan.FromMinutes(5))
        {
            return "under 5m";
        }

        var fiveMinuteUnits = duration.TotalMinutes / 5;
        var roundedMinutes = (long)(roundUp ? Math.Ceiling(fiveMinuteUnits) : Math.Floor(fiveMinuteUnits)) * 5;
        if (roundedMinutes < 5)
        {
            return "under 5m";
        }

        var totalHours = roundedMinutes / 60;
        var remainingMinutes = roundedMinutes % 60;
        if (totalHours < 1)
        {
            return $"{roundedMinutes.ToString(CultureInfo.InvariantCulture)}m";
        }

        var days = totalHours / 24;
        var hours = totalHours % 24;
        if (days > 0)
        {
            return remainingMinutes == 0
                ? $"{days.ToString(CultureInfo.InvariantCulture)}d {hours.ToString(CultureInfo.InvariantCulture)}h"
                : $"{days.ToString(CultureInfo.InvariantCulture)}d {hours.ToString(CultureInfo.InvariantCulture)}h {remainingMinutes.ToString(CultureInfo.InvariantCulture)}m";
        }

        return remainingMinutes == 0
            ? $"{totalHours.ToString(CultureInfo.InvariantCulture)}h"
            : $"{totalHours.ToString(CultureInfo.InvariantCulture)}h {remainingMinutes.ToString(CultureInfo.InvariantCulture)}m";
    }

    private sealed record QuotaHistoryDocument(
        int Version,
        List<QuotaUsageObservation> Observations,
        List<ProviderSeriesState> Providers);

    private sealed record ProviderSeriesState(string ProviderId, string Plan, List<string> LimitIdentities);
}

public sealed record QuotaUsageObservation(
    string ProviderId,
    string Plan,
    string LimitIdentity,
    DateTimeOffset ResetAt,
    DateTimeOffset ObservedAt,
    double UsedPercent);
