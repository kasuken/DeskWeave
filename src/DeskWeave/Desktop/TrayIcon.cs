using System.Drawing;
using System.Drawing.Drawing2D;
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

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripLabel("DeskWeave") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(toggle);
        menu.Items.Add("Refresh layout", null, (_, _) => _host.ScheduleLayout());
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

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
