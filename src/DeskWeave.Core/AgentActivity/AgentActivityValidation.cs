namespace DeskWeave.Core.AgentActivity;

internal static class AgentActivityValidation
{
    internal const int MaxBytes = 16 * 1024;
    internal const int MaxItems = 128;
    internal const int MaxSettingsItems = 16;

    internal static bool AdapterId(string? value) =>
        value is { Length: > 0 and <= 48 } &&
        value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    internal static bool SessionId(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');

    internal static bool TaskId(string? value) =>
        value is { Length: > 0 and <= 96 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // Deliberately excludes path separators, punctuation used in credentials, and control characters.
    internal static bool DisplayText(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength &&
        value == value.Trim() && value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_');

    internal static bool LocalDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
            return false;

        try
        {
            return HasNoReparseDirectories(path) && Directory.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool HasNoReparseDirectories(string directory)
    {
        foreach (string path in DirectoriesFromRoot(directory))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        return true;
    }

    internal static void CreateSafeDirectory(string directory)
    {
        foreach (string path in DirectoriesFromRoot(directory))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                Directory.CreateDirectory(path);
                attributes = File.GetAttributes(path);
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new IOException("Directory must not contain reparse points.");
        }
    }

    private static IEnumerable<string> DirectoriesFromRoot(string directory)
    {
        var directories = new Stack<string>();
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
            directories.Push(current.FullName);
        return directories;
    }

    internal static bool IsTerminal(AgentTaskState state) => state is AgentTaskState.Completed or AgentTaskState.Failed;
}
