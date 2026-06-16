using NAudio.Wave;

namespace whispershortkey.Services;

public class AudioRecorderService : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _waveWriter;
    private string? _outputPath;
    private volatile bool _isRecording;

    public event Action<float>? AudioLevelChanged;
    public event Action<string>? RecordingStopped;

    public bool IsRecording => _isRecording;

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

    public void StartRecording(int deviceId = 0)
    {
        if (_isRecording) return;

        _outputPath = Path.Combine(Path.GetTempPath(), $"voicetray_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50,
            DeviceNumber = deviceId
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        _waveWriter = new WaveFileWriter(_outputPath, _waveIn.WaveFormat);
        _waveIn.StartRecording();
        _isRecording = true;
    }

    public void StopRecording()
    {
        if (!_isRecording || _waveIn == null) return;
        _waveIn.StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);

        float max = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i);
            float normalized = Math.Abs(sample) / 32768f;
            if (normalized > max) max = normalized;
        }

        AudioLevelChanged?.Invoke(max);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _isRecording = false;

        _waveWriter?.Dispose();
        _waveWriter = null;

        _waveIn?.Dispose();
        _waveIn = null;

        if (_outputPath != null)
            RecordingStopped?.Invoke(_outputPath);
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
