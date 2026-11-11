namespace DeskWeave.Core.GitHubStatus;

public sealed record ProviderStatusReading(string Id, string Name, StatusReading Reading);

public sealed record StatusProvider(string Id, string Name, IReadOnlyList<string> Endpoints, string StatusPage);

public static class StatusProviders
{
    public const string GitHub = "github";
    public const string Anthropic = "anthropic";
    public const string OpenAI = "openai";

    public static readonly IReadOnlyList<StatusProvider> All =
    [
        new(GitHub, "GitHub", [StatusClient.Endpoint], "https://www.githubstatus.com/"),
        new(Anthropic, "Claude", ["https://status.claude.com/api/v2/summary.json"], "https://status.claude.com/"),
        new(OpenAI, "OpenAI",
            ["https://status.openai.com/api/v2/status.json", "https://status.openai.com/api/v2/components.json",
             "https://status.openai.com/api/v1/summary"], "https://status.openai.com/"),
    ];
}
