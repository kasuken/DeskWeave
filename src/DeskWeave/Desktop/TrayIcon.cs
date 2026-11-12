using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Diagnostics;
using DeskWeave.Core.Display;
using DeskWeave.Core.GitHubStatus;
using Forms = System.Windows.Forms;
using static DeskWeave.Interop.NativeMethods;

namespace DeskWeave.Desktop;

/// <summary>Notification area icon: the only interactive surface, since widgets are click-through.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _image;
    private readonly WidgetHost _host;

    public TrayIcon(WidgetHost host, Action exit)
    {
        _host = host;
        _image = CreateIcon();

        var toggle = new Forms.ToolStripMenuItem("Show widgets") { Checked = true, CheckOnClick = true };
        toggle.CheckedChanged += (_, _) =>
        {
            if (toggle.Checked)
            {
                _host.Show();
            }
            else
            {
                _host.Hide();
            }
        };

        var startup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = StartupRegistration.IsEnabled, CheckOnClick = true };
        startup.CheckedChanged += (_, _) => StartupRegistration.Set(startup.Checked);

        (MonitorMode Mode, Forms.ToolStripMenuItem Item)[] screens =
        [
            (MonitorMode.Primary, new Forms.ToolStripMenuItem("Primary screen only")),
            (MonitorMode.Secondary, new Forms.ToolStripMenuItem("Secondary screen only")),
            (MonitorMode.Both, new Forms.ToolStripMenuItem("Both screens")),
        ];
        foreach (var (mode, item) in screens)
        {
            item.Click += (_, _) => _host.Mode = mode;
        }

        var statusMenu = new Forms.ToolStripMenuItem("Service status");
        var statusWarning = new Forms.ToolStripMenuItem { Enabled = false, Visible = false };
        var quotaHistoryWarning = new Forms.ToolStripMenuItem { Enabled = false, Visible = false };
        statusMenu.DropDownItems.Add(statusWarning);
        var providerItems = new List<(StatusProvider Provider, Forms.ToolStripMenuItem Item)>();
        foreach (var provider in StatusProviders.All)
        {
            var item = new Forms.ToolStripMenuItem($"Show {provider.Name}");
            item.Click += (_, _) =>
            {
                try
                {
                    _host.SetStatusEnabled(provider.Id, !_host.IsStatusEnabled(provider.Id));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Forms.MessageBox.Show($"Could not save service status settings: {ex.Message}",
                        "DeskWeave", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                }
            };
            providerItems.Add((provider, item));
            statusMenu.DropDownItems.Add(item);
        }

        statusMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        foreach (var provider in StatusProviders.All)
        {
            statusMenu.DropDownItems.Add($"Open {provider.Name} status page", null, (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(provider.StatusPage) { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    Forms.MessageBox.Show($"Could not open the status page: {ex.Message}",
                        "DeskWeave", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                }
            });
        }

        var agentMenu = new Forms.ToolStripMenuItem("Agent activity");
        var agentToggle = new Forms.ToolStripMenuItem("Enable Claude Code hooks");
        var agentStatus = new Forms.ToolStripMenuItem { Enabled = false };
        var agentWarning = new Forms.ToolStripMenuItem { Enabled = false, Visible = false };
        var workspaceMenu = new Forms.ToolStripMenuItem("Open workspace folder");
        agentToggle.Click += (_, _) =>
        {
            try
            {
                _host.SetAgentEnabled("claude-code", !_host.IsAgentEnabled("claude-code"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Forms.MessageBox.Show($"Could not save agent activity settings: {ex.Message}",
                    "DeskWeave", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            }
        };
        agentMenu.DropDownItems.Add(agentToggle);
        agentMenu.DropDownItems.Add(agentStatus);
        agentMenu.DropDownItems.Add(agentWarning);
        agentMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        agentMenu.DropDownItems.Add("Edit integration settings", null, (_, _) =>
        {
            try
            {
                WidgetHost.PrepareAgentSettingsFile();
                var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
                start.ArgumentList.Add(WidgetHost.AgentSettingsPath);
                Process.Start(start);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
            {
                Forms.MessageBox.Show($"Could not open agent settings: {ex.Message}",
                    "DeskWeave", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            }
        });
        agentMenu.DropDownItems.Add("Reload integration settings", null, (_, _) => _host.ReloadAgentSettings());
        agentMenu.DropDownItems.Add(workspaceMenu);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripLabel("DeskWeave") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(toggle);
        menu.Items.Add("Refresh layout", null, (_, _) => _host.ScheduleLayout());
        menu.Items.Add(startup);
        menu.Items.Add(statusMenu);
        menu.Items.Add(agentMenu);
        menu.Items.Add(quotaHistoryWarning);
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var (_, item) in screens)
        {
            menu.Items.Add(item);
        }

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        // The saved preference stays checked even while a missing secondary screen forces a primary fallback;
        // choosing an unavailable mode is only blocked when it is not already the preference.
        menu.Opening += (_, _) =>
        {
            foreach (var (mode, item) in screens)
            {
                item.Checked = mode == _host.Mode;
                item.Enabled = WidgetHost.IsModeAvailable(mode);
            }
            foreach (var (provider, item) in providerItems)
            {
                item.Checked = _host.IsStatusEnabled(provider.Id);
            }
            statusWarning.Text = _host.StatusSettingsWarning ?? string.Empty;
            statusWarning.Visible = _host.StatusSettingsWarning is not null;
            quotaHistoryWarning.Text = _host.QuotaHistoryWarning ?? string.Empty;
            quotaHistoryWarning.Visible = _host.QuotaHistoryWarning is not null;
            agentToggle.Checked = _host.IsAgentEnabled("claude-code");
            var source = _host.AgentSources.FirstOrDefault(source => source.Id == "claude-code");
            agentStatus.Text = source is null
                ? agentToggle.Checked ? "Waiting for hook metadata" : "Disabled"
                : source.Status;
            agentWarning.Text = _host.AgentSettingsWarning ?? source?.Warning ?? string.Empty;
            agentWarning.Visible = agentWarning.Text.Length > 0;
            workspaceMenu.DropDownItems.Clear();
            foreach (var (alias, destination) in _host.AgentWorkspaces.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var workspaceItem = new Forms.ToolStripMenuItem(alias);
                workspaceItem.Click += (_, _) =>
                {
                    try
                    {
                        WorkspaceLauncher.Open(destination);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                                   or Win32Exception or InvalidOperationException)
                    {
                        Forms.MessageBox.Show($"Could not open the workspace folder: {ex.Message}",
                            "DeskWeave", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                    }
                };
                workspaceMenu.DropDownItems.Add(workspaceItem);
            }
            workspaceMenu.Enabled = workspaceMenu.DropDownItems.Count > 0;
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "DeskWeave",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => toggle.Checked = !toggle.Checked;
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var panel = new SolidBrush(ColorTranslator.FromHtml("#100D1D"));
            using var cyan = new Pen(ColorTranslator.FromHtml("#38E8FF"), 3);
            using var magenta = new Pen(ColorTranslator.FromHtml("#FF4FB8"), 3);
            graphics.FillRectangle(panel, 1, 1, 30, 30);
            graphics.DrawRectangle(cyan, 2, 2, 28, 28);
            graphics.DrawLines(magenta, [new PointF(7, 20), new PointF(12, 13), new PointF(17, 18), new PointF(25, 9)]);
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
