using System.Text;
using DeskWeave.Core.AiQuota;

namespace DeskWeave.Core.Tests;

public class AiQuotaTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("claude", "max_20x", "Max 20x")]
    [InlineData("claude", "team", "Team")]
    [InlineData("claude", "free", null)]
    [InlineData("codex", "edu", "Education")]
    [InlineData("codex", "plus", "Plus")]
    [InlineData("codex", "free", null)]
    [InlineData("copilot", "individual_pro", "Pro")]
    [InlineData("copilot", "individual-pro-plus", "Pro+")]
    [InlineData("copilot", "enterprise", "Enterprise")]
    [InlineData("copilot", "free", null)]
    [InlineData("antigravity", "Google AI Ultra", "Google AI Ultra")]
    [InlineData("antigravity", "pro", "Google AI Pro")]
    [InlineData("antigravity", "standard-tier", null)]
    public void PlanLabel_maps_paid_plans_and_hides_free_ones(string provider, string raw, string? expected) =>
        Assert.Equal(expected, QuotaParsers.PlanLabel(provider, raw));

    [Fact]
    public void Claude_credential_prefers_max_tier_and_reads_expiry()
    {
        var credential = QuotaParsers.ParseClaudeCredential(
            """{"claudeAiOauth":{"accessToken":"t","subscriptionType":"max","rateLimitTier":"default_claude_max_20x","expiresAt":1793900000000}}""");

        Assert.NotNull(credential);
        Assert.Equal("Max 20x", credential.Plan);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793900000000), credential.ExpiresAt);
    }

    [Fact]
    public void Claude_usage_reads_five_hour_and_weekly_windows()
    {
        var limits = QuotaParsers.ParseClaudeUsage(
            """{"five_hour":{"utilization":42.5,"resets_at":"2026-11-05T17:00:00Z"},"seven_day":{"utilization":null,"resets_at":null}}""");

        Assert.Collection(limits,
            l =>
            {
                Assert.Equal("5h", l.Label);
                Assert.Equal(42.5, l.UsedPercent);
                Assert.Equal(Now.AddHours(2), l.ResetsAt);
                Assert.Equal("five_hour", l.Identity);
            },
            l => { Assert.Equal("7d", l.Label); Assert.Null(l.UsedPercent); Assert.Null(l.ResetsAt); });
    }

    [Fact]
    public void Codex_credential_requires_chatgpt_login()
    {
        Assert.NotNull(QuotaParsers.ParseCodexCredential("""{"auth_mode":"chatgpt","tokens":{"access_token":"t","account_id":"a"}}"""));
        Assert.Null(QuotaParsers.ParseCodexCredential("""{"auth_mode":"apikey","OPENAI_API_KEY":"k"}"""));
    }

    [Fact]
    public void Codex_usage_labels_windows_by_length()
    {
        var parsed = QuotaParsers.ParseCodexUsage("""
            {"plan_type":"plus","rate_limit":{
              "primary_window":{"used_percent":0,"limit_window_seconds":18000,"reset_at":1793909692},
              "secondary_window":{"used_percent":28,"limit_window_seconds":604800,"reset_at":1794378984}}}
            """);

        Assert.Equal("Plus", parsed.Plan);
        Assert.Equal(["5h", "7d"], parsed.Limits.Select(l => l.Label));
        Assert.Equal(["primary_window:18000", "secondary_window:604800"], parsed.Limits.Select(l => l.Identity));
        Assert.Equal(28, parsed.Limits[1].UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794378984), parsed.Limits[1].ResetsAt);
    }

    [Fact]
    public void Codex_usage_based_plan_is_hidden() =>
        Assert.True(QuotaParsers.ParseCodexUsage("""{"plan_type":"usage_based_team"}""").NoAccess);

    [Fact]
    public void Copilot_premium_credits_show_consumed_and_entitlement()
    {
        var parsed = QuotaParsers.ParseCopilotUser("""
            {"copilot_plan":"enterprise","access_type_sku":"copilot_enterprise_seat_quota","quota_reset_date_utc":"2026-12-01T00:00:00.000Z",
             "quota_snapshots":{"premium_interactions":{"percent_remaining":84.7,"unlimited":false,"has_quota":true,
               "token_based_billing":true,"credits_used":7514,"remaining":42374,"entitlement":50000}}}
            """);

        var limit = Assert.Single(parsed.Limits);
        Assert.Equal("Enterprise", parsed.Plan);
        Assert.Equal("AI credits", limit.Label);
        Assert.Equal("premium_interactions:credits", limit.Identity);
        Assert.Equal(15.3, limit.UsedPercent!.Value, 3);
        Assert.Equal("7,514 / 50,000", limit.Detail);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), limit.ResetsAt);
    }

    [Fact]
    public void Copilot_unlimited_and_no_access_are_handled()
    {
        var unlimited = QuotaParsers.ParseCopilotUser("""
            {"copilot_plan":"business","quota_snapshots":{"premium_interactions":{"unlimited":true,"has_quota":true}}}
            """);
        Assert.True(Assert.Single(unlimited.Limits).Unlimited);
        Assert.Equal("Premium requests", unlimited.Limits[0].Label);

        Assert.True(QuotaParsers.ParseCopilotUser("""{"copilot_plan":"individual","access_type_sku":"no_access"}""").NoAccess);
    }

    [Fact]
    public void Copilot_without_premium_falls_back_to_unlimited_chat_rows()
    {
        var parsed = QuotaParsers.ParseCopilotUser("""
            {"copilot_plan":"individual","quota_snapshots":{"chat":{"unlimited":true},"completions":{"unlimited":true}}}
            """);

        Assert.Equal(["Chat", "Completions"], parsed.Limits.Select(l => l.Label));
        Assert.Null(parsed.Status);
    }

    [Fact]
    public void Antigravity_credential_accepts_go_keyring_base64()
    {
        var json = """{"auth_method":"consumer","token":{"access_token":"t","expiry":"2026-11-05T16:00:00Z"}}""";
        var secret = "go-keyring-base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var credential = QuotaParsers.ParseAntigravityCredential(secret);

        Assert.NotNull(credential);
        Assert.Equal("t", credential.AccessToken);
        Assert.Equal(Now.AddHours(1), credential.ExpiresAt);
        Assert.Null(QuotaParsers.ParseAntigravityCredential("""{"auth_method":"api_key","token":{"access_token":"t"}}"""));
    }

    [Fact]
    public void Antigravity_assist_reads_project_object_and_paid_tier()
    {
        var (project, plan) = QuotaParsers.ParseAntigravityAssist(
            """{"cloudaicompanionProject":{"id":"p-1"},"currentTier":{"name":"Free"},"paidTier":{"name":"Google AI Pro"}}""");

        Assert.Equal("p-1", project);
        Assert.Equal("Google AI Pro", plan);
    }

    [Fact]
    public void Antigravity_summary_builds_group_labels_and_exhausted_buckets()
    {
        var limits = QuotaParsers.ParseAntigravitySummary("""
            {"groups":[{"displayName":"Gemini","buckets":[
              {"displayName":"Daily","remainingFraction":0.25,"resetTime":"2026-11-06T00:00:00Z"},
              {"window":"Weekly","remainingAmount":0,"resetTime":"2026-11-10T00:00:00Z"},
              {"bucketId":"off","disabled":true,"remainingFraction":1}]}]}
            """);

        Assert.Collection(limits,
            l => { Assert.Equal("Gemini / Daily", l.Label); Assert.Equal(75, l.UsedPercent); },
            l => { Assert.Equal("Gemini / Weekly", l.Label); Assert.Equal(100, l.UsedPercent); });
    }

    [Fact]
    public void Antigravity_identity_uses_a_stable_provider_key_not_the_display_label()
    {
        var first = Assert.Single(QuotaParsers.ParseAntigravitySummary("""
            {"groups":[{"displayName":"Gemini","buckets":[
              {"bucketId":"model-bucket-1","displayName":"Daily","remainingFraction":0.5,"resetTime":"2026-11-06T00:00:00Z"}]}]}
            """));
        var translated = Assert.Single(QuotaParsers.ParseAntigravitySummary("""
            {"groups":[{"displayName":"Gemini","buckets":[
              {"bucketId":"model-bucket-1","displayName":"Día","remainingFraction":0.5,"resetTime":"2026-11-06T00:00:00Z"}]}]}
            """));

        Assert.Equal(first.Identity, translated.Identity);
        Assert.DoesNotContain("model-bucket-1", first.Identity, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconcile_orders_providers_and_applies_outcomes()
    {
        var previousCodex = Quota("codex", Now.AddHours(-1));
        var previousCopilot = Quota("copilot", Now.AddHours(-2));
        var freshClaude = Quota("claude", Now);

        var result = QuotaReconciler.Reconcile(
            [previousCodex, previousCopilot],
            [ProviderOutcome.Error("codex"), ProviderOutcome.Absent("copilot"), ProviderOutcome.Active(freshClaude)],
            Now);

        Assert.Equal(["claude", "codex"], result.Select(q => q.Id));
        Assert.True(result[1].Stale);
        Assert.Equal(QuotaReconciler.OfflineStatus, result[1].Status);
        Assert.Equal(previousCodex.Limits, result[1].Limits);
    }

    [Fact]
    public void Reconcile_drops_errors_older_than_a_day()
    {
        var result = QuotaReconciler.Reconcile([Quota("codex", Now.AddHours(-25))], [ProviderOutcome.Error("codex")], Now);

        Assert.Empty(result);
    }

    [Fact]
    public void Reconcile_auth_keeps_the_plan_and_hides_meters()
    {
        var result = QuotaReconciler.Reconcile(
            [Quota("antigravity", Now.AddHours(-1))],
            [ProviderOutcome.Auth("claude", "Team"), ProviderOutcome.Auth("antigravity"), ProviderOutcome.Auth("codex")],
            Now);

        Assert.Equal(["claude", "antigravity"], result.Select(q => q.Id));
        Assert.All(result, q => { Assert.Empty(q.Limits); Assert.Equal(QuotaReconciler.SignInStatus, q.Status); });
        Assert.Equal("Team", result[0].Plan);
    }

    [Theory]
    [InlineData(-5, "Waiting for refreshed quota")]
    [InlineData(45, "Resets in 45m")]
    [InlineData(125, "Resets in 2h 5m")]
    [InlineData(3 * 24 * 60, "Resets Sun 15:00")]
    public void Reset_text_matches_omarchy_wording(int minutes, string expected) =>
        Assert.Equal(expected, QuotaText.Reset(Now.AddMinutes(minutes), Now, TimeZoneInfo.Utc));

    [Fact]
    public void Value_text_covers_unlimited_expired_and_unknown()
    {
        Assert.Equal("Unlimited", QuotaText.Value(new QuotaLimit("Chat", null, null, Unlimited: true), Now));
        Assert.Equal("Refresh due", QuotaText.Value(new QuotaLimit("5h", 80, Now.AddMinutes(-1)), Now));
        Assert.False(QuotaText.HasMeter(new QuotaLimit("5h", 80, Now.AddMinutes(-1)), Now));
        Assert.Equal("N/A", QuotaText.Value(new QuotaLimit("7d", null, null), Now));
        Assert.Equal("43% used", QuotaText.Value(new QuotaLimit("7d", 42.6, Now.AddDays(1)), Now));
        Assert.Equal("7,514 / 50,000 · Resets in 45m",
            QuotaText.Detail(new QuotaLimit("AI credits", 15, Now.AddMinutes(45), "7,514 / 50,000"), Now));
    }

    [Fact]
    public void Cache_round_trips_display_data_and_skips_expired_entries()
    {
        var file = Path.Combine(Path.GetTempPath(), $"deskweave-quota-{Guid.NewGuid():N}.json");
        try
        {
            QuotaCache.Write(file, [Quota("claude", Now.AddHours(-1)), Quota("codex", Now.AddHours(-30))]);
            var cached = QuotaCache.Read(file, Now);

            var claude = Assert.Single(cached);
            Assert.Equal("claude", claude.Id);
            Assert.Equal(42, claude.Limits[0].UsedPercent);
            Assert.DoesNotContain("token", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Service_hides_missing_sign_ins_and_marks_expired_claude_login()
    {
        var home = Directory.CreateTempSubdirectory("deskweave-home-").FullName;
        var cache = Path.Combine(home, "cache.json");
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            File.WriteAllText(Path.Combine(home, ".claude", ".credentials.json"),
                """{"claudeAiOauth":{"accessToken":"t","subscriptionType":"pro","expiresAt":1000}}""");

            var sources = new QuotaSources(home, _ => null, (_, _, _) => Task.FromResult<CommandResult?>(null), () => null);
            using var http = new HttpClient(new FailingHandler());
            var service = new AiQuotaService(http, sources, cache);

            var result = await service.RefreshAsync(CancellationToken.None);

            var claude = Assert.Single(result);
            Assert.Equal("Pro", claude.Plan);
            Assert.Equal(QuotaReconciler.SignInStatus, claude.Status);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static ProviderQuota Quota(string id, DateTimeOffset fetchedAt) =>
        new(id, QuotaProviders.Name(id), "Pro", [new QuotaLimit("5h", 42, fetchedAt.AddHours(3))], null, fetchedAt);

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network in tests");
    }
}
