using System.Text.Json;
using whispershortkey.Models;

namespace whispershortkey.Services;

/// <summary>
/// The durable home for every recording: the ones still on their way to text, and the ones
/// already delivered and kept as history.
///
/// Audio is recorded straight into this directory rather than %TEMP%: there is no
/// cross-volume move to fail, a crash mid-transcription leaves the audio already durable,
/// and temp cleaners never see it. One sidecar JSON per record (not a shared index) means
/// the worker and the UI never fight over the same file, and a torn write costs one record
/// instead of the whole list.
/// </summary>
public sealed class JobStore
{
    /// <summary>Transcripts outlive their audio: they are kilobytes, audio is megabytes.</summary>
    public const int MaxRecords = 500;
    public static readonly TimeSpan RecordTtl = TimeSpan.FromDays(90);

    public static readonly TimeSpan AudioTtl = TimeSpan.FromDays(14);
    public const long MaxAudioBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// Failures are never pruned on a timer - the user decides their fate. This is only a
    /// runaway guard for someone who never looks at the tray.
    /// </summary>
    public const int MaxFailedRecords = 50;

    private static readonly string RootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceTray");

    public static string RecordsDirectory { get; } = Path.Combine(RootDirectory, "records");

    private static readonly string LegacyPendingDirectory = Path.Combine(RootDirectory, "pending");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JobStore()
    {
        EnsureDirectory();
        MigrateLegacyPendingDirectory();
    }

    public static void EnsureDirectory()
    {
        try
        {
            Directory.CreateDirectory(RecordsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort, exactly like SettingsService. The first write will report it.
        }
    }

    /// <summary>Records used to live in a directory called "pending", before they outlived delivery.</summary>
    private static void MigrateLegacyPendingDirectory()
    {
        try
        {
            if (!Directory.Exists(LegacyPendingDirectory)) return;

            foreach (var file in Directory.EnumerateFiles(LegacyPendingDirectory))
            {
                var destination = Path.Combine(RecordsDirectory, Path.GetFileName(file));
                if (!File.Exists(destination))
                    File.Move(file, destination);
            }

            Directory.Delete(LegacyPendingDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving the old directory in place is harmless.
        }
    }

    public string NewAudioPath(string jobId) => Path.Combine(RecordsDirectory, jobId + ".wav");

    private static string SidecarPath(string jobId) => Path.Combine(RecordsDirectory, jobId + ".json");

    /// <summary>
    /// Everything on disk, oldest first, with crash recovery applied: records persisted as
    /// in-flight are turned back into failures, and files that lost their partner are swept.
    /// </summary>
    public IReadOnlyList<TranscriptionJob> LoadAll()
    {
        EnsureDirectory();

        var jobs = new List<TranscriptionJob>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Enumerate("*.json"))
        {
            var job = TryRead(path);
            if (job == null || string.IsNullOrEmpty(job.Id))
            {
                TryDelete(path);
                continue;
            }

            // The audio always lives beside the sidecar under the record's id. Trusting the
            // stored absolute path instead would lose the audio of anything written before
            // the directory moved - a roaming profile, a relocated AppData, or the migration
            // above - because it would look like a record whose recording had vanished.
            var canonicalAudio = NewAudioPath(job.Id);
            if (!string.Equals(job.AudioPath, canonicalAudio, StringComparison.OrdinalIgnoreCase))
            {
                job.AudioPath = canonicalAudio;
                Save(job);
            }

            if (job.Status is JobStatus.Transcribing or JobStatus.Delivering)
            {
                // Nothing is in flight at startup, whatever the file says.
                job.Status = JobStatus.Failed;
                job.LastErrorShort = "Interrupted";
                job.LastError = "VoiceTray closed while this recording was being processed.";
                job.LastErrorTransient = true;
                Save(job);
            }

            // No audio and no text means there is nothing to show and nothing to retry.
            if (!job.HasText && !job.HasAudio)
            {
                TryDelete(path);
                continue;
            }

            known.Add(Path.GetFileNameWithoutExtension(path));
            jobs.Add(job);
        }

        // A WAV with no sidecar means the process died mid-recording. Its RIFF header was
        // never closed, so the declared data length is wrong and it will not decode reliably.
        foreach (var wav in Enumerate("*.wav"))
        {
            if (!known.Contains(Path.GetFileNameWithoutExtension(wav)))
                TryDelete(wav);
        }

        // Leftovers from an interrupted atomic write.
        foreach (var temp in Enumerate("*.tmp"))
            TryDelete(temp);

        jobs.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        return jobs;
    }

    public void Save(TranscriptionJob job)
    {
        try
        {
            AtomicFile.WriteAllText(SidecarPath(job.Id), JsonSerializer.Serialize(job, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: losing the sidecar costs the retry, not the session.
        }
    }

    public void Delete(TranscriptionJob job)
    {
        TryDelete(SidecarPath(job.Id));
        DeleteAudio(job);
    }

    public void DeleteAudio(TranscriptionJob job)
    {
        if (!string.IsNullOrEmpty(job.AudioPath))
            TryDelete(job.AudioPath);
    }

    /// <summary>
    /// Applies the retention caps and reports what changed.
    ///
    /// Completed records lose their audio first (14 days, or 1 GB total, oldest first) and
    /// the transcript much later (90 days, or 500 records). Failures are left alone entirely
    /// apart from a runaway guard - they are waiting on the user, not on a timer.
    /// </summary>
    public PruneResult Prune(IEnumerable<TranscriptionJob> jobs)
    {
        var all = jobs.ToList();
        var removed = new List<TranscriptionJob>();
        var trimmed = 0;

        var completed = all.Where(j => j.Status == JobStatus.Done)
                           .OrderBy(j => j.CreatedAt)
                           .ToList();

        // 1. Whole records: age, then count.
        var recordCutoff = DateTimeOffset.Now - RecordTtl;
        foreach (var job in completed.ToList())
        {
            if (job.CreatedAt <= recordCutoff)
                Remove(job);
        }

        while (all.Count > MaxRecords && completed.Count > 0)
            Remove(completed[0]);

        // 2. Audio of the records that survive.
        var audioCutoff = DateTimeOffset.Now - AudioTtl;
        foreach (var job in completed.ToList())
        {
            if (job.CreatedAt <= audioCutoff && job.HasAudio)
                TrimAudio(job);
        }

        var audioBytes = completed.Sum(SizeOf);
        var index = 0;
        while (audioBytes > MaxAudioBytes && index < completed.Count)
        {
            var job = completed[index++];
            if (!job.HasAudio) continue;

            audioBytes -= SizeOf(job);
            TrimAudio(job);
        }

        // 3. Runaway guard on failures.
        var failed = all.Where(j => j.Status == JobStatus.Failed).OrderBy(j => j.CreatedAt).ToList();
        while (failed.Count > MaxFailedRecords)
        {
            Remove(failed[0]);
            failed.RemoveAt(0);
        }

        return new PruneResult(removed, trimmed);

        void Remove(TranscriptionJob job)
        {
            job.Status = JobStatus.Abandoned;
            Delete(job);
            all.Remove(job);
            completed.Remove(job);
            removed.Add(job);
        }

        void TrimAudio(TranscriptionJob job)
        {
            DeleteAudio(job);
            trimmed++;
        }
    }

    private static long SizeOf(TranscriptionJob job)
    {
        try
        {
            return string.IsNullOrEmpty(job.AudioPath) ? 0 : new FileInfo(job.AudioPath).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static TranscriptionJob? TryRead(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<TranscriptionJob>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Enumerate(string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(RecordsDirectory, pattern).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked file will be swept on the next launch.
        }
    }
}

public sealed record PruneResult(IReadOnlyList<TranscriptionJob> Removed, int AudioTrimmed)
{
    public bool ChangedAnything => Removed.Count > 0 || AudioTrimmed > 0;
}
