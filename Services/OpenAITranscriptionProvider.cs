using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace whispershortkey.Services;

public class OpenAITranscriptionProvider : ITranscriptionProvider
{
    private const string ProviderName = "OpenAI";

    /// <summary>
    /// "Files can be up to 25 MB." Decimal, not MiB: reading it as MiB would let us send
    /// 26.2 MB and take a 413 right at the boundary.
    /// </summary>
    public const long MaxUploadBytes = 25_000_000;

    private readonly HttpClient _http;

    public OpenAITranscriptionProvider(HttpClient http) => _http = http;

    public async Task<string> TranscribeAsync(
        string audioFilePath, string apiKey, string model, string language, CancellationToken ct)
    {
        var audioBytes = await File.ReadAllBytesAsync(audioFilePath, ct);

        // Fail locally rather than spending minutes uploading something that gets rejected.
        if (audioBytes.LongLength > MaxUploadBytes)
        {
            throw TranscriptionException.TooLarge(
                $"The recording is {audioBytes.LongLength / 1_000_000} MB; OpenAI accepts files up to 25 MB, which is about 13 minutes at this quality. Record in shorter bursts.");
        }

        using var form = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audioContent, "file", Path.GetFileName(audioFilePath));
        form.Add(new StringContent(model), "model");

        if (!string.IsNullOrEmpty(language) && language != "auto")
            form.Add(new StringContent(language), "language");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions")
        {
            Content = form
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        // Read the body first: EnsureSuccessStatusCode would throw away the only useful part.
        if (!response.IsSuccessStatusCode)
            throw TranscriptionException.FromResponse(response, json, ProviderName);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
        {
            throw new TranscriptionException(
                TranscriptionFailureKind.MalformedResponse,
                "Bad response",
                $"OpenAI replied without a transcript. The model \"{model}\" may not support plain transcription.",
                isTransient: false);
        }

        return text.GetString() ?? "";
    }
}
