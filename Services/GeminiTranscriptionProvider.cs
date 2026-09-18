using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace whispershortkey.Services;

public class GeminiTranscriptionProvider : ITranscriptionProvider
{
    private const string ProviderName = "Gemini";

    /// <summary>
    /// Gemini caps the whole request at 20 MB - prompt and all files together - and base64
    /// inflates the audio by 4/3. 14 MB of audio encodes to ~18.7 MB, which leaves real room
    /// for the JSON envelope. This is our ceiling, not the model's: Gemini itself handles
    /// 9.5 hours of audio per prompt when the file goes through the Files API instead.
    /// </summary>
    public const long MaxInlineBytes = 14_000_000;

    private readonly HttpClient _http;

    private static readonly Dictionary<string, string> LanguageNames = new()
    {
        ["bg"] = "Bulgarian",
        ["en"] = "English",
        ["es"] = "Spanish",
        ["fr"] = "French",
        ["de"] = "German",
        ["it"] = "Italian",
        ["pt"] = "Portuguese",
        ["ru"] = "Russian",
        ["ja"] = "Japanese",
        ["zh"] = "Chinese",
        ["tr"] = "Turkish"
    };

    public GeminiTranscriptionProvider(HttpClient http) => _http = http;

    public async Task<string> TranscribeAsync(
        string audioFilePath, string apiKey, string model, string language, CancellationToken ct)
    {
        var audioBytes = await File.ReadAllBytesAsync(audioFilePath, ct);

        if (audioBytes.LongLength > MaxInlineBytes)
        {
            throw TranscriptionException.TooLarge(
                $"The recording is {audioBytes.LongLength / 1_000_000} MB. Gemini caps a request at 20 MB once the audio is encoded, which is about 7 minutes at this quality. Record in shorter bursts, or switch to OpenAI for longer ones.");
        }

        var prompt = "Transcribe this audio recording accurately. " +
                     "Respond with ONLY the transcribed text, without any commentary, labels, or quotation marks.";

        if (!string.IsNullOrEmpty(language) && language != "auto" &&
            LanguageNames.TryGetValue(language, out var langName))
        {
            prompt += $" The spoken language is {langName}.";
        }

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt },
                        new { inline_data = new { mime_type = "audio/wav", data = Convert.ToBase64String(audioBytes) } }
                    }
                }
            },
            generationConfig = new { temperature = 0.0 }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };

        // Header, not a query string: the URI shows up in exception messages and crash dumps,
        // and those messages are surfaced in the tray menu.
        request.Headers.Add("x-goog-api-key", apiKey);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        // Check the status before parsing: a proxy 502 answers with HTML, not JSON.
        if (!response.IsSuccessStatusCode)
            throw TranscriptionException.FromResponse(response, responseJson, ProviderName);

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            // Blocked or empty. Not an error - there is simply nothing to type.
            return "";
        }

        // A safety-blocked candidate carries a finishReason but no content at all.
        if (!candidates[0].TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                sb.Append(text.GetString());
        }

        return sb.ToString().Trim();
    }
}
