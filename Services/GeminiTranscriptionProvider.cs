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
    /// for the JSON envelope. This is our ceiling, not the model's: sent through the Files
    /// API instead, Gemini handles far longer recordings.
    /// </summary>
    public const long MaxInlineBytes = 14_000_000;

    /// <summary>The dedicated speech-to-text model, which is served by a different API.</summary>
    public const string TranscribeModelPrefix = "gemini-3.5-transcribe";

    private const string InteractionsUrl = "https://generativelanguage.googleapis.com/v1beta/interactions";
    private const string GenerateContentUrlFormat = "https://generativelanguage.googleapis.com/v1beta/models/{0}:generateContent";

    /// <summary>Pins the Interactions API request/response shape this code was written against.</summary>
    private const string InteractionsApiRevision = "2026-05-20";

    private readonly HttpClient _http;

    /// <summary>Prompt wording for the general-purpose models, which are only told in prose.</summary>
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

    /// <summary>BCP-47 codes, which is what the transcription model's language_codes wants.</summary>
    private static readonly Dictionary<string, string> LanguageTags = new()
    {
        ["bg"] = "bg-BG",
        ["en"] = "en-US",
        ["es"] = "es-ES",
        ["fr"] = "fr-FR",
        ["de"] = "de-DE",
        ["it"] = "it-IT",
        ["pt"] = "pt-PT",
        ["ru"] = "ru-RU",
        ["ja"] = "ja-JP",
        ["zh"] = "zh-CN",
        ["tr"] = "tr-TR"
    };

    public GeminiTranscriptionProvider(HttpClient http) => _http = http;

    public static bool IsTranscribeModel(string model) =>
        model.StartsWith(TranscribeModelPrefix, StringComparison.OrdinalIgnoreCase);

    public async Task<string> TranscribeAsync(
        string audioFilePath, string apiKey, string model, string language, CancellationToken ct)
    {
        var audioBytes = await File.ReadAllBytesAsync(audioFilePath, ct);

        if (audioBytes.LongLength > MaxInlineBytes)
        {
            throw TranscriptionException.TooLarge(
                $"The recording is {audioBytes.LongLength / 1_000_000} MB. Gemini caps a request at 20 MB once the audio is encoded, which is about 7 minutes at this quality. Record in shorter bursts, or switch to OpenAI for longer ones.");
        }

        var audioBase64 = Convert.ToBase64String(audioBytes);

        // The dedicated transcription model lives behind the Interactions API, not
        // generateContent, so the two paths share nothing but the key and the size limit.
        return IsTranscribeModel(model)
            ? await TranscribeViaInteractionsAsync(audioBase64, apiKey, model, language, ct)
            : await TranscribeViaGenerateContentAsync(audioBase64, apiKey, model, language, ct);
    }

    /// <summary>
    /// The purpose-built speech-to-text path. No prompt: this model only transcribes, so
    /// there is nothing to instruct and no commentary to strip out of the answer.
    /// </summary>
    private async Task<string> TranscribeViaInteractionsAsync(
        string audioBase64, string apiKey, string model, string language, CancellationToken ct)
    {
        // "smart" cleans up filler words and applies punctuation and capitalisation, which is
        // what dictated text wants. An empty language list means auto-detect.
        object transcriptionConfig = LanguageTags.TryGetValue(language, out var tag) && language != "auto"
            ? new { language_codes = new[] { tag }, mode = "smart" }
            : new { mode = "smart" };

        var requestBody = new
        {
            model,
            input = new object[]
            {
                new { type = "audio", data = audioBase64, mime_type = "audio/wav" }
            },
            generation_config = new { transcription_config = transcriptionConfig }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, InteractionsUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };

        request.Headers.Add("x-goog-api-key", apiKey);
        request.Headers.Add("Api-Revision", InteractionsApiRevision);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw TranscriptionException.FromResponse(response, responseJson, ProviderName);

        using var doc = JsonDocument.Parse(responseJson);
        return ReadInteractionText(doc.RootElement);
    }

    /// <summary>
    /// Pulls the transcript out of an Interactions reply. The shape is
    /// steps[] -> content[] -> text, for steps of type "model_output".
    ///
    /// The SDKs expose this as a flat "output_text" property, but the REST body has no such
    /// field - silence simply comes back with no steps at all, which is not an error.
    /// </summary>
    private static string ReadInteractionText(JsonElement root)
    {
        // Honoured first in case a future revision does flatten it, as the SDKs do.
        if (root.TryGetProperty("output_text", out var flat) && flat.ValueKind == JsonValueKind.String)
            return flat.GetString()?.Trim() ?? "";

        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            return "";

        var sb = new StringBuilder();
        foreach (var step in steps.EnumerateArray())
        {
            if (!step.TryGetProperty("type", out var stepType) ||
                stepType.GetString() != "model_output" ||
                !step.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var itemType) &&
                    itemType.GetString() == "text" &&
                    item.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    sb.Append(text.GetString());
                }
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// The general-purpose models, which are chat models being asked to transcribe and so
    /// need the instruction spelled out in a prompt.
    /// </summary>
    private async Task<string> TranscribeViaGenerateContentAsync(
        string audioBase64, string apiKey, string model, string language, CancellationToken ct)
    {
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
                        new { inline_data = new { mime_type = "audio/wav", data = audioBase64 } }
                    }
                }
            },
            generationConfig = new { temperature = 0.0 }
        };

        var url = string.Format(GenerateContentUrlFormat, model);

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
