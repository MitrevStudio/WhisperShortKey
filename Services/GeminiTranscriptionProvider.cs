using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace whispershortkey.Services;

public class GeminiTranscriptionProvider : ITranscriptionProvider
{
    private readonly HttpClient _http = new();

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

    public async Task<string> TranscribeAsync(string audioFilePath, string apiKey, string model, string language)
    {
        var audioBytes = await File.ReadAllBytesAsync(audioFilePath);
        var base64Audio = Convert.ToBase64String(audioBytes);

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
                        new { inline_data = new { mime_type = "audio/wav", data = base64Audio } }
                    }
                }
            },
            generationConfig = new { temperature = 0.0 }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content);
        var responseJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : "Unknown Gemini API error";
            throw new InvalidOperationException(message);
        }

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            return "";

        var parts = candidates[0].GetProperty("content").GetProperty("parts");
        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text))
                sb.Append(text.GetString());
        }

        return sb.ToString().Trim();
    }
}
