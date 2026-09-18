using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using whispershortkey.Models;
using whispershortkey.Services;
using whispershortkey.Views;
using WpfApp = System.Windows.Application;

namespace whispershortkey;

public partial class App : WpfApp
{
    private const string InstanceMutexName = "Local\\VoiceTray.SingleInstance";

    /// <summary>Anything shorter than this is a mis-tap, not speech.</summary>
    private const double MinimumRecordingSeconds = 0.3;

    private TrayService? _tray;
    private HotkeyService? _hotkey;
    private AudioRecorderService? _recorder;
    private AudioPlaybackService? _playback;
    private TranscriptionService? _transcription;
    private TextDeliveryService? _delivery;
    private KeyboardInjectionService? _injector;
    private SettingsService? _settings;
    private JobStore? _jobStore;
    private TranscriptionQueue? _queue;
    private MicrophoneOverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private MainWindow? _mainWindow;
    private Mutex? _instanceMutex;

    // Per-recording, not shared state: a second recording started while the first is still
    // being transcribed must not steal the first one's destination.
    private WindowTarget _pendingTarget;
    private string? _pendingJobId;

    /// <summary>Which job the overlay is currently reporting on.</summary>
    private string? _overlayJobId;

    // The cap in force for the recording that is running, and whether it came from the
    // provider rather than the user's own setting. Read only when the cap is hit.
    private int _recordingCapMinutes;
    private bool _capIsProviderLimit;

    private bool _disposed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Two instances would share the records directory and transcribe - and type - everything twice.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show(
                "VoiceTray is already running. Look for the microphone icon in the system tray.",
                "VoiceTray", MessageBoxButton.OK, MessageBoxImage.Information);
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _settings = new SettingsService();
        _injector = new KeyboardInjectionService();
        _delivery = new TextDeliveryService(_injector);
        _transcription = new TranscriptionService(_settings);
        _jobStore = new JobStore();
        _queue = new TranscriptionQueue(_jobStore, _transcription, _delivery, _settings);
        _playback = new AudioPlaybackService();

        _recorder = new AudioRecorderService();
        _recorder.AudioLevelChanged += OnAudioLevelChanged;
        _recorder.RecordingStopped += OnRecordingStopped;
        _recorder.RecordingCancelled += OnRecordingCancelled;
        _recorder.MaxDurationReached += OnMaxDurationReached;

        // EnsureHandle instead of Show/Hide: the old pair flashed a 1x1 window on every launch.
        _mainWindow = new MainWindow();
        var hwnd = new WindowInteropHelper(_mainWindow).EnsureHandle();

        _hotkey = new HotkeyService();
        _hotkey.HotkeyPressed += OnHotkeyPressed;
        _hotkey.CancelPressed += OnCancelHotkey;
        var hotkeyRegistered = _hotkey.Initialize(hwnd);

        _tray = new TrayService(Dispatcher);
        _tray.SettingsRequested += OnSettingsRequested;
        _tray.HistoryRequested += OnHistoryRequested;
        _tray.ExitRequested += OnExitRequested;
        _tray.ToggleRecordingRequested += OnToggleRecording;
        _tray.RetryJobRequested += id => _queue!.RequestRetry(id);
        _tray.RetryAllRequested += () => _queue!.RequestRetryAll();
        _tray.DiscardAllRequested += () => _queue!.DiscardAll();
        _tray.BalloonClicked += id => _queue!.RequestRetry(id);

        _queue.JobsChanged += OnJobsChanged;
        _queue.JobStarted += OnJobStarted;
        _queue.JobCompleted += OnJobCompleted;
        _queue.JobFailed += OnJobFailed;
        _queue.JobEmpty += OnJobEmpty;

        var waiting = _queue.Start();

        if (!hotkeyRegistered)
        {
            _tray.ShowTooltip(
                "Ctrl+Space is already taken by another app, so the hotkey is inactive. Double-click this icon to record.",
                ToolTipIcon.Warning);
        }
        else if (waiting > 0)
        {
            _tray.ShowTooltip(
                waiting == 1
                    ? "One recording from your last session is waiting. Right-click here to retry it."
                    : $"{waiting} recordings from your last session are waiting. Right-click here to retry them.",
                ToolTipIcon.Warning);
        }
    }

    private void OnHotkeyPressed() => ToggleRecording();
    private void OnToggleRecording() => ToggleRecording();

    private void ToggleRecording()
    {
        if (_recorder!.IsRecording)
        {
            _recorder.StopRecording();
            _overlay?.SetTranscribing(_settings!.Settings.Provider);
            return;
        }

        try
        {
            var settings = _settings!.Settings;

            _pendingTarget = KeyboardInjectionService.CaptureForegroundWindow();
            _pendingJobId = Guid.NewGuid().ToString("N");

            var deviceId = AudioRecorderService.ResolveDeviceId(settings.MicrophoneName, settings.MicrophoneDeviceId);

            // Whichever ceiling is lower wins, so a recording can never grow past what the
            // selected provider will accept - that used to surface only at upload time, as
            // "Recording too large", after the user had already said everything.
            var userCap = Math.Clamp(settings.MaxRecordingMinutes, 1, 60);
            var providerCap = TranscriptionService.MaxRecordingMinutesFor(settings.Provider);
            _recordingCapMinutes = Math.Min(userCap, providerCap);
            _capIsProviderLimit = providerCap < userCap;

            var maxDuration = TimeSpan.FromMinutes(_recordingCapMinutes);

            // Nothing may be typed into the window the user is about to dictate into.
            _queue!.SuspendDelivery();

            _recorder.StartRecording(deviceId, _jobStore!.NewAudioPath(_pendingJobId), maxDuration);
            _hotkey?.EnableCancelHotkey();
            ShowListeningOverlay();
        }
        catch (Exception ex)
        {
            _queue!.ResumeDelivery();
            _pendingJobId = null;
            _pendingTarget = default;
            _tray?.ShowTooltip($"Recording failed: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void OnCancelHotkey()
    {
        if (_recorder is { IsRecording: true })
            _recorder.CancelRecording();
    }

    private void OnMaxDurationReached()
    {
        var reason = _capIsProviderLimit
            ? $"the longest {_settings!.Settings.Provider} accepts in one go"
            : "your limit";

        _tray?.ShowTooltip(
            $"Recording stopped at {_recordingCapMinutes} min - {reason}. Transcribing what was captured.",
            ToolTipIcon.Warning);
    }

    /// <summary>A fresh panel at the cursor for a new recording.</summary>
    private void ShowListeningOverlay()
    {
        CloseOverlay();
        _overlayJobId = _pendingJobId;
        CreateOverlay().SetListening(TimeSpan.FromMinutes(_recordingCapMinutes));
    }

    private MicrophoneOverlayWindow CreateOverlay()
    {
        if (_overlay != null) return _overlay;

        _overlay = new MicrophoneOverlayWindow();
        _overlay.Closed += (_, _) => _overlay = null;
        _overlay.RetryRequested += OnOverlayRetry;
        _overlay.DismissRequested += CloseOverlay;
        _overlay.Show();
        return _overlay;
    }

    private void CloseOverlay()
    {
        // Synchronous when already on the UI thread: ShowListeningOverlay closes and then
        // immediately recreates, and a queued close would leave the old panel in place -
        // still positioned at the previous cursor location.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(CloseOverlay);
            return;
        }

        _overlay?.Close();
        _overlay = null;
    }

    private void OnOverlayRetry(string jobId)
    {
        CloseOverlay();
        _queue?.RequestRetry(jobId);
    }

    private void OnAudioLevelChanged(float level, double elapsedSeconds) =>
        _overlay?.UpdateAudioLevel(level, elapsedSeconds);

    private void OnRecordingStopped(string filePath, Exception? error, double durationSeconds)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _hotkey?.DisableCancelHotkey();
            _queue!.ResumeDelivery();

            var jobId = _pendingJobId ?? Path.GetFileNameWithoutExtension(filePath);
            var target = _pendingTarget;
            _pendingJobId = null;
            _pendingTarget = default;

            // A device disappearing mid-recording still leaves usable audio, because the
            // writer is closed properly - so warn, but transcribe what was captured.
            if (error != null)
                _tray?.ShowTooltip($"Recording ended early: {error.Message}", ToolTipIcon.Warning);

            if (durationSeconds < MinimumRecordingSeconds)
            {
                CloseOverlay();
                TryDelete(filePath);
                return;
            }

            // The overlay stays up, now showing progress, until the job finishes or fails.
            _overlayJobId = jobId;
            _overlay?.SetTranscribing(_settings!.Settings.Provider);

            _queue.Enqueue(jobId, filePath, target, durationSeconds);
        });
    }

    private void OnRecordingCancelled()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _hotkey?.DisableCancelHotkey();
            CloseOverlay();
            _queue!.ResumeDelivery();
            _overlayJobId = null;
            _pendingJobId = null;
            _pendingTarget = default;
        });
    }

    private void OnJobsChanged()
    {
        var failures = _queue!.SnapshotFailures()
            .Select(job => new FailureSummary(
                job.Id,
                job.CreatedAt,
                job.DurationSeconds,
                job.LastErrorShort ?? "Failed",
                job.LastError ?? "",
                job.AttemptCount,
                job.HasText))
            .ToList();

        _tray?.SetFailures(failures);
    }

    private void OnJobStarted(TranscriptionJob job)
    {
        Dispatcher.InvokeAsync(() =>
        {
            // Never take the panel away from someone who is mid-sentence.
            if (_recorder is { IsRecording: true }) return;

            _overlayJobId = job.Id;
            CreateOverlay().SetTranscribing(job.Provider);
        });
    }

    private void OnJobCompleted(TranscriptionJob job, DeliveryOutcome outcome)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_overlayJobId == job.Id && _overlay?.State != OverlayState.Listening)
            {
                CloseOverlay();
                _overlayJobId = null;
            }

            if (outcome == DeliveryOutcome.ClipboardOnly)
            {
                _tray?.ShowTooltip(
                    $"Transcript ready - press Ctrl+V to paste it ({job.CreatedAt:HH:mm} recording).",
                    ToolTipIcon.Info);
            }
        });
    }

    private void OnJobFailed(TranscriptionJob job)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var title = job.LastErrorShort ?? "Transcription failed";
            var detail = string.IsNullOrWhiteSpace(job.LastError)
                ? "The recording is kept - retry it from here or from the tray."
                : job.LastError;

            // Showing it in the panel is the whole point; a balloon on top would be noise.
            // The exception is a failure that arrives while the user is recording again.
            if (_recorder is { IsRecording: true })
            {
                _tray?.ShowTooltip($"{detail}\n\nClick here to retry.", ToolTipIcon.Error, job.Id);
                return;
            }

            _overlayJobId = job.Id;
            CreateOverlay().SetError(job.Id, title, detail, canRetry: true);
        });
    }

    private void OnJobEmpty(TranscriptionJob job)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_overlayJobId == job.Id)
            {
                CloseOverlay();
                _overlayJobId = null;
            }

            _tray?.ShowTooltip("No speech detected in that recording.", ToolTipIcon.Info);
        });
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

    private void OnHistoryRequested()
    {
        if (_historyWindow == null)
        {
            _historyWindow = new HistoryWindow(_queue!, _playback!);
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Show();
        }
        else
        {
            if (_historyWindow.WindowState == WindowState.Minimized)
                _historyWindow.WindowState = WindowState.Normal;
            _historyWindow.Activate();
        }
    }

    private async void OnExitRequested()
    {
        if (_disposed)
        {
            Shutdown();
            return;
        }

        _disposed = true;

        _hotkey?.Dispose();
        _recorder?.Dispose();
        _playback?.Dispose();

        // Gives an in-flight transcription a bounded moment to unwind; whatever it was
        // working on is persisted as pending and offered again next launch.
        if (_queue != null)
            await _queue.DisposeAsync();

        _delivery?.Dispose();
        _overlay?.Close();
        _settingsWindow?.Close();
        _historyWindow?.Close();
        _tray?.Dispose();
        _mainWindow?.Close();

        ReleaseInstanceMutex();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_disposed)
        {
            _disposed = true;
            _hotkey?.Dispose();
            _recorder?.Dispose();
            _playback?.Dispose();
            _delivery?.Dispose();
            _overlay?.Close();
            _settingsWindow?.Close();
            _historyWindow?.Close();
            _tray?.Dispose();
            ReleaseInstanceMutex();
        }

        base.OnExit(e);
    }

    private void ReleaseInstanceMutex()
    {
        if (_instanceMutex == null) return;

        try
        {
            _instanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owning thread; disposing still releases the handle.
        }

        _instanceMutex.Dispose();
        _instanceMutex = null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
