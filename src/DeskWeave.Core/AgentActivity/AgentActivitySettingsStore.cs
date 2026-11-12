using System.Collections.ObjectModel;
using System.Text.Json;

namespace DeskWeave.Core.AgentActivity;

/// <summary>Enabled integration identifiers and locally configured workspace destinations.</summary>
public sealed record AgentActivitySettings(
    IReadOnlyList<string> EnabledIntegrations, IReadOnlyDictionary<string, string> Workspaces, string? Warning);

/// <summary>Stores opt-in agent activity settings without exposing workspace paths to snapshots.</summary>
public sealed class AgentActivitySettingsStore
{
    private const string InvalidWarning = "Agent activity settings could not be loaded; integrations are disabled.";
    private readonly string _file;

    /// <summary>Creates a store for the specified settings file.</summary>
    public AgentActivitySettingsStore(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        _file = Path.GetFullPath(file);
        if (_file.StartsWith(@"\\", StringComparison.Ordinal) || _file.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Settings must be stored locally.", nameof(file));
    }

    /// <summary>Checks that a workspace destination is an existing local directory without reparse points.</summary>
    public static bool IsValidWorkspaceDestination(string? path) => AgentActivityValidation.LocalDirectory(path);

    /// <summary>Loads validated settings, or disables integrations with a sanitized warning on failure.</summary>
    public AgentActivitySettings Load()
    {
        try
        {
            string directory = Path.GetDirectoryName(_file) ?? throw new IOException("Invalid settings path.");
            if (!AgentActivityValidation.HasNoReparseDirectories(directory))
                return Empty(InvalidWarning);
            if ((File.GetAttributes(_file) & FileAttributes.ReparsePoint) != 0)
                return Empty(InvalidWarning);
            using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            byte[] bytes = new byte[AgentActivityValidation.MaxBytes + 1];
            int count = 0;
            while (count < bytes.Length)
            {
                int read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count > AgentActivityValidation.MaxBytes)
                return Empty(InvalidWarning);
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (!AgentSnapshotParser.Properties(root, ["EnabledIntegrations", "Workspaces"]) ||
                root.GetProperty("EnabledIntegrations").ValueKind != JsonValueKind.Array ||
                root.GetProperty("Workspaces").ValueKind != JsonValueKind.Object)
                return Empty(InvalidWarning);

            var integrations = new List<string>();
            foreach (var item in root.GetProperty("EnabledIntegrations").EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } id ||
                    !AgentActivityValidation.AdapterId(id) || integrations.Contains(id, StringComparer.Ordinal) ||
                    integrations.Count == AgentActivityValidation.MaxSettingsItems)
                    return Empty(InvalidWarning);
                integrations.Add(id);
            }

            var workspaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.GetProperty("Workspaces").EnumerateObject())
            {
                if (!AgentActivityValidation.DisplayText(item.Name, 48) || item.Value.ValueKind != JsonValueKind.String ||
                    item.Value.GetString() is not { } path || !AgentActivityValidation.LocalDirectory(path) ||
                    workspaces.Count == AgentActivityValidation.MaxSettingsItems || !workspaces.TryAdd(item.Name, path))
                    return Empty(InvalidWarning);
            }
            return new(integrations.AsReadOnly(), new ReadOnlyDictionary<string, string>(workspaces), null);
        }
        catch (FileNotFoundException) { return Empty(null); }
        catch (DirectoryNotFoundException) { return Empty(null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return Empty(InvalidWarning);
        }
    }

    /// <summary>Validates and atomically replaces the settings file; validation and I/O errors propagate.</summary>
    public void Save(IEnumerable<string> enabledIntegrations, IReadOnlyDictionary<string, string> workspaces)
    {
        ArgumentNullException.ThrowIfNull(enabledIntegrations);
        ArgumentNullException.ThrowIfNull(workspaces);
        var integrations = new List<string>();
        foreach (var id in enabledIntegrations)
        {
            if (!AgentActivityValidation.AdapterId(id) || integrations.Contains(id, StringComparer.Ordinal) ||
                integrations.Count == AgentActivityValidation.MaxSettingsItems)
                throw new ArgumentException("Invalid integration settings.", nameof(enabledIntegrations));
            integrations.Add(id);
        }
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, path) in workspaces)
        {
            if (!AgentActivityValidation.DisplayText(alias, 48) || !AgentActivityValidation.LocalDirectory(path) ||
                mappings.Count == AgentActivityValidation.MaxSettingsItems || !mappings.TryAdd(alias, path))
                throw new ArgumentException("Invalid workspace settings.", nameof(workspaces));
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new SettingsFile(integrations, mappings));
        if (bytes.Length > AgentActivityValidation.MaxBytes)
            throw new ArgumentException("Settings exceed the size limit.");

        string directory = Path.GetDirectoryName(_file) ?? throw new ArgumentException("Invalid settings path.");
        AgentActivityValidation.CreateSafeDirectory(directory);
        if (File.Exists(_file) && (File.GetAttributes(_file) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Settings file must not be a reparse point.");
        string temporary = Path.Combine(directory, $".agent-settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(bytes);
            File.Move(temporary, _file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static AgentActivitySettings Empty(string? warning) =>
        new(Array.Empty<string>(), new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), warning);

    private sealed record SettingsFile(IReadOnlyList<string> EnabledIntegrations, IReadOnlyDictionary<string, string> Workspaces);
}
