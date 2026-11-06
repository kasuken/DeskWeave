using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskWeave.Core.Display;

/// <summary>Persists the monitor preference in a small JSON file next to the DeskWeave caches.</summary>
public sealed class DisplaySettingsStore(string file)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The saved mode, or <see cref="MonitorMode.Primary"/> when nothing valid is saved.</summary>
    public MonitorMode Load()
    {
        try
        {
            if (File.Exists(file)
                && JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Options) is { } settings
                && Enum.IsDefined(settings.MonitorMode))
            {
                return settings.MonitorMode;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        return MonitorMode.Primary;
    }

    public void Save(MonitorMode mode)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(mode), Options));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Settings(MonitorMode MonitorMode);
}
