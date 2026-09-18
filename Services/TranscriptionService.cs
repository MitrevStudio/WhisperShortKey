using System.Net.Http;

namespace whispershortkey.Services;

public class TranscriptionService
{
    // One client for the whole process. PooledConnectionLifetime matters less for socket
    // exhaustion than for staleness: this app stays resident for weeks and would otherwise
    // pin DNS results from whenever it happened to start.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        // Per-attempt budgets are enforced with a linked CTS instead. HttpClient.Timeout
        // surfaces as a TaskCanceledException indistinguishable from our own cancellation.
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly TimeSpan BaseAttemptTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxAttemptTimeout = TimeSpan.FromMinutes(10);

    private readonly SettingsService _settings;
    private readonly Dictionary<string, ITranscriptionProvider> _providers;

    public TranscriptionService(SettingsService settings)
    {
        _settings = settings;
        _providers = new Dictionary<string, ITranscriptionProvider>(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI"] = new OpenAITranscriptionProvider(Http),
            ["Gemini"] = new GeminiTranscriptionProvider(Http)
        };
    }

    public static string DefaultModelFor(string provider) =>
        string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase) ? "gemini-3.5-flash" : "whisper-1";

    public static long MaxAudioBytesFor(string provider) =>
        string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase)
            ? GeminiTranscriptionProvider.MaxInlineBytes
            : OpenAITranscriptionProvider.MaxUploadBytes;

    /// <summary>
    /// How many whole minutes fit inside the provider's limit. Rounded down, with a second
    /// of slack: recording stops on a 50ms buffer boundary and the WAV carries a header, so
    /// landing exactly on the limit would still be rejected.
    /// </summary>
    public static int MaxRecordingMinutesFor(string provider)
    {
        var usable = MaxAudioBytesFor(provider)
                     - AudioRecorderService.WavHeaderBytes
                     - AudioRecorderService.BytesPerSecond;

        return Math.Max(1, (int)(usable / AudioRecorderService.BytesPerSecond / 60));
    }

    /// <summary>Transcribes using whatever the settings currently say.</summary>
    public Task<string> TranscribeAsync(string audioFilePath, CancellationToken ct = default)
    {
        var s = _settings.Settings;
        return TranscribeAsync(audioFilePath, s.Provider, s.GetModel(s.Provider, DefaultModelFor(s.Provider)), s.Language, ct);
    }

    /// <summary>
    /// Transcribes with an explicit provider/model/language, so a queued job keeps the
    /// settings it was recorded under even if the user changes them in the meantime.
    /// The API key is deliberately not part of that snapshot - it is read fresh here, so
    /// retrying after a 401 picks up the key the user just corrected.
    /// </summary>
    public async Task<string> TranscribeAsync(
        string audioFilePath, string provider, string model, string language, CancellationToken ct)
    {
        if (!_providers.TryGetValue(provider, out var impl))
            throw TranscriptionException.Configuration($"Unknown provider \"{provider}\". Open Settings and pick one.");

        var apiKey = _settings.Settings.GetApiKey(provider);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw TranscriptionException.Configuration($"No API key set for {provider}. Open Settings to add it.");

        if (string.IsNullOrWhiteSpace(model))
            model = DefaultModelFor(provider);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(AttemptTimeout(audioFilePath));

        try
        {
            return await impl.TranscribeAsync(audioFilePath, apiKey, model, language, attempt.Token);
        }
        catch (Exception ex)
        {
            // ct, not attempt.Token: this is what separates "we asked to stop" from "it timed out".
            throw TranscriptionException.FromException(ex, provider, ct);
        }
    }

    /// <summary>
    /// A fixed 100s default drops long recordings on ordinary uplinks - a 10 minute WAV is
    /// 25 MB, and 33 MB again once Gemini base64-encodes it inline.
    /// </summary>
    private static TimeSpan AttemptTimeout(string audioFilePath)
    {
        var seconds = 0.0;
        try
        {
            seconds = AudioRecorderService.EstimateDurationSeconds(new FileInfo(audioFilePath).Length);
        }
        catch (IOException)
        {
            // Unreadable here just means no scaling; the attempt itself will report the problem.
        }

        var budget = BaseAttemptTimeout + TimeSpan.FromSeconds(seconds * 2);
        return budget > MaxAttemptTimeout ? MaxAttemptTimeout : budget;
    }
}
