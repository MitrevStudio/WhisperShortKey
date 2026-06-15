using whispershortkey.Models;

namespace whispershortkey.Services;

public class TranscriptionService
{
    private readonly SettingsService _settings;
    private readonly Dictionary<string, ITranscriptionProvider> _providers;

    public TranscriptionService(SettingsService settings)
    {
        _settings = settings;
        _providers = new Dictionary<string, ITranscriptionProvider>
        {
            ["OpenAI"] = new OpenAITranscriptionProvider(),
            ["Gemini"] = new GeminiTranscriptionProvider()
        };
    }

    public async Task<string> TranscribeAsync(string audioFilePath)
    {
        var s = _settings.Settings;

        var apiKey = s.GetApiKey(s.Provider);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"No API key set for {s.Provider}. Open Settings to add it.");

        if (!_providers.TryGetValue(s.Provider, out var provider))
            provider = _providers["OpenAI"];

        var fallbackModel = s.Provider == "Gemini" ? "gemini-3.5-flash" : "whisper-1";
        var model = s.GetModel(s.Provider, fallbackModel);

        return await provider.TranscribeAsync(audioFilePath, apiKey, model, s.Language);
    }
}
