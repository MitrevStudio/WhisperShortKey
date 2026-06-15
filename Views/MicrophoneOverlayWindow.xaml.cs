using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace whispershortkey.Views;

public partial class MicrophoneOverlayWindow : Window
{
    private readonly Border[] _bars;
    private double _smoothedLevel;

    public MicrophoneOverlayWindow()
    {
        InitializeComponent();
        _bars = new[] { Bar1, Bar2, Bar3, Bar4, Bar5, Bar6, Bar7, Bar8 };
        ApplyTheme(IsSystemLightTheme());
    }

    private void ApplyTheme(bool light)
    {
        // Neutral surfaces mirror the WinUI/Fluent light & dark tokens so the
        // overlay matches the Settings window (which uses ThemeMode="System").
        if (light)
        {
            PanelBorder.Background = Brush("#F9F9F9");
            PanelBorder.BorderBrush = Brush("#E5E5E5");
            StatusText.Foreground = Brush("#1A1A1A");
            HintText.Foreground = Brush("#8A8A8A");
            WaveContainer.Background = Brush("#EDEDED");
        }
        else
        {
            PanelBorder.Background = Brush("#F22C2C2C");
            PanelBorder.BorderBrush = Brush("#3D3D3D");
            StatusText.Foreground = Brush("#FFFFFF");
            HintText.Foreground = Brush("#9A9A9A");
            WaveContainer.Background = Brush("#272727");
        }

        var accent = GetAccentBrush();
        foreach (var bar in _bars)
            bar.Background = accent;
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

    private static SolidColorBrush Brush(string hex)
    {
        return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    }

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
        if (source?.CompositionTarget != null)
        {
            var transform = source.CompositionTarget.TransformFromDevice;
            var wpfPt = transform.Transform(new System.Windows.Point(cursorPos.X, cursorPos.Y));
            Left = Math.Max(0, wpfPt.X - ActualWidth / 2);
            Top = Math.Max(0, wpfPt.Y - ActualHeight - 30);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    public void UpdateAudioLevel(float level)
    {
        Dispatcher.Invoke(() =>
        {
            var clamped = Math.Clamp(level, 0f, 1f);
            _smoothedLevel = _smoothedLevel * 0.55 + clamped * 0.45;

            var boosted = Math.Clamp(_smoothedLevel * 2.8, 0.0, 1.0);
            var weights = new[] { 0.35, 0.55, 0.78, 1.0, 0.92, 0.7, 0.5, 0.32 };

            for (var i = 0; i < _bars.Length; i++)
            {
                var normalized = Math.Clamp(boosted * weights[i], 0.0, 1.0);
                _bars[i].Height = 4 + normalized * 18;
                _bars[i].Opacity = 0.35 + normalized * 0.65;
            }
        });
    }

    public void SetStatus(string text)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
        });
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
