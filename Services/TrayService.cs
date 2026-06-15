using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace whispershortkey.Services;

public class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;

    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action? ToggleRecordingRequested;

    public TrayService()
    {
        _contextMenu = new ContextMenuStrip();

        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _contextMenu.Items.Add(settingsItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "VoiceTray",
            Visible = true,
            ContextMenuStrip = _contextMenu
        };

        _notifyIcon.MouseDoubleClick += (_, _) => ToggleRecordingRequested?.Invoke();
    }

    public void ShowTooltip(string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(3000, "VoiceTray", message, icon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }

    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var micColor = Color.FromArgb(230, 80, 80);

        using var brush = new SolidBrush(micColor);
        g.FillEllipse(brush, 8, 2, 16, 16);

        using var pen = new Pen(micColor, 2.5f);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        g.DrawLine(pen, 16, 18, 16, 26);

        using var basePen = new Pen(micColor, 2.5f);
        basePen.StartCap = LineCap.Round;
        basePen.EndCap = LineCap.Round;
        g.DrawArc(basePen, 6, 20, 20, 12, 200, 140);

        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
