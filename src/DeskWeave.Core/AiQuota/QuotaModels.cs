namespace DeskWeave.Core.AiQuota;

/// <summary>One usage limit of a subscription. <see cref="UsedPercent"/> is null when the provider did not report a percentage.</summary>
public sealed record QuotaLimit(
    string Label,
    double? UsedPercent,
    DateTimeOffset? ResetsAt,
    string? Detail = null,
    bool Unlimited = false);

/// <summary>Display-only snapshot of one paid AI subscription. Never contains credentials or account identifiers.</summary>
public sealed record ProviderQuota(
    string Id,
    string Name,
    string Plan,
    IReadOnlyList<QuotaLimit> Limits,
    string? Status,
    DateTimeOffset FetchedAt,
    bool Stale = false);

public enum OutcomeKind
{
    /// <summary>Fresh data for a recognized paid plan.</summary>
    Active,

    /// <summary>No credentials, no access, or a free/unrecognized plan: the row is removed.</summary>
    Absent,

    /// <summary>Network or provider failure: last known data is kept, marked stale.</summary>
    Error,

    /// <summary>Credentials exist but are expired or rejected: the plan stays, meters are hidden.</summary>
    Auth,
}

public sealed record ProviderOutcome(string Id, OutcomeKind Kind, ProviderQuota? Quota = null, string? Plan = null)
{
    public static ProviderOutcome Active(ProviderQuota quota) => new(quota.Id, OutcomeKind.Active, quota);

    public static ProviderOutcome Absent(string id) => new(id, OutcomeKind.Absent);

    public static ProviderOutcome Error(string id) => new(id, OutcomeKind.Error);

    public static ProviderOutcome Auth(string id, string? plan = null) => new(id, OutcomeKind.Auth, Plan: plan);
}

/// <summary>Provider identifiers, display names and the fixed display order.</summary>
public static class QuotaProviders
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string Copilot = "copilot";
    public const string Antigravity = "antigravity";

    public static readonly IReadOnlyList<string> Order = [Claude, Codex, Copilot, Antigravity];

    public static string Name(string id) => id switch
    {
        Claude => "Claude",
        Codex => "Codex",
        Copilot => "GitHub Copilot",
        Antigravity => "Antigravity",
        _ => id,
    };
}
