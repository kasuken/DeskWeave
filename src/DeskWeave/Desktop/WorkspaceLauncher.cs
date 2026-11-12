using System.Diagnostics;
using DeskWeave.Core.AgentActivity;

namespace DeskWeave.Desktop;

internal static class WorkspaceLauncher
{
    public static void Open(string destination)
    {
        Validate(destination);
        var start = new ProcessStartInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = false,
        };
        start.ArgumentList.Add(destination);
        Process.Start(start);
    }

    internal static void Validate(string destination)
    {
        if (!AgentActivitySettingsStore.IsValidWorkspaceDestination(destination) || destination.Contains(','))
        {
            throw new ArgumentException("Choose an existing local workspace folder.");
        }
    }
}
