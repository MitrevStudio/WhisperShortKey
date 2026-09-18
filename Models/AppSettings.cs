using System.Text.Json.Serialization;

namespace whispershortkey.Models;

public class AppSettings
{
    public string Provider { get; set; } = "OpenAI";
    public Dictionary<string, string> ApiKeys { get; set; } = new();
    public Dictionary<string, string> Models { get; set; } = new();
    public string Language { get; set; } = "auto";

    /// <summary>
    /// WinMM product name of the chosen microphone. Device indexes shift whenever a device
    /// is plugged in or removed, which silently moves recording to the wrong microphone,
    /// so the name is what we actually persist.
    /// </summary>
    public string MicrophoneName { get; set; } = "";

    /// <summary>Legacy index, kept so existing settings files still resolve a device.</summary>
    public int MicrophoneDeviceId { get; set; }

    public bool UseClipboardFallback { get; set; } = true;

    /// <summary>Safety net against a recording left running by accident.</summary>
    public int MaxRecordingMinutes { get; set; } = 10;

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

    /// <summary>
    /// A deep copy, so edits in the Settings window never mutate the instance that a
    /// transcription running on another thread is reading.
    /// </summary>
    public AppSettings Clone() => new()
    {
        Provider = Provider,
        ApiKeys = new Dictionary<string, string>(ApiKeys),
        Models = new Dictionary<string, string>(Models),
        Language = Language,
        MicrophoneName = MicrophoneName,
        MicrophoneDeviceId = MicrophoneDeviceId,
        UseClipboardFallback = UseClipboardFallback,
        MaxRecordingMinutes = MaxRecordingMinutes
    };
}
