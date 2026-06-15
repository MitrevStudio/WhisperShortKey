namespace whispershortkey.Services;

public interface ITranscriptionProvider
{
    Task<string> TranscribeAsync(string audioFilePath, string apiKey, string model, string language);
}
