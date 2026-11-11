using System.Text.Json;

namespace DeskWeave.Core.GitHubStatus;

public sealed record StatusSettings(IReadOnlySet<string> Enabled, string? Warning);

/// <summary>Persists status provider selection independently of monitor settings.</summary>
public sealed class StatusSettingsStore(string file)
{
    public StatusSettings Load()
    {
        var defaults = StatusProviders.All.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(file))
            {
                return new(defaults, null);
            }

            if (new FileInfo(file).Length > 16_384)
            {
                return new(defaults, "Service status settings are too large; defaults are in use.");
            }

            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(file));
            if (settings?.Enabled is null || settings.Enabled.Any(id => !defaults.Contains(id)))
            {
                return new(defaults, "Service status settings are invalid; defaults are in use.");
            }

            return new(settings.Enabled.ToHashSet(StringComparer.Ordinal), null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(defaults, "Service status settings could not be read; defaults are in use.");
        }
    }

    public void Save(IEnumerable<string> enabled)
    {
        var selected = enabled.ToHashSet(StringComparer.Ordinal);
        if (selected.Any(id => StatusProviders.All.All(provider => provider.Id != id)))
        {
            throw new ArgumentException("Unknown service status provider.", nameof(enabled));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(
            StatusProviders.All.Where(provider => selected.Contains(provider.Id)).Select(provider => provider.Id).ToArray())));
        File.Move(temporary, file, overwrite: true);
    }

    private sealed record Settings(string[] Enabled);
}
