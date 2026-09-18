using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using whispershortkey.Models;
using whispershortkey.Services;

namespace whispershortkey.Views;

public partial class HistoryWindow : Window
{
    private const int PreviewChars = 160;

    private static readonly SolidColorBrush DoneBrush = Frozen("#3FA34D");
    private static readonly SolidColorBrush FailedBrush = Frozen("#D9534F");
    private static readonly SolidColorBrush WorkingBrush = Frozen("#C9821B");

    private readonly TranscriptionQueue _queue;
    private readonly AudioPlaybackService _playback;
    private readonly ObservableCollection<HistoryRow> _rows = [];

    private string _search = "";

    public HistoryWindow(TranscriptionQueue queue, AudioPlaybackService playback)
    {
        InitializeComponent();

        _queue = queue;
        _playback = playback;

        HistoryList.ItemsSource = _rows;

        _queue.JobsChanged += OnJobsChanged;
        _playback.PlaybackStopped += OnPlaybackStopped;

        Refresh();
        UpdateButtons();
    }

    private void OnJobsChanged() => Dispatcher.InvokeAsync(Refresh);

    private void OnPlaybackStopped() => Dispatcher.InvokeAsync(UpdateButtons);

    private void Refresh()
    {
        var selectedId = (HistoryList.SelectedItem as HistoryRow)?.Id;

        var all = _queue.SnapshotHistory();
        var matching = all.Where(Matches).Select(HistoryRow.From).ToList();

        _rows.Clear();
        foreach (var row in matching)
            _rows.Add(row);

        CountText.Text = _search.Length == 0
            ? $"{all.Count} recording(s)"
            : $"{matching.Count} of {all.Count}";

        var restored = selectedId == null ? null : _rows.FirstOrDefault(r => r.Id == selectedId);
        HistoryList.SelectedItem = restored ?? _rows.FirstOrDefault();

        // SelectionChanged does not fire when the resolved selection is unchanged.
        ShowDetail(HistoryList.SelectedItem as HistoryRow);
        UpdateButtons();
    }

    private bool Matches(TranscriptionJob job)
    {
        if (_search.Length == 0) return true;

        return (job.ResultText?.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || (job.LastErrorShort?.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || job.Provider.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
            || job.Model.Contains(_search, StringComparison.CurrentCultureIgnoreCase);
    }

    private void ShowDetail(HistoryRow? row)
    {
        if (row == null)
        {
            DetailHeader.Text = "";
            DetailMeta.Text = "";
            DetailText.Text = "";
            return;
        }

        DetailHeader.Text = row.TimeText;
        DetailMeta.Text = row.MetaText + (row.HasAudio ? "" : "  -  audio no longer stored");
        DetailText.Text = row.DetailText;
    }

    private void UpdateButtons()
    {
        var row = HistoryList.SelectedItem as HistoryRow;
        var playingThis = row != null && _playback.IsPlaying && _playback.CurrentPath == row.AudioPath;

        PlayButton.Content = playingThis ? "Stop" : "Play";
        PlayButton.IsEnabled = row?.HasAudio == true;
        CopyButton.IsEnabled = row?.HasText == true;
        RetranscribeButton.IsEnabled = row?.CanRetranscribe == true;
        DeleteButton.IsEnabled = row?.CanDelete == true;
        ClearButton.IsEnabled = _rows.Count > 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowDetail(HistoryList.SelectedItem as HistoryRow);
        UpdateButtons();
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;

        if (_playback.IsPlaying && _playback.CurrentPath == row.AudioPath)
        {
            _playback.Stop();
        }
        else if (!_playback.Play(row.AudioPath))
        {
            System.Windows.MessageBox.Show(this,
                "That recording could not be played. It may have been removed.",
                "VoiceTray", MessageBoxButton.OK, MessageBoxImage.Warning);
            Refresh();
            return;
        }

        UpdateButtons();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow { HasText: true } row) return;

        try
        {
            // Retrying matters: clipboard managers and Office routinely hold it open.
            System.Windows.Forms.Clipboard.SetDataObject(row.FullText, copy: true, retryTimes: 10, retryDelay: 50);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(this,
                "The clipboard is being held by another app. Try again in a moment.",
                "VoiceTray", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RetranscribeButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;

        if (!_queue.RequestRetranscribe(row.Id))
        {
            System.Windows.MessageBox.Show(this,
                "That recording can no longer be transcribed - its audio is gone.",
                "VoiceTray", MessageBoxButton.OK, MessageBoxImage.Warning);
            Refresh();
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;

        var confirm = System.Windows.MessageBox.Show(this,
            "Delete this recording and its transcript? This cannot be undone.",
            "VoiceTray", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        if (_playback.CurrentPath == row.AudioPath)
            _playback.Stop();

        _queue.Discard(row.Id);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(this,
            "Delete every recording and transcript in the history? This cannot be undone.",
            "VoiceTray", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        _playback.Stop();
        _queue.ClearHistory();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _queue.JobsChanged -= OnJobsChanged;
        _playback.PlaybackStopped -= OnPlaybackStopped;
        _playback.Stop();
        base.OnClosed(e);
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>One row, flattened for binding - the window has no other view model.</summary>
    public sealed class HistoryRow
    {
        public required string Id { get; init; }
        public required string AudioPath { get; init; }
        public required string TimeText { get; init; }
        public required string MetaText { get; init; }
        public required string StatusText { get; init; }
        public required SolidColorBrush StatusBrush { get; init; }
        public required string Preview { get; init; }
        public required string DetailText { get; init; }
        public required string FullText { get; init; }
        public required bool HasAudio { get; init; }
        public required bool HasText { get; init; }
        public required bool CanRetranscribe { get; init; }
        public required bool CanDelete { get; init; }

        public static HistoryRow From(TranscriptionJob job)
        {
            var inFlight = job.Status is JobStatus.Transcribing or JobStatus.Delivering
                or JobStatus.Pending or JobStatus.ReadyForDelivery;

            var (status, brush) = job.Status switch
            {
                JobStatus.Done => ("Done", DoneBrush),
                JobStatus.Failed => ($"Failed - {job.LastErrorShort ?? "error"}", FailedBrush),
                JobStatus.Transcribing => ("Transcribing…", WorkingBrush),
                JobStatus.Delivering => ("Delivering…", WorkingBrush),
                _ => ("Waiting…", WorkingBrush)
            };

            var text = job.ResultText ?? "";
            var body = job.HasText
                ? text
                : job.LastError ?? (inFlight ? "Working on it…" : "No transcript.");

            var meta = $"{Math.Max(1, (int)Math.Round(job.DurationSeconds))}s  -  {job.Provider} {job.Model}  -  {job.Language}";
            if (!job.HasAudio)
                meta += "  -  no audio";

            return new HistoryRow
            {
                Id = job.Id,
                AudioPath = job.AudioPath,
                TimeText = job.CreatedAt.ToLocalTime().ToString("ddd d MMM  HH:mm"),
                MetaText = meta,
                StatusText = status,
                StatusBrush = brush,
                Preview = Shorten(body),
                DetailText = body,
                FullText = text,
                HasAudio = job.HasAudio,
                HasText = job.HasText,
                CanRetranscribe = job.HasAudio && job.Status is not (JobStatus.Transcribing or JobStatus.Delivering),
                CanDelete = job.Status is not (JobStatus.Transcribing or JobStatus.Delivering)
            };
        }

        private static string Shorten(string text)
        {
            var flat = text.ReplaceLineEndings(" ").Trim();
            return flat.Length <= PreviewChars ? flat : flat[..PreviewChars] + "…";
        }
    }
}
