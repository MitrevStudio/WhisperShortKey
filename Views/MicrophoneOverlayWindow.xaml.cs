using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace whispershortkey.Views;

public enum OverlayState
{
    Listening,
    Transcribing,
    Error
}

public partial class MicrophoneOverlayWindow : Window
{
    private const double BarBaseHeight = 4;
    private const double BarMaxGrowth = 18;
    private const double CursorGap = 30;

    /// <summary>Never start the countdown earlier than this, however long the cap is.</summary>
    private const double MaxWarnSeconds = 30;

    /// <summary>On a short cap, 30s of warning would be most of the recording.</summary>
    private const double WarnFractionOfLimit = 0.25;

    private const int UrgentSeconds = 10;

    private readonly Border[] _bars;
    private readonly DispatcherTimer _busyTimer;

    // Assigned by ApplyTheme, which the constructor always calls.
    private SolidColorBrush _hintBrush = null!;
    private SolidColorBrush _warnBrush = null!;
    private SolidColorBrush _urgentBrush = null!;

    private double _smoothedLevel;
    private double _busyPhase;
    private double _anchorY;
    private bool _anchored;

    private double _limitSeconds;
    private double _warnAtSeconds;

    /// <summary>Last whole second painted, so the text is not rewritten 20 times a second.</summary>
    private int _lastCountdownSecond = -1;

    /// <summary>Raised with the job id when the user clicks Retry on a failure.</summary>
    public event Action<string>? RetryRequested;

    public event Action? DismissRequested;

    public OverlayState State { get; private set; } = OverlayState.Listening;

    public string? JobId { get; private set; }

    public MicrophoneOverlayWindow()
    {
        InitializeComponent();
        _bars = [Bar1, Bar2, Bar3, Bar4, Bar5, Bar6, Bar7, Bar8];

        ApplyTheme(IsSystemLightTheme());

        // Drives the indeterminate sweep while a transcription is in flight; there is no
        // progress to report, so the point is only to show that something is happening.
        _busyTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(60) };
        _busyTimer.Tick += (_, _) => AdvanceBusyAnimation();

        SizeChanged += (_, _) => Reposition();
    }

    // The three state changes run synchronously on the UI thread, which is where every
    // caller already is. Queueing them would let State read stale between two rapid
    // transitions - for instance a job finishing just as the next recording starts.

    /// <summary>
    /// <paramref name="limit"/> is when the recording will stop by itself. The last stretch
    /// of it is counted down in place of the hotkey hint, so there is time to wrap up a
    /// sentence instead of being cut off mid-word.
    /// </summary>
    public void SetListening(TimeSpan limit)
    {
        State = OverlayState.Listening;
        _busyTimer.Stop();

        _limitSeconds = limit.TotalSeconds;
        _warnAtSeconds = Math.Min(MaxWarnSeconds, _limitSeconds * WarnFractionOfLimit);
        _lastCountdownSecond = -1;

        ActivePanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "Listening";
        HintText.Text = "Ctrl+Space";
        HintText.Foreground = _hintBrush;
        ResetBars();
    }

    public void SetTranscribing(string provider)
    {
        State = OverlayState.Transcribing;
        ActivePanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "Transcribing…";
        HintText.Text = provider;
        HintText.Foreground = _hintBrush;
        _smoothedLevel = 0;
        _lastCountdownSecond = -1;
        _busyTimer.Start();
    }

    /// <summary>
    /// Shows a failure and stays there. The recording is safe either way - this only decides
    /// whether it is tried again now or left in the tray for later.
    /// </summary>
    public void SetError(string jobId, string title, string detail, bool canRetry)
    {
        State = OverlayState.Error;
        JobId = jobId;
        _busyTimer.Stop();

        ActivePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;

        ErrorTitle.Text = "⚠  " + title;
        ErrorDetail.Text = detail;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    public void UpdateAudioLevel(float level, double elapsedSeconds)
    {
        // InvokeAsync, not Invoke: this arrives from the audio capture thread 20 times a
        // second, and blocking it whenever the UI thread is busy stalls the recording itself.
        Dispatcher.InvokeAsync(() =>
        {
            if (State != OverlayState.Listening) return;

            UpdateCountdown(elapsedSeconds);

            var clamped = Math.Clamp(level, 0f, 1f);
            _smoothedLevel = _smoothedLevel * 0.55 + clamped * 0.45;

            var boosted = Math.Clamp(_smoothedLevel * 2.8, 0.0, 1.0);
            var weights = new[] { 0.35, 0.55, 0.78, 1.0, 0.92, 0.7, 0.5, 0.32 };

            for (var i = 0; i < _bars.Length; i++)
            {
                var normalized = Math.Clamp(boosted * weights[i], 0.0, 1.0);
                _bars[i].Height = BarBaseHeight + normalized * BarMaxGrowth;
                _bars[i].Opacity = 0.35 + normalized * 0.65;
            }
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// Silent for most of a recording - a twenty second dictation should look exactly as it
    /// did before - and only takes over the hint once the end is actually close.
    /// </summary>
    private void UpdateCountdown(double elapsedSeconds)
    {
        if (_limitSeconds <= 0) return;

        var remaining = _limitSeconds - elapsedSeconds;

        if (remaining > _warnAtSeconds)
        {
            if (_lastCountdownSecond < 0) return;

            _lastCountdownSecond = -1;
            HintText.Text = "Ctrl+Space";
            HintText.Foreground = _hintBrush;
            return;
        }

        var wholeSeconds = Math.Max(0, (int)Math.Ceiling(remaining));
        if (wholeSeconds == _lastCountdownSecond) return;

        _lastCountdownSecond = wholeSeconds;
        HintText.Text = $"{wholeSeconds / 60}:{wholeSeconds % 60:00} left";
        HintText.Foreground = wholeSeconds <= UrgentSeconds ? _urgentBrush : _warnBrush;
    }

    private void AdvanceBusyAnimation()
    {
        _busyPhase += 0.45;

        for (var i = 0; i < _bars.Length; i++)
        {
            // A travelling wave, so the row reads as "working" rather than "stuck".
            var wave = (Math.Sin(_busyPhase - i * 0.7) + 1) / 2;
            _bars[i].Height = BarBaseHeight + wave * BarMaxGrowth * 0.75;
            _bars[i].Opacity = 0.3 + wave * 0.7;
        }
    }

    private void ResetBars()
    {
        foreach (var bar in _bars)
        {
            bar.Height = BarBaseHeight;
            bar.Opacity = 0.5;
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (JobId is { } id)
            RetryRequested?.Invoke(id);
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e) => DismissRequested?.Invoke();

    private void ApplyTheme(bool light)
    {
        // Neutral surfaces mirror the WinUI/Fluent light & dark tokens so the overlay matches
        // the Settings window (which uses ThemeMode="System").
        if (light)
        {
            PanelBorder.Background = Brush("#F9F9F9");
            PanelBorder.BorderBrush = Brush("#E5E5E5");
            StatusText.Foreground = Brush("#1A1A1A");
            WaveContainer.Background = Brush("#EDEDED");
            ErrorTitle.Foreground = Brush("#B3261E");
            ErrorDetail.Foreground = Brush("#4A4A4A");

            _hintBrush = Brush("#8A8A8A");
            _warnBrush = Brush("#B26A00");
            _urgentBrush = Brush("#B3261E");
        }
        else
        {
            PanelBorder.Background = Brush("#F22C2C2C");
            PanelBorder.BorderBrush = Brush("#3D3D3D");
            StatusText.Foreground = Brush("#FFFFFF");
            WaveContainer.Background = Brush("#272727");
            ErrorTitle.Foreground = Brush("#F2B8B5");
            ErrorDetail.Foreground = Brush("#C8C8C8");

            _hintBrush = Brush("#9A9A9A");
            _warnBrush = Brush("#F5B642");
            _urgentBrush = Brush("#F2B8B5");
        }

        HintText.Foreground = _hintBrush;

        var accent = GetAccentBrush();
        foreach (var bar in _bars)
            bar.Background = accent;

        ResetBars();
    }

    private static SolidColorBrush GetAccentBrush()
    {
        try
        {
            // System accent color (same one Fluent controls use).
            var c = SystemParameters.WindowGlassColor;
            if (c.A == 0)
                return Brush("#6C7CFF");
            c.A = 255;
            return new SolidColorBrush(c);
        }
        catch
        {
            return Brush("#6C7CFF");
        }
    }

    private static SolidColorBrush Brush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i != 0;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        var cursorPos = System.Windows.Forms.Cursor.Position;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var transform = source.CompositionTarget.TransformFromDevice;
        var point = transform.Transform(new System.Windows.Point(cursorPos.X, cursorPos.Y));

        _anchorY = point.Y;
        _anchored = true;

        // Clamped on all four sides against the whole virtual desktop: near the top of the
        // screen, or on a secondary monitor, a lower bound alone lets it drift off-screen.
        Left = Clamp(point.X - Width / 2,
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);

        Reposition();
    }

    /// <summary>
    /// Keeps the panel pinned above the anchor as it grows - switching to the error state
    /// makes it taller, and it should expand upward rather than slide over the cursor.
    /// </summary>
    private void Reposition()
    {
        if (!_anchored) return;

        var height = ActualHeight > 0 ? ActualHeight : 78;
        Top = Clamp(_anchorY - height - CursorGap,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - height);
    }

    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Min(Math.Max(value, min), max);

    protected override void OnClosed(EventArgs e)
    {
        _busyTimer.Stop();
        base.OnClosed(e);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
