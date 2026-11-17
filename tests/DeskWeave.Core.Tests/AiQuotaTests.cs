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

    [Theory]
    [InlineData("\"1250.5\"", 1250.5, "1,250.5 available")]
    [InlineData("1250", 1250, "1,250 available")]
    [InlineData("\"0\"", 0, "0 available")]
    public void Antigravity_assist_reads_credit_balance_separately_from_model_quotas(
        string amount, double expected, string value)
    {
        var (_, parsed) = QuotaParsers.ParseAntigravityAssist($$"""
            {"currentTier":{"name":"Free"},"paidTier":{"name":"Google AI Pro","availableCredits":[
              {"creditType":"OTHER","creditAmount":"99999"},
              {"creditType":"GOOGLE_ONE_AI","creditAmount":{{amount}},"minimumCreditAmountForUsage":"50"}]},
             "groups":[{"displayName":"Claude and GPT models","buckets":[{"remainingFraction":1}]}]}
            """);

        Assert.Equal("Google AI Pro", parsed.Plan);
        Assert.Null(parsed.Status);
        var limit = Assert.Single(parsed.Limits);
        Assert.Equal("AI credits", limit.Label);
        Assert.Equal(expected, limit.RemainingAmount);
        Assert.Equal("google_one_ai:credits", limit.Identity);
        Assert.Null(limit.UsedPercent);
        Assert.Null(limit.ResetsAt);
        Assert.Equal(value, QuotaText.Value(limit, Now));
        Assert.False(QuotaText.HasMeter(limit, Now));
        Assert.Empty(QuotaText.Detail(limit, Now));
    }

    [Theory]
    [InlineData("""{"creditType":"GOOGLE_ONE_AI"}""")]
    [InlineData("""{"creditType":"GOOGLE_ONE_AI","creditAmount":null}""")]
    [InlineData("""{"creditType":"GOOGLE_ONE_AI","creditAmount":"unknown"}""")]
    [InlineData("""{"creditType":"GOOGLE_ONE_AI","creditAmount":-1}""")]
    [InlineData("""{"creditType":"GOOGLE_ONE_AI","creditAmount":"NaN"}""")]
    [InlineData("""{"creditType":"GOOGLE_ONE_AI","creditAmount":"Infinity"}""")]
    [InlineData("""{"creditType":"OTHER","creditAmount":1000}""")]
    public void Antigravity_unknown_or_invalid_credit_balance_is_not_shown_as_zero(string credit)
    {
        var (_, parsed) = QuotaParsers.ParseAntigravityAssist($$$"""
            {"paidTier":{"name":"Google AI Pro","availableCredits":[{{{credit}}}]}}
            """);

        Assert.Equal("Google AI Pro", parsed.Plan);
        Assert.Empty(parsed.Limits);
        Assert.Equal("Credits unavailable", parsed.Status);
    }

    [Fact]
    public void Antigravity_missing_credits_preserves_the_plan_and_reads_the_project_for_fallback()
    {
        var (project, parsed) = QuotaParsers.ParseAntigravityAssist("""
            {"cloudaicompanionProject":{"id":"p-1"},"paidTier":{"name":"Google AI Pro"}}
            """);

        Assert.Equal("p-1", project);
        Assert.Equal("Google AI Pro", parsed.Plan);
        Assert.Empty(parsed.Limits);
        Assert.Equal("Credits unavailable", parsed.Status);
    }

    [Theory]
    [InlineData("""{"remainingFraction":0.25}""", 75d)]
    [InlineData("""{"remaining":{"remainingFraction":0.25}}""", 75d)]
    [InlineData("""{"remaining":{"remainingAmount":"0"}}""", 100d)]
    [InlineData("""{"remaining":{"remainingFraction":0}}""", 100d)]
    [InlineData("""{"remaining":{"remainingFraction":1}}""", 0d)]
    [InlineData("""{"remaining":{}}""", null)]
    [InlineData("""{"remaining":{"remainingFraction":-1}}""", null)]
    [InlineData("""{"remaining":{"remainingFraction":"NaN"}}""", null)]
    public void Antigravity_rate_limits_read_reported_usage_without_inventing_exhaustion(string remaining, double? expected)
    {
        var limits = QuotaParsers.ParseAntigravitySummary($$$"""
            {"response":{"groups":[{"displayName":"Claude and GPT models","buckets":[
              {"bucketId":"claude-weekly","displayName":"Weekly","resetTime":"2026-11-06T00:00:00Z",
               {{{remaining[1..^1]}}}},
              {"bucketId":"off","displayName":"Disabled","disabled":true,"remainingFraction":0}]}]}}
            """);

        var limit = Assert.Single(limits);
        Assert.Equal("Claude and GPT models / Weekly", limit.Label);
        Assert.Equal(expected, limit.UsedPercent);
        Assert.Equal(expected is not null, QuotaText.HasMeter(limit, Now));
        Assert.Null(limit.RemainingAmount);
        Assert.Equal(Now.AddHours(9), limit.ResetsAt);
        if (expected is null)
        {
            Assert.Equal("N/A", QuotaText.Value(limit, Now));
        }
    }

    [Fact]
    public void Antigravity_rate_limits_preserve_stable_private_identities_across_display_names()
    {
        var first = Assert.Single(QuotaParsers.ParseAntigravitySummary("""
            {"buckets":[{"bucketId":"model-bucket-1","displayName":"Weekly","remainingFraction":0.5}]}
            """));
        var renamed = Assert.Single(QuotaParsers.ParseAntigravitySummary("""
            {"summary":{"buckets":[{"bucketId":"model-bucket-1","displayName":"Renamed","remainingFraction":0.5}]}}
            """));

        Assert.Equal(first.Identity, renamed.Identity);
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

    [Theory]
    [InlineData("legacy")]
    [InlineData("credits")]
    [InlineData("rate-limits")]
    [InlineData("cli")]
    public void Cache_only_preserves_current_cli_antigravity_quotas(string kind)
    {
        var file = Path.Combine(Path.GetTempPath(), $"deskweave-quota-{Guid.NewGuid():N}.json");
        var limit = kind switch
        {
            "legacy" => new QuotaLimit("Claude and GPT models / Weekly", 0, Now.AddDays(1), Identity: "antigravity:old"),
            "rate-limits" => Assert.Single(QuotaParsers.ParseAntigravitySummary("""
                {"buckets":[{"bucketId":"weekly","displayName":"Weekly","remainingFraction":0.5}]}
                """)),
            "cli" => new QuotaLimit("Gemini / Weekly", 11.44, Now.AddDays(1),
                Identity: "agy-usage:gemini-weekly", ShowRemaining: true),
            _ => new QuotaLimit("AI credits", null, null, Identity: "google_one_ai:credits", RemainingAmount: 1250),
        };
        try
        {
            QuotaCache.Write(file,
                [new ProviderQuota("antigravity", "Antigravity", kind == "cli" ? "CLI" : "Google AI Pro", [limit], null, Now)]);

            var cached = QuotaCache.Read(file, Now);
            if (kind != "cli")
            {
                Assert.Empty(cached);
            }
            else
            {
                var quota = Assert.Single(cached);
                Assert.Equal("CLI", quota.Plan);
                Assert.Equal(limit, Assert.Single(quota.Limits));
                Assert.Null(quota.Status);
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Service_uses_cli_usage_report_for_antigravity_quotas()
    {
        var home = Directory.CreateTempSubdirectory("deskweave-home-").FullName;
        try
        {
            var requests = new List<string>();
            var sources = new QuotaSources(home, _ => null, (file, args, _) =>
            {
                if (file != "agy") return Task.FromResult<CommandResult?>(null);
                requests.Add(string.Join(" ", args));
                return Task.FromResult<CommandResult?>(args[0] == "--version"
                    ? new CommandResult(0, "1.5.10")
                    : new CommandResult(0, CliUsageReport));
            },
                () => """{"auth_method":"consumer","token":{"access_token":"test-token"}}""");
            using var http = new HttpClient(new FailingHandler());
            var cache = Path.Combine(home, "cache.json");
            var service = new AiQuotaService(http, sources, cache);
            var quota = Assert.Single(await service.RefreshAsync(CancellationToken.None));

            Assert.Equal(["--version", "-p /usage --output-format json --print-timeout 80s"], requests);
            Assert.Equal("CLI", quota.Plan);
            Assert.Equal(4, quota.Limits.Count);
            Assert.Equal("88.56% left", QuotaText.Value(quota.Limits[0], Now));
            Assert.Equal(quota.Limits, Assert.Single(QuotaCache.Read(cache, DateTimeOffset.UtcNow)).Limits);
            Assert.DoesNotContain("private@example.com", File.ReadAllText(cache), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, "Install Antigravity CLI to see quota")]
    [InlineData("1.1.10", "Update Antigravity CLI to see quota")]
    [InlineData("not a version", "Update Antigravity CLI to see quota")]
    public async Task Service_does_not_send_usage_prompt_to_missing_or_unsupported_cli(string? version, string expectedStatus)
    {
        var home = Directory.CreateTempSubdirectory("deskweave-home-").FullName;
        try
        {
            var requests = new List<string>();
            var sources = new QuotaSources(home, _ => null, (file, args, _) =>
            {
                if (file != "agy") return Task.FromResult<CommandResult?>(null);
                requests.Add(string.Join(" ", args));
                return Task.FromResult(version is null ? null : new CommandResult(0, version));
            }, () => """{"token":{"access_token":"test-token"}}""");
            using var http = new HttpClient(new FailingHandler());
            var quota = Assert.Single(await new AiQuotaService(http, sources, Path.Combine(home, "cache.json"))
                .RefreshAsync(CancellationToken.None));

            Assert.Equal(["--version"], requests);
            Assert.Empty(quota.Limits);
            Assert.Equal(expectedStatus, quota.Status);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
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

    private const string CliUsageReport = """
        {"status":"SUCCESS","command":{"name":"usage","data":{"account":"private@example.com","groups":[
          {"name":"Claude and GPT models","buckets":[
            {"id":"3p-weekly","remaining_fraction":1,"reset_time":"2099-01-01T00:00:00Z"},
            {"id":"3p-5h","remaining_fraction":1,"reset_time":"2099-01-01T00:00:00Z"}]},
          {"name":"Gemini Models","buckets":[
            {"id":"gemini-5h","remaining_fraction":1,"reset_time":"2099-01-01T00:00:00Z"},
            {"id":"gemini-weekly","remaining_fraction":0.8856,"reset_time":"2099-01-01T00:00:00Z"}]}]}}}
        """;

    [Fact]
    public void Antigravity_cli_report_orders_supported_model_groups_and_preserves_precise_remaining()
    {
        var limits = QuotaParsers.ParseAntigravityCliUsage(CliUsageReport);
        Assert.Equal(["Gemini / Weekly", "Gemini / 5h", "Claude + GPT / Weekly", "Claude + GPT / 5h"],
            limits.Select(limit => limit.Label));
        Assert.Equal(11.44, limits[0].UsedPercent!.Value, 2);
        Assert.Equal("88.56% left", QuotaText.Value(limits[0], Now));
        Assert.Equal("100% left", QuotaText.Value(limits[1], Now));
    }

    [Theory]
    [InlineData("""{"status":"ERROR","command":{"name":"usage","data":{"groups":[]}}}""")]
    [InlineData("""{"status":"SUCCESS","command":{"name":"other","data":{"groups":[]}}}""")]
    [InlineData("""{"status":"SUCCESS","command":{"name":"usage","data":{}}}""")]
    public void Antigravity_cli_report_rejects_non_usage_and_failed_responses(string json) =>
        Assert.Throws<FormatException>(() => QuotaParsers.ParseAntigravityCliUsage(json));

    [Fact]
    public void Antigravity_cli_report_never_invents_unknown_usage_or_accepts_disabled_buckets()
    {
        var limits = QuotaParsers.ParseAntigravityCliUsage("""
            {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"buckets":[
              {"id":"gemini-weekly","reset_time":"2099-01-01T00:00:00Z"},
              {"id":"gemini-5h","remaining_fraction":-1},
              {"id":"3p-weekly","remaining_fraction":"NaN"},
              {"id":"3p-5h","remaining_fraction":1,"disabled":true}]}]}}}
            """);
        Assert.Empty(limits);
    }

    [Fact]
    public void Antigravity_cli_report_rejects_duplicate_bucket_ids()
    {
        var json = """
            {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"buckets":[
              {"id":"gemini-weekly","remaining_fraction":0.8},
              {"id":"gemini-weekly","remaining_fraction":0.6}]}]}}}
            """;
        Assert.Throws<FormatException>(() => QuotaParsers.ParseAntigravityCliUsage(json));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No network in tests");
    }
}
