using System.Net.Http;
using System.Text.Json;

namespace whispershortkey.Services;

public class OpenAITranscriptionProvider : ITranscriptionProvider
{
    private readonly HttpClient _http = new();

    public async Task<string> TranscribeAsync(string audioFilePath, string apiKey, string model, string language)
    {
        using var form = new MultipartFormDataContent();
        var audioBytes = await File.ReadAllBytesAsync(audioFilePath);
        form.Add(new ByteArrayContent(audioBytes), "file", Path.GetFileName(audioFilePath));
        form.Add(new StringContent(model), "model");

        if (!string.IsNullOrEmpty(language) && language != "auto")
        {
            form.Add(new StringContent(language), "language");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions")
        {
            Content = form
        };
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("text").GetString() ?? "";
    }
}
