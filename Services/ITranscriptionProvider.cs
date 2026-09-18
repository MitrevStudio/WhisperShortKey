namespace whispershortkey.Services;

public interface ITranscriptionProvider
{
    /// <summary>
    /// Transcribes a WAV file. Implementations throw <see cref="TranscriptionException"/>
    /// for everything a caller might want to act on; anything else is a bug.
    /// </summary>
    Task<string> TranscribeAsync(
        string audioFilePath, string apiKey, string model, string language, CancellationToken ct);
}
