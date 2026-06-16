using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using whispershortkey.Services;
using whispershortkey.Views;
using WpfApp = System.Windows.Application;

namespace whispershortkey;

public partial class App : WpfApp
{
    private TrayService? _tray;
    private HotkeyService? _hotkey;
    private AudioRecorderService? _recorder;
    private TranscriptionService? _transcription;
    private KeyboardInjectionService? _injector;
    private SettingsService? _settings;
    private MicrophoneOverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private MainWindow? _mainWindow;
    private IntPtr _targetWindowHandle;
    private bool _disposed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = new SettingsService();
        _injector = new KeyboardInjectionService();
        _transcription = new TranscriptionService(_settings);
        _recorder = new AudioRecorderService();
        _recorder.AudioLevelChanged += OnAudioLevelChanged;
        _recorder.RecordingStopped += OnRecordingStopped;

        _mainWindow = new MainWindow();
        _mainWindow.Show();
        var hwnd = new WindowInteropHelper(_mainWindow).Handle;
        _mainWindow.Hide();

        _hotkey = new HotkeyService();
        _hotkey.HotkeyPressed += OnHotkeyPressed;
        _hotkey.Initialize(hwnd);

        _tray = new TrayService();
        _tray.SettingsRequested += OnSettingsRequested;
        _tray.ExitRequested += OnExitRequested;
        _tray.ToggleRecordingRequested += OnToggleRecording;
    }

    private void OnHotkeyPressed() => ToggleRecording();
    private void OnToggleRecording() => ToggleRecording();

    private void ToggleRecording()
    {
        if (_recorder!.IsRecording)
        {
            _recorder.StopRecording();
            _overlay?.SetStatus("Processing...");
        }
        else
        {
            try
            {
                _targetWindowHandle = GetForegroundWindow();
                _recorder.StartRecording(_settings!.Settings.MicrophoneDeviceId);
                ShowOverlay();
            }
            catch (Exception ex)
            {
                _tray?.ShowTooltip($"Recording failed: {ex.Message}", System.Windows.Forms.ToolTipIcon.Error);
            }
        }
    }

    private void ShowOverlay()
    {
        _overlay?.Close();
        _overlay = new MicrophoneOverlayWindow();
        _overlay.Closed += (_, _) => _overlay = null;
        _overlay.Show();
    }

    private void HideOverlay()
    {
        Dispatcher.Invoke(() =>
        {
            _overlay?.Close();
            _overlay = null;
        });
    }

    private void OnAudioLevelChanged(float level)
    {
        _overlay?.UpdateAudioLevel(level);
    }

    private async void OnRecordingStopped(string filePath)
    {
        try
        {
            var text = await _transcription!.TranscribeAsync(filePath);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var finalText = text.Trim();
                await Dispatcher.InvokeAsync(() =>
                {
                    _overlay?.Close();
                    _overlay = null;
                    _injector!.SendText(finalText, _settings!.Settings.UseClipboardFallback, _targetWindowHandle);
                });
            }
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                _tray?.ShowTooltip($"Transcription failed: {ex.Message}", System.Windows.Forms.ToolTipIcon.Error);
            });
        }
        finally
        {
            try { File.Delete(filePath); } catch { }
            HideOverlay();
        }
    }

    private void OnSettingsRequested()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings!);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
        }
    }

    private void OnExitRequested()
    {
        _disposed = true;
        _hotkey?.Dispose();
        _recorder?.Dispose();
        _overlay?.Close();
        _settingsWindow?.Close();
        _tray?.Dispose();
        _mainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_disposed)
        {
            _hotkey?.Dispose();
            _recorder?.Dispose();
            _tray?.Dispose();
        }
        base.OnExit(e);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
