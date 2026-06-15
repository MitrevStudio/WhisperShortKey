using System.Text.Json.Serialization;

namespace whispershortkey.Models;

public class AppSettings
{
    public string Provider { get; set; } = "OpenAI";
    public Dictionary<string, string> ApiKeys { get; set; } = new();
    public Dictionary<string, string> Models { get; set; } = new();
    public string Language { get; set; } = "auto";
    public bool UseClipboardFallback { get; set; } = true;

    [JsonIgnore]
    public string CurrentApiKey => GetApiKey(Provider);

    public string GetApiKey(string provider) =>
        ApiKeys.TryGetValue(provider, out var key) ? key : "";

    public void SetApiKey(string provider, string key) => ApiKeys[provider] = key;

    public string GetModel(string provider, string fallback) =>
        Models.TryGetValue(provider, out var model) && !string.IsNullOrWhiteSpace(model)
            ? model
            : fallback;

    public void SetModel(string provider, string model) => Models[provider] = model;
}
