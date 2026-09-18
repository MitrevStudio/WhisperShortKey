using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Threading;

namespace whispershortkey.Services;

/// <summary>What the tray needs to know about one failed recording.</summary>
public sealed record FailureSummary(
    string Id,
    DateTimeOffset CreatedAt,
    double DurationSeconds,
    string ShortError,
    string FullError,
    int AttemptCount,
    bool HasText);

public class TrayService : IDisposable
{
    /// <summary>NotifyIcon.Text throws above this.</summary>
    private const int MaxTooltipChars = 63;

    /// <summary>Balloon text is silently cut off well before this.</summary>
    private const int MaxBalloonChars = 240;

    private readonly Dispatcher _dispatcher;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _failuresRoot;
    private readonly ToolStripSeparator _failuresSeparator;

    private IReadOnlyList<FailureSummary> _failures = [];
    private string? _balloonJobId;
    private bool _iconBadged;
    private bool _disposed;

    public event Action? SettingsRequested;
    public event Action? HistoryRequested;
    public event Action? ExitRequested;
    public event Action? ToggleRecordingRequested;
    public event Action<string>? RetryJobRequested;
    public event Action? RetryAllRequested;
    public event Action? DiscardAllRequested;

    /// <summary>The user clicked an error balloon; the argument is the job it was about.</summary>
    public event Action<string>? BalloonClicked;

    public TrayService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _contextMenu = new ContextMenuStrip();

        _failuresRoot = new ToolStripMenuItem("Failed recordings") { Visible = false };
        _failuresRoot.Click += (_, _) =>
        {
            // Only set when there is exactly one failure, so this item is the action itself.
            if (_failuresRoot.Tag is string id)
                RetryJobRequested?.Invoke(id);
        };

        _failuresSeparator = new ToolStripSeparator { Visible = false };

        var historyItem = new ToolStripMenuItem("History...");
        historyItem.Click += (_, _) => HistoryRequested?.Invoke();

        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _contextMenu.Items.Add(_failuresRoot);
        _contextMenu.Items.Add(_failuresSeparator);
        _contextMenu.Items.Add(historyItem);
        _contextMenu.Items.Add(settingsItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(exitItem);

        // Rebuilding on Opening rather than on every change: the menu is only ever read when
        // it is open, and disposing items while it is displayed throws.
        _contextMenu.Opening += OnMenuOpening;

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(badged: false),
            Text = "VoiceTray",
            Visible = true,
            ContextMenuStrip = _contextMenu
        };

        _notifyIcon.MouseDoubleClick += (_, _) => ToggleRecordingRequested?.Invoke();
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            var id = _balloonJobId;
            if (!string.IsNullOrEmpty(id))
                BalloonClicked?.Invoke(id);
        };
        _notifyIcon.BalloonTipClosed += (_, _) => _balloonJobId = null;
    }

    /// <summary>
    /// Updates what is visible without opening the menu: the hover text and the icon badge.
    /// Safe to call from any thread.
    /// </summary>
    public void SetFailures(IReadOnlyList<FailureSummary> failures)
    {
        if (_disposed) return;

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => SetFailures(failures));
            return;
        }

        // The marshalled call can land after shutdown started.
        if (_disposed) return;

        _failures = failures;

        var count = failures.Count;
        _notifyIcon.Text = Truncate(count == 0 ? "VoiceTray" : $"VoiceTray - {count} failed", MaxTooltipChars);
        ApplyIcon(count > 0);
    }

    /// <summary>
    /// Shows a balloon. Pass <paramref name="balloonJobId"/> to make clicking it retry that
    /// recording - a balloon can be swallowed by Focus Assist, so it supplements the menu
    /// rather than replacing it.
    /// </summary>
    public void ShowTooltip(string message, ToolTipIcon icon = ToolTipIcon.Info, string? balloonJobId = null)
    {
        if (_disposed) return;

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => ShowTooltip(message, icon, balloonJobId));
            return;
        }

        if (_disposed) return;

        _balloonJobId = balloonJobId;
        _notifyIcon.ShowBalloonTip(3000, "VoiceTray", Truncate(message, MaxBalloonChars), icon);
    }

    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        var failures = _failures;

        ClearFailureItems();

        if (failures.Count == 0)
        {
            _failuresRoot.Visible = false;
            _failuresSeparator.Visible = false;
            _failuresRoot.Tag = null;
            return;
        }

        _failuresRoot.Visible = true;
        _failuresSeparator.Visible = true;

        if (failures.Count == 1)
        {
            var only = failures[0];
            _failuresRoot.Text = Describe(only);
            _failuresRoot.ToolTipText = only.FullError;
            _failuresRoot.Tag = only.Id;
            return;
        }

        _failuresRoot.Text = $"Failed recordings ({failures.Count})";
        _failuresRoot.ToolTipText = null;
        _failuresRoot.Tag = null;

        foreach (var failure in failures)
        {
            var item = new ToolStripMenuItem(Describe(failure)) { ToolTipText = failure.FullError };
            var id = failure.Id;
            item.Click += (_, _) => RetryJobRequested?.Invoke(id);
            _failuresRoot.DropDownItems.Add(item);
        }

        _failuresRoot.DropDownItems.Add(new ToolStripSeparator());

        var retryAll = new ToolStripMenuItem("Retry all");
        retryAll.Click += (_, _) => RetryAllRequested?.Invoke();
        _failuresRoot.DropDownItems.Add(retryAll);

        var discardAll = new ToolStripMenuItem("Discard all");
        discardAll.Click += (_, _) => DiscardAllRequested?.Invoke();
        _failuresRoot.DropDownItems.Add(discardAll);
    }

    private void ClearFailureItems()
    {
        var items = _failuresRoot.DropDownItems.Cast<ToolStripItem>().ToList();
        _failuresRoot.DropDownItems.Clear();
        foreach (var item in items)
            item.Dispose();
    }

    private static string Describe(FailureSummary failure)
    {
        var action = failure.HasText ? "Paste again" : "Retry";
        var seconds = Math.Max(1, (int)Math.Round(failure.DurationSeconds));
        return $"{action} - {failure.CreatedAt:HH:mm} - {seconds}s - {failure.ShortError}";
    }

    private void ApplyIcon(bool badged)
    {
        if (badged == _iconBadged && _notifyIcon.Icon != null) return;

        var previous = _notifyIcon.Icon;
        _notifyIcon.Icon = CreateTrayIcon(badged);
        previous?.Dispose();
        _iconBadged = badged;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    public void Dispose()
    {
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }

    private static Icon CreateTrayIcon(bool badged)
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

        if (badged)
        {
            using var badgeFill = new SolidBrush(Color.FromArgb(245, 170, 30));
            using var badgeEdge = new Pen(Color.FromArgb(40, 40, 40), 2f);
            g.FillEllipse(badgeFill, 19, 19, 12, 12);
            g.DrawEllipse(badgeEdge, 19, 19, 12, 12);
        }

        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
