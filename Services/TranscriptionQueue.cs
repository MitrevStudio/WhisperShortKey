using System.Threading.Channels;
using whispershortkey.Models;

namespace whispershortkey.Services;

/// <summary>
/// Owns every recording from the moment it stops until the user deletes it: the attempt, the
/// delivery, and afterwards its place in history.
///
/// Single consumer on purpose: exactly one transcription and one delivery in flight, in
/// order, with no reentrancy to reason about. Nothing here retries on its own - a failure
/// parks and waits for the user, which is what they asked for.
/// </summary>
public sealed class TranscriptionQueue : IAsyncDisposable
{
    private readonly JobStore _store;
    private readonly TranscriptionService _transcription;
    private readonly TextDeliveryService _delivery;
    private readonly SettingsService _settings;

    private readonly object _gate = new();
    private readonly Dictionary<string, TranscriptionJob> _jobs = new();

    /// <summary>
    /// Where each job's first delivery should go. In memory only, and dropped the moment a
    /// job fails or is retried: a retry minutes later must never type into a stale window.
    /// </summary>
    private readonly Dictionary<string, WindowTarget> _targets = new();

    private readonly Channel<string> _queue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;

    private TaskCompletionSource _deliveryGate = CompletedGate();

    public event Action? JobsChanged;

    /// <summary>A job just began transcribing - the cue for the overlay's loading state.</summary>
    public event Action<TranscriptionJob>? JobStarted;

    public event Action<TranscriptionJob, DeliveryOutcome>? JobCompleted;
    public event Action<TranscriptionJob>? JobEmpty;
    public event Action<TranscriptionJob>? JobFailed;

    public TranscriptionQueue(
        JobStore store, TranscriptionService transcription, TextDeliveryService delivery, SettingsService settings)
    {
        _store = store;
        _transcription = transcription;
        _delivery = delivery;
        _settings = settings;
    }

    public int FailureCount
    {
        get
        {
            lock (_gate)
                return _jobs.Values.Count(j => j.Status == JobStatus.Failed);
        }
    }

    public IReadOnlyList<TranscriptionJob> SnapshotFailures()
    {
        lock (_gate)
        {
            return _jobs.Values
                .Where(j => j.Status == JobStatus.Failed)
                .OrderByDescending(j => j.CreatedAt)
                .ToList();
        }
    }

    /// <summary>Every record, newest first: history, failures and anything in flight.</summary>
    public IReadOnlyList<TranscriptionJob> SnapshotHistory()
    {
        lock (_gate)
            return _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();
    }

    public TranscriptionJob? Find(string jobId)
    {
        lock (_gate)
            return _jobs.GetValueOrDefault(jobId);
    }

    /// <summary>
    /// Loads whatever survived the last session and starts the worker. Nothing is retried
    /// automatically: the user may be on a different network hours later, and a surprise
    /// burst of API calls is worse than a badge in the tray. Returns how many unfinished
    /// recordings were waiting.
    /// </summary>
    public int Start()
    {
        var loaded = _store.LoadAll();

        // Settle unfinished records into Failed before pruning. Completed ones are history
        // and must be left exactly as they are.
        var waiting = 0;
        foreach (var job in loaded)
        {
            if (job.IsFinished) continue;

            job.Status = JobStatus.Failed;
            job.LastErrorShort ??= "From last session";
            job.LastError ??= "This recording was still waiting when VoiceTray last closed.";
            job.LastErrorTransient = true;
            _store.Save(job);
            waiting++;
        }

        var pruned = _store.Prune(loaded).Removed.Select(j => j.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            foreach (var job in loaded)
            {
                if (!pruned.Contains(job.Id))
                    _jobs[job.Id] = job;
            }
        }

        _worker = Task.Run(RunAsync);
        RaiseJobsChanged();
        return waiting;
    }

    public TranscriptionJob Enqueue(string jobId, string audioPath, WindowTarget target, double durationSeconds)
    {
        var job = new TranscriptionJob
        {
            Id = jobId,
            AudioPath = audioPath,
            DurationSeconds = durationSeconds,
            Status = JobStatus.Pending
        };

        ApplyCurrentSettings(job);

        lock (_gate)
        {
            _jobs[job.Id] = job;
            _targets[job.Id] = target;
        }

        _store.Save(job);
        PruneNow();

        _queue.Writer.TryWrite(job.Id);
        RaiseJobsChanged();
        return job;
    }

    /// <summary>
    /// Tries a failed recording again exactly as it was recorded - same provider and model.
    /// The point is to get past a transient problem or a key the user has just corrected.
    /// </summary>
    public void RequestRetry(string? jobId)
    {
        if (string.IsNullOrEmpty(jobId)) return;

        TranscriptionJob job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out job!) || job.Status != JobStatus.Failed)
                return;

            // A retry is always offered through the clipboard, never typed into a window
            // that may have been closed, moved on from, or replaced since.
            _targets.Remove(jobId);
            job.Status = job.HasText ? JobStatus.ReadyForDelivery : JobStatus.Pending;
        }

        _store.Save(job);
        _queue.Writer.TryWrite(jobId);
        RaiseJobsChanged();
    }

    public void RequestRetryAll()
    {
        foreach (var job in SnapshotFailures())
            RequestRetry(job.Id);
    }

    /// <summary>
    /// Transcribes an existing recording again from scratch, under the settings in force
    /// now - the way to redo something with a different model or language. Needs the audio,
    /// so it is unavailable once retention has trimmed it.
    /// </summary>
    public bool RequestRetranscribe(string jobId)
    {
        TranscriptionJob job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out job!)) return false;
            if (job.Status is JobStatus.Transcribing or JobStatus.Delivering) return false;
            if (!job.HasAudio) return false;

            ApplyCurrentSettings(job);
            job.ResultText = null;
            job.CompletedAt = null;
            job.Status = JobStatus.Pending;
            _targets.Remove(jobId);
        }

        _store.Save(job);
        _queue.Writer.TryWrite(jobId);
        RaiseJobsChanged();
        return true;
    }

    /// <summary>Deletes one record outright - transcript and audio.</summary>
    public void Discard(string jobId)
    {
        TranscriptionJob? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out job)) return;
            if (job.Status is JobStatus.Transcribing or JobStatus.Delivering) return;

            _jobs.Remove(jobId);
            _targets.Remove(jobId);
            job.Status = JobStatus.Abandoned;
        }

        _store.Delete(job);
        RaiseJobsChanged();
    }

    public void DiscardAll()
    {
        foreach (var job in SnapshotFailures())
            Discard(job.Id);
    }

    /// <summary>Wipes everything that is not currently being worked on.</summary>
    public void ClearHistory()
    {
        foreach (var job in SnapshotHistory())
        {
            if (job.Status is not (JobStatus.Transcribing or JobStatus.Delivering))
                Discard(job.Id);
        }
    }

    /// <summary>Called when a recording starts: never type into a window being dictated into.</summary>
    public void SuspendDelivery()
    {
        lock (_gate)
        {
            if (_deliveryGate.Task.IsCompleted)
                _deliveryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void ResumeDelivery()
    {
        lock (_gate)
            _deliveryGate.TrySetResult();
    }

    private void ApplyCurrentSettings(TranscriptionJob job)
    {
        var s = _settings.Settings;
        job.Provider = s.Provider;
        job.Model = s.GetModel(s.Provider, TranscriptionService.DefaultModelFor(s.Provider));
        job.Language = s.Language;
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var jobId in _queue.Reader.ReadAllAsync(_cts.Token))
                await ProcessAsync(jobId, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task ProcessAsync(string jobId, CancellationToken ct)
    {
        TranscriptionJob? job;
        lock (_gate)
            _jobs.TryGetValue(jobId, out job);

        if (job == null || job.Status is JobStatus.Abandoned or JobStatus.Done)
            return;

        if (!job.HasText && !await TranscribeAsync(job, ct))
            return;

        if (string.IsNullOrEmpty(job.ResultText))
        {
            // Silence, or a safety-blocked reply. Nothing to deliver.
            Complete(job);
            JobEmpty?.Invoke(job);
            return;
        }

        await DeliverAsync(job, ct);
    }

    private async Task<bool> TranscribeAsync(TranscriptionJob job, CancellationToken ct)
    {
        SetStatus(job, JobStatus.Transcribing);
        job.AttemptCount++;
        _store.Save(job);

        JobStarted?.Invoke(job);
        RaiseJobsChanged();

        try
        {
            var text = await _transcription.TranscribeAsync(
                job.AudioPath, job.Provider, job.Model, job.Language, ct);

            job.ResultText = text.Trim();
            job.LastError = null;
            job.LastErrorShort = null;
            job.LastStatusCode = null;
            job.LastErrorTransient = false;

            SetStatus(job, JobStatus.ReadyForDelivery);
            _store.Save(job);
            return true;
        }
        catch (Exception ex)
        {
            var failure = TranscriptionException.FromException(ex, job.Provider, ct);

            if (failure.Kind == TranscriptionFailureKind.Cancelled)
            {
                // Not a failure - we asked it to stop. Put it back untouched so the next
                // launch offers it rather than counting a phantom attempt against it.
                job.AttemptCount = Math.Max(0, job.AttemptCount - 1);
                SetStatus(job, JobStatus.Pending);
                _store.Save(job);
                RaiseJobsChanged();
                return false;
            }

            Fail(job, failure.ShortMessage, failure.UserMessage, failure.StatusCode, failure.IsTransient);
            return false;
        }
    }

    private async Task DeliverAsync(TranscriptionJob job, CancellationToken ct)
    {
        Task gate;
        lock (_gate)
            gate = _deliveryGate.Task;

        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Keep the text: the next launch will offer it as "Paste again".
            SetStatus(job, JobStatus.ReadyForDelivery);
            _store.Save(job);
            return;
        }

        WindowTarget target;
        lock (_gate)
            _targets.TryGetValue(job.Id, out target);

        SetStatus(job, JobStatus.Delivering);
        _store.Save(job);

        var text = job.ResultText!;
        var outcome = target.IsEmpty
            ? await _delivery.DeliverToClipboardAsync(text)
            : await _delivery.DeliverToWindowAsync(text, target, _settings.Settings.UseClipboardFallback);

        if (outcome == DeliveryOutcome.Failed)
        {
            Fail(job, "Clipboard blocked",
                "The transcript could not be put on the clipboard - another app is holding it open. Retry to try again.",
                statusCode: null, transient: true);
            return;
        }

        Complete(job);
        JobCompleted?.Invoke(job, outcome);
    }

    /// <summary>
    /// Finishes a record and keeps it: the transcript and, until retention trims it, the
    /// audio stay available in history.
    /// </summary>
    private void Complete(TranscriptionJob job)
    {
        lock (_gate)
        {
            job.Status = JobStatus.Done;
            job.CompletedAt = DateTimeOffset.Now;
            job.LastError = null;
            job.LastErrorShort = null;
            _targets.Remove(job.Id);
        }

        _store.Save(job);
        PruneNow();
        RaiseJobsChanged();
    }

    private void Fail(TranscriptionJob job, string shortMessage, string message, int? statusCode, bool transient)
    {
        lock (_gate)
        {
            job.Status = JobStatus.Failed;
            job.LastErrorShort = shortMessage;
            job.LastError = message;
            job.LastStatusCode = statusCode;
            job.LastErrorTransient = transient;

            // The window it came from is no longer a safe destination.
            _targets.Remove(job.Id);
        }

        _store.Save(job);
        RaiseJobsChanged();
        JobFailed?.Invoke(job);
    }

    private void SetStatus(TranscriptionJob job, JobStatus status)
    {
        lock (_gate)
            job.Status = status;
    }

    private void PruneNow()
    {
        List<TranscriptionJob> snapshot;
        lock (_gate)
            snapshot = _jobs.Values.ToList();

        var result = _store.Prune(snapshot);
        if (result.Removed.Count == 0) return;

        lock (_gate)
        {
            foreach (var job in result.Removed)
            {
                _jobs.Remove(job.Id);
                _targets.Remove(job.Id);
            }
        }
    }

    private void RaiseJobsChanged() => JobsChanged?.Invoke();

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();

        if (_worker != null)
            await Task.WhenAny(_worker, Task.Delay(TimeSpan.FromSeconds(2)));

        _cts.Dispose();
    }
}
