using System.Text.Json.Serialization;

namespace whispershortkey.Models;

[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
public enum JobStatus
{
    /// <summary>Waiting for an attempt: freshly recorded, retried, or re-transcribed.</summary>
    Pending,
    Transcribing,

    /// <summary>Text in hand, delivery deferred because a recording is in progress.</summary>
    ReadyForDelivery,
    Delivering,

    /// <summary>Waiting for the user to decide. Nothing happens on its own.</summary>
    Failed,

    /// <summary>Transcribed and delivered. Stays on disk as history.</summary>
    Done,

    /// <summary>Discarded by the user, or evicted by the retention caps.</summary>
    Abandoned
}

/// <summary>
/// One recording, from the moment it stops until it is deleted. Persisted next to its WAV
/// so a failure - or a crash - never costs the user the audio, and kept afterwards so the
/// transcript and the recording remain reviewable.
/// </summary>
public sealed class TranscriptionJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AudioPath { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public double DurationSeconds { get; set; }

    // Snapshot of the settings this was recorded under, so a retry hours later does not
    // silently switch provider or model. The API key is deliberately NOT part of it:
    // it is read fresh at attempt time, so retrying after a 401 picks up the corrected key.
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Language { get; set; } = "auto";

    public JobStatus Status { get; set; } = JobStatus.Pending;
    public int AttemptCount { get; set; }

    /// <summary>Full provider message. Shown in the overlay and as the menu item's tooltip.</summary>
    public string? LastError { get; set; }

    /// <summary>Compact label for the tray menu, e.g. "Invalid API key".</summary>
    public string? LastErrorShort { get; set; }

    public int? LastStatusCode { get; set; }
    public bool LastErrorTransient { get; set; }

    /// <summary>
    /// Set once the audio has been transcribed. A job that has this and still failed only
    /// needs re-delivering, so retrying it costs nothing at the API.
    /// </summary>
    public string? ResultText { get; set; }

    [JsonIgnore]
    public bool HasText => !string.IsNullOrEmpty(ResultText);

    /// <summary>
    /// False once the audio has been pruned by the retention cap - the transcript outlives
    /// the recording, so history rows stay readable but can no longer be played or redone.
    /// </summary>
    [JsonIgnore]
    public bool HasAudio => !string.IsNullOrEmpty(AudioPath) && File.Exists(AudioPath);

    [JsonIgnore]
    public bool IsFinished => Status is JobStatus.Done or JobStatus.Failed;
}
