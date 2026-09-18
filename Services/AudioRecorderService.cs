using NAudio.Wave;

namespace whispershortkey.Services;

public class AudioRecorderService : IDisposable
{
    public const int SampleRate = 16000;
    public const int BitsPerSample = 16;
    public const int ChannelCount = 1;
    public const int BytesPerSecond = SampleRate * ChannelCount * BitsPerSample / 8;

    public const int WavHeaderBytes = 44;

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _waveWriter;
    private string? _outputPath;
    private long _bytesWritten;
    private long _maxBytes;
    private volatile bool _isRecording;
    private volatile bool _cancelled;
    private volatile bool _stoppedByLimit;

    /// <summary>Input level (0..1) and how long the recording has been running.</summary>
    public event Action<float, double>? AudioLevelChanged;

    /// <summary>Fires with the file path, the failure that ended it (if any), and its length.</summary>
    public event Action<string, Exception?, double>? RecordingStopped;

    /// <summary>Fires instead of <see cref="RecordingStopped"/> when the user cancels.</summary>
    public event Action? RecordingCancelled;

    public event Action? MaxDurationReached;

    public bool IsRecording => _isRecording;

    /// <summary>How much audio has been captured so far, derived from what has been written.</summary>
    public double ElapsedSeconds => _bytesWritten / (double)BytesPerSecond;

    /// <summary>Length of a WAV we recorded ourselves, derived from its size.</summary>
    public static double EstimateDurationSeconds(long fileSizeBytes) =>
        Math.Max(0, fileSizeBytes - WavHeaderBytes) / (double)BytesPerSecond;

    /// <summary>
    /// Resolves a stored microphone choice to a current device index. Name first: WinMM
    /// indexes shift whenever a device is plugged in or removed, so the stored index alone
    /// silently moves recording to a different microphone.
    /// </summary>
    public static int ResolveDeviceId(string preferredName, int fallbackId)
    {
        var devices = GetInputDevices();
        if (devices.Count == 0) return 0;

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            foreach (var device in devices)
            {
                if (string.Equals(device.Name, preferredName, StringComparison.OrdinalIgnoreCase))
                    return device.Id;
            }
        }

        return devices.Any(d => d.Id == fallbackId) ? fallbackId : devices[0].Id;
    }

    public static List<(int Id, string Name)> GetInputDevices()
    {
        var devices = new List<(int, string)>();
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            devices.Add((i, caps.ProductName));
        }
        return devices;
    }

    public void StartRecording(int deviceId, string outputPath, TimeSpan maxDuration)
    {
        if (_isRecording) return;

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _outputPath = outputPath;
        _bytesWritten = 0;
        _cancelled = false;
        _stoppedByLimit = false;
        _maxBytes = (long)(maxDuration.TotalSeconds * BytesPerSecond);

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, ChannelCount),
            BufferMilliseconds = 50,
            DeviceNumber = deviceId
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        try
        {
            _waveWriter = new WaveFileWriter(_outputPath, _waveIn.WaveFormat);
            _waveIn.StartRecording();
        }
        catch
        {
            // A missing or busy device throws here. Without this the writer would stay open
            // on a zero-length file that nothing ever closes.
            _waveWriter?.Dispose();
            _waveWriter = null;
            _waveIn.Dispose();
            _waveIn = null;
            TryDelete(_outputPath);
            _outputPath = null;
            throw;
        }

        _isRecording = true;
    }

    public void StopRecording()
    {
        if (!_isRecording || _waveIn == null) return;
        _waveIn.StopRecording();
    }

    /// <summary>Stops and throws the audio away - nothing is transcribed.</summary>
    public void CancelRecording()
    {
        if (!_isRecording || _waveIn == null) return;
        _cancelled = true;
        _waveIn.StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
        _bytesWritten += e.BytesRecorded;

        float max = 0;
        for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i);
            float normalized = Math.Abs(sample) / 32768f;
            if (normalized > max) max = normalized;
        }

        AudioLevelChanged?.Invoke(max, ElapsedSeconds);

        // A recording left running by accident would otherwise fill the disk and then cost
        // a large upload; stop it at the cap instead.
        if (_maxBytes > 0 && _bytesWritten >= _maxBytes && !_stoppedByLimit)
        {
            _stoppedByLimit = true;
            _waveIn?.StopRecording();
            MaxDurationReached?.Invoke();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _isRecording = false;

        _waveWriter?.Dispose();
        _waveWriter = null;

        _waveIn?.Dispose();
        _waveIn = null;

        var path = _outputPath;
        _outputPath = null;
        if (path == null) return;

        if (_cancelled)
        {
            TryDelete(path);
            RecordingCancelled?.Invoke();
            return;
        }

        double duration;
        try
        {
            duration = EstimateDurationSeconds(new FileInfo(path).Length);
        }
        catch (IOException)
        {
            duration = _bytesWritten / (double)BytesPerSecond;
        }

        // e.Exception is how NAudio reports a device disappearing mid-recording; ignoring it
        // means paying to upload a truncated file.
        RecordingStopped?.Invoke(path, e.Exception, duration);
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

    public void Dispose()
    {
        if (_isRecording)
            StopRecording();

        _waveWriter?.Dispose();
        _waveWriter = null;

        _waveIn?.Dispose();
        _waveIn = null;
    }
}
