using System.Globalization;

namespace DeskWeave.Core.AiQuota;

/// <summary>Merges a refresh round into the previous provider list, applying the last-known-value rules.</summary>
public static class QuotaReconciler
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    public const string OfflineStatus = "Offline · last known plan";
    public const string SignInStatus = "Sign in to refresh";

    public static IReadOnlyList<ProviderQuota> Reconcile(
        IReadOnlyList<ProviderQuota> previous, IReadOnlyList<ProviderOutcome> outcomes, DateTimeOffset now)
    {
        var result = new List<ProviderQuota>();
        foreach (var id in QuotaProviders.Order)
        {
            var last = previous.FirstOrDefault(p => p.Id == id);
            if (last is not null && !IsFresh(last, now))
            {
                last = null;
            }

            var outcome = outcomes.FirstOrDefault(o => o.Id == id);
            var next = outcome?.Kind switch
            {
                null => last,
                OutcomeKind.Active => outcome.Quota,
                OutcomeKind.Absent => null,
                OutcomeKind.Error => last is null ? null : last with { Stale = true, Status = OfflineStatus },
                OutcomeKind.Auth => (outcome.Plan ?? last?.Plan) is { } plan
                    ? new ProviderQuota(id, QuotaProviders.Name(id), plan, [], SignInStatus, last?.FetchedAt ?? now)
                    : null,
                _ => null,
            };

            if (next is not null)
            {
                result.Add(next);
            }
        }

        return result;
    }

    public static bool IsFresh(ProviderQuota quota, DateTimeOffset now)
    {
        var age = now - quota.FetchedAt;
        return age >= TimeSpan.Zero && age < Lifetime;
    }
}

/// <summary>Text shown for a quota row, shared by the widget and the tests.</summary>
public static class QuotaText
{
    public static string Value(QuotaLimit limit, DateTimeOffset now) =>
        limit.Unlimited ? "Unlimited"
        : limit.ResetsAt is { } reset && reset <= now ? "Refresh due"
        : limit.UsedPercent is { } used ? $"{Math.Round(used).ToString(CultureInfo.InvariantCulture)}% used"
        : "N/A";

    /// <summary>True when the meter should be drawn: a percentage is known and its window has not already reset.</summary>
    public static bool HasMeter(QuotaLimit limit, DateTimeOffset now) =>
        !limit.Unlimited && limit.UsedPercent is not null && !(limit.ResetsAt is { } reset && reset <= now);

    public static string? Reset(DateTimeOffset? resetsAt, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (resetsAt is not { } reset)
        {
            return null;
        }

        var minutes = (int)Math.Ceiling((reset - now).TotalMinutes);
        if (minutes <= 0)
        {
            return "Waiting for refreshed quota";
        }

        if (minutes < 60)
        {
            return $"Resets in {minutes}m";
        }

        if (minutes < 24 * 60)
        {
            return $"Resets in {minutes / 60}h {minutes % 60}m";
        }

        var local = TimeZoneInfo.ConvertTime(reset, zone ?? TimeZoneInfo.Local);
        return $"Resets {local.ToString("ddd HH:mm", CultureInfo.InvariantCulture)}";
    }

    public static string Detail(QuotaLimit limit, DateTimeOffset now, TimeZoneInfo? zone = null) =>
        string.Join(" · ", new[] { limit.Detail, Reset(limit.ResetsAt, now, zone) }.Where(part => !string.IsNullOrEmpty(part)));
}
