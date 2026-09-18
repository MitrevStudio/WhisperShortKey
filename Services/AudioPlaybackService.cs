using NAudio.Wave;

namespace whispershortkey.Services;

/// <summary>
/// Plays back a stored recording so a transcript can be checked against what was actually
/// said. One clip at a time - starting another stops the previous one.
/// </summary>
public sealed class AudioPlaybackService : IDisposable
{
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private string? _currentPath;

    /// <summary>Fires when playback ends, whether it finished or was stopped.</summary>
    public event Action? PlaybackStopped;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    public string? CurrentPath => _currentPath;

    /// <summary>Starts a clip. Returns false if the file is gone or cannot be decoded.</summary>
    public bool Play(string path)
    {
        Stop();

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;

        try
        {
            _reader = new AudioFileReader(path);
            _output = new WaveOutEvent();
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Init(_reader);
            _output.Play();
            _currentPath = path;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or ArgumentException)
        {
            Cleanup();
            return false;
        }
    }

    public void Stop()
    {
        if (_output == null) return;

        _output.PlaybackStopped -= OnPlaybackStopped;

        try
        {
            _output.Stop();
        }
        catch (InvalidOperationException)
        {
            // Device already gone.
        }

        Cleanup();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Cleanup();
        PlaybackStopped?.Invoke();
    }

    private void Cleanup()
    {
        _output?.Dispose();
        _output = null;

        _reader?.Dispose();
        _reader = null;

        _currentPath = null;
    }

    public void Dispose() => Stop();
}
