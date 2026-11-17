using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskWeave.Core.AiQuota;

public sealed record ClaudeCredential(string AccessToken, string? Plan, DateTimeOffset? ExpiresAt);

public sealed record CodexCredential(string AccessToken, string? AccountId);

public sealed record AntigravityCredential(string AccessToken, DateTimeOffset? ExpiresAt);

public sealed record ParsedQuota(string? Plan, IReadOnlyList<QuotaLimit> Limits, string? Status = null, bool NoAccess = false);

/// <summary>
/// Pure parsers for the provider responses and local credential files. Credential records stay in memory only;
/// nothing produced here except <see cref="QuotaLimit"/> and plan labels is ever displayed or cached.
/// </summary>
public static class QuotaParsers
{
    private const string KeyringPrefix = "go-keyring-base64:";

    /// <summary>Maps a raw plan name to its display label, or null when the plan is free or unrecognized.</summary>
    public static string? PlanLabel(string provider, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var key = string.Join(' ', raw.ToLowerInvariant()
            .Replace('_', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return provider switch
        {
            QuotaProviders.Claude => key switch
            {
                "pro" => "Pro",
                "team" => "Team",
                "enterprise" => "Enterprise",
                "max" => "Max",
                "max 5x" => "Max 5x",
                "max 20x" => "Max 20x",
                _ => null,
            },
            QuotaProviders.Codex => key switch
            {
                "plus" => "Plus",
                "pro" => "Pro",
                "team" => "Team",
                "business" => "Business",
                "enterprise" => "Enterprise",
                "edu" or "education" => "Education",
                _ => null,
            },
            QuotaProviders.Copilot => key switch
            {
                "individual" or "individual pro" or "pro" => "Pro",
                "pro+" or "pro plus" or "individual pro plus" or "individual pro+" => "Pro+",
                "business" => "Business",
                "enterprise" => "Enterprise",
                "max" => "Max",
                _ => null,
            },
            QuotaProviders.Antigravity => key switch
            {
                "google ai plus" => "Google AI Plus",
                "google ai pro" or "pro" => "Google AI Pro",
                "google ai ultra" or "ultra" => "Google AI Ultra",
                "enterprise" => "Enterprise",
                "business" => "Business",
                _ => null,
            },
            _ => null,
        };
    }

    public static ClaudeCredential? ParseClaudeCredential(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGet(out var oauth, "claudeAiOauth") || oauth.Str("accessToken") is not { Length: > 0 } token)
        {
            return null;
        }

        var tier = oauth.Str("rateLimitTier")?.ToLowerInvariant() ?? string.Empty;
        var plan = tier.Contains("max_20x", StringComparison.Ordinal) ? "max 20x"
            : tier.Contains("max_5x", StringComparison.Ordinal) ? "max 5x"
            : oauth.Str("subscriptionType");
        DateTimeOffset? expires = oauth.Num("expiresAt") is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;
        return new ClaudeCredential(token, PlanLabel(QuotaProviders.Claude, plan), expires);
    }

    public static IReadOnlyList<QuotaLimit> ParseClaudeUsage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var limits = new List<QuotaLimit>();
        foreach (var (key, label) in new[] { ("five_hour", "5h"), ("seven_day", "7d") })
        {
            if (document.RootElement.TryGet(out var window, key) && window.ValueKind == JsonValueKind.Object)
            {
                limits.Add(new QuotaLimit(label, Clamp(window.Num("utilization")), window.Time("resets_at"), Identity: key));
            }
        }

        return limits;
    }

    public static CodexCredential? ParseCodexCredential(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var mode = root.Str("auth_mode");
        if (mode is not null && !mode.Equals("chatgpt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return root.TryGet(out var tokens, "tokens") && tokens.Str("access_token") is { Length: > 0 } token
            ? new CodexCredential(token, tokens.Str("account_id"))
            : null;
    }

    public static ParsedQuota ParseCodexUsage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var rawPlan = root.Str("plan_type");
        if (rawPlan is null || rawPlan.Contains("usage_based", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedQuota(null, [], NoAccess: true);
        }

        var limits = new List<QuotaLimit>();
        foreach (var key in new[] { "primary_window", "secondary_window" })
        {
            if (root.TryGet(out var window, "rate_limit", key) && window.ValueKind == JsonValueKind.Object)
            {
                DateTimeOffset? reset = window.Num("reset_at") is { } seconds ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;
                var windowSeconds = window.Num("limit_window_seconds");
                var identity = windowSeconds is { } length && double.IsFinite(length) && length > 0
                    ? $"{key}:{length.ToString("0", CultureInfo.InvariantCulture)}"
                    : key;
                limits.Add(new QuotaLimit(WindowLabel(windowSeconds), Clamp(window.Num("used_percent")), reset, Identity: identity));
            }
        }

        return new ParsedQuota(PlanLabel(QuotaProviders.Codex, rawPlan), limits);
    }

    public static ParsedQuota ParseCopilotUser(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (string.Equals(root.Str("access_type_sku"), "no_access", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedQuota(null, [], NoAccess: true);
        }

        var plan = PlanLabel(QuotaProviders.Copilot, root.Str("copilot_plan"));
        var reset = root.Time("quota_reset_date_utc") ?? root.Time("quota_reset_date");
        var limits = new List<QuotaLimit>();

        var premiumKey = root.TryGet(out var premium, "quota_snapshots", "premium_interactions")
            ? "premium_interactions"
            : root.TryGet(out premium, "quota_snapshots", "premium_requests")
                ? "premium_requests"
                : null;
        if (premiumKey is not null
            && premium.ValueKind == JsonValueKind.Object
            && premium.Bool("has_quota") != false)
        {
            var label = premium.Bool("token_based_billing") == true ? "AI credits" : "Premium requests";
            var identity = $"{premiumKey}:{(premium.Bool("token_based_billing") == true ? "credits" : "requests")}";
            if (premium.Bool("unlimited") == true)
            {
                limits.Add(new QuotaLimit(label, null, null, Unlimited: true, Identity: identity));
            }
            else
            {
                var entitlement = premium.Num("entitlement");
                var consumed = premium.Num("credits_used") is { } used && used > 0
                    ? used
                    : entitlement - premium.Num("remaining");
                var detail = entitlement is > 0 && consumed is { } c
                    ? $"{c.ToString("N0", CultureInfo.InvariantCulture)} / {entitlement.Value.ToString("N0", CultureInfo.InvariantCulture)}"
                    : null;
                var used100 = premium.Num("percent_remaining") is { } left ? Clamp(100 - left) : null;
                limits.Add(new QuotaLimit(label, used100, reset, detail, Identity: identity));
            }
        }
        else
        {
            foreach (var (key, label) in new[] { ("chat", "Chat"), ("completions", "Completions") })
            {
                if (root.TryGet(out var snapshot, "quota_snapshots", key) && snapshot.Bool("unlimited") == true)
                {
                    limits.Add(new QuotaLimit(label, null, null, Unlimited: true, Identity: key));
                }
            }
        }

        return new ParsedQuota(plan, limits, limits.Count == 0 ? "Quota unavailable" : null);
    }

    /// <summary>Parses the Antigravity keyring secret or token file, which may be base64 wrapped by go-keyring.</summary>
    public static AntigravityCredential? ParseAntigravityCredential(string secret)
    {
        var raw = secret.Trim();
        if (raw.StartsWith(KeyringPrefix, StringComparison.Ordinal))
        {
            raw = Encoding.UTF8.GetString(Convert.FromBase64String(raw[KeyringPrefix.Length..].Trim()));
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var method = root.Str("auth_method");
        if (method is not null && !method.Equals("consumer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = root.TryGet(out var nested, "token") && nested.ValueKind == JsonValueKind.Object ? nested : root;
        return token.Str("access_token") is { Length: > 0 } access
            ? new AntigravityCredential(access, token.Time("expiry"))
            : null;
    }

    /// <summary>Reads the subscription and AI credit balance, not model quotas, from loadCodeAssist.</summary>
    public static (string? Project, ParsedQuota Quota) ParseAntigravityAssist(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var project = root.TryGet(out var p, "cloudaicompanionProject")
            ? p.ValueKind == JsonValueKind.String ? p.GetString() : p.Str("id")
            : null;
        var plan = root.TryGet(out var paid, "paidTier") ? paid.Str("name") : null;
        plan ??= root.TryGet(out var current, "currentTier") ? current.Str("name") : null;
        var limits = new List<QuotaLimit>();
        if (paid.TryGet(out var credits, "availableCredits") && credits.ValueKind == JsonValueKind.Array)
        {
            foreach (var credit in credits.EnumerateArray())
            {
                if (credit.Str("creditType") == "GOOGLE_ONE_AI"
                    && credit.Num("creditAmount") is { } amount && double.IsFinite(amount) && amount >= 0)
                {
                    limits.Add(new QuotaLimit("AI credits", null, null,
                        Identity: "google_one_ai:credits", RemainingAmount: amount));
                    break;
                }
            }
        }

        return (project, new ParsedQuota(PlanLabel(QuotaProviders.Antigravity, plan), limits,
            limits.Count == 0 ? "Credits unavailable" : null));
    }

    public static IReadOnlyList<QuotaLimit> ParseAntigravitySummary(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var summary = root.TryGet(out var response, "response") ? response
            : root.TryGet(out var nested, "summary") ? nested : root;
        IEnumerable<(string? Name, JsonElement Buckets)> groups;
        if (summary.TryGet(out var grouped, "groups") && grouped.ValueKind == JsonValueKind.Array)
        {
            groups = grouped.EnumerateArray().Select(group =>
                (group.Str("displayName"), group.TryGet(out var groupBuckets, "buckets") ? groupBuckets : default));
        }
        else if (summary.TryGet(out var ungrouped, "buckets") && ungrouped.ValueKind == JsonValueKind.Array)
        {
            groups = [(null, ungrouped)];
        }
        else
        {
            throw new FormatException("Unrecognized Antigravity quota summary");
        }

        var limits = new List<QuotaLimit>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (group, buckets) in groups)
        {
            if (buckets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.Bool("disabled") == true)
                {
                    continue;
                }

                var remaining = bucket.TryGet(out var value, "remaining") && value.ValueKind == JsonValueKind.Object
                    ? value : bucket;
                var fraction = remaining.Num("remainingFraction");
                if (fraction is null && remaining.Num("remainingAmount") == 0)
                {
                    fraction = 0;
                }

                var used = fraction is { } f && double.IsFinite(f) && f is >= 0 and <= 1
                    ? Clamp((1 - f) * 100) : null;
                var reset = bucket.Time("resetTime") ?? remaining.Time("resetTime");
                var name = bucket.Str("displayName") ?? bucket.Str("window") ?? bucket.Str("bucketId");
                if (name is null || (used is null && reset is null))
                {
                    continue;
                }

                var stableKey = bucket.Str("bucketId") is { } bucketId ? $"bucket:{bucketId}"
                    : bucket.Str("window") is { } window ? $"window:{window}" : null;
                var identity = stableKey is null ? null : "antigravity-rate-limit:" + Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(stableKey)));
                if (identity is not null && !identities.Add(identity))
                {
                    identity = null;
                }

                limits.Add(new QuotaLimit(group is { Length: > 0 } ? $"{group} / {name}" : name,
                    used, reset, Identity: identity));
            }
        }

        return limits;
    }

    /// <summary>"5h", "7d" or "30m" from a window length in seconds.</summary>
    public static string WindowLabel(double? seconds)
    {
        if (seconds is not > 0)
        {
            return "Limit";
        }

        var s = (long)seconds.Value;
        return s % 86_400 == 0 ? $"{s / 86_400}d"
            : s % 3_600 == 0 ? $"{s / 3_600}h"
            : $"{Math.Max(1, s / 60)}m";
    }

    private static double? Clamp(double? value) => value is { } v && double.IsFinite(v) ? Math.Clamp(v, 0, 100) : null;

    private static bool TryGet(this JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var name in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out value))
            {
                value = default;
                return false;
            }
        }

        return value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    private static string? Str(this JsonElement element, string name) =>
        element.TryGet(out var value, name) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static double? Num(this JsonElement element, string name)
    {
        if (!element.TryGet(out var value, name))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool? Bool(this JsonElement element, string name) =>
        element.TryGet(out var value, name) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTimeOffset? Time(this JsonElement element, string name) =>
        element.Str(name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : null;
}
