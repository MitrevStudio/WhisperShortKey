using System.Windows;
using System.Windows.Controls;
using whispershortkey.Services;

namespace whispershortkey.Views;

public partial class SettingsWindow : Window
{
    private static readonly int[] LengthPresets = [1, 2, 3, 5, 10, 15, 20, 30, 45, 60];

    private readonly SettingsService _settingsService;
    private readonly List<(int Id, string Name)> _micDevices;
    private readonly Dictionary<string, string[]> _providerModels = new()
    {
        ["OpenAI"] =
        [
            "whisper-1",
            "gpt-4o-mini-transcribe",
            "gpt-4o-transcribe",
            "gpt-4o-transcribe-diarize"
        ],
        ["Gemini"] =
        [
            // First entry doubles as the fallback when nothing is saved, so the fastest one
            // leads. Measured on this machine, 2.5-flash answered in about half the time of
            // 3.5-transcribe and a third of 2.5-pro, with an identical transcript.
            "gemini-2.5-flash",

            // The dedicated speech-to-text model. Not the quickest on short dictation, but
            // the only one here that does diarization, word timestamps and hour-long audio.
            "gemini-3.5-transcribe",

            "gemini-3.5-flash",
            "gemini-3.1-pro-preview",
            "gemini-3-flash-preview",
            "gemini-3.1-flash-lite",
            "gemini-2.5-flash-lite",
            "gemini-2.5-pro"
        ]
    };

    // Working copies, so switching provider keeps each provider's key/model.
    private readonly Dictionary<string, string> _apiKeys = new();
    private readonly Dictionary<string, string> _models = new();
    private string _currentProvider = "OpenAI";
    private bool _initialized;

    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _micDevices = AudioRecorderService.GetInputDevices();
        if (_micDevices.Count == 0)
            _micDevices.Add((0, "Default"));
        LoadSettings();
    }

    private void LoadSettings()
    {
        var s = _settingsService.Settings;

        foreach (var provider in _providerModels.Keys)
        {
            _apiKeys[provider] = s.GetApiKey(provider);
            _models[provider] = s.GetModel(provider, _providerModels[provider][0]);
        }

        _currentProvider = s.Provider;
        ProviderCombo.SelectedIndex = s.Provider == "Gemini" ? 1 : 0;

        ApiKeyBox.Password = _apiKeys[_currentProvider];
        PopulateModels(_currentProvider, _models[_currentProvider]);

        foreach (ComboBoxItem item in LanguageCombo.Items)
        {
            if (item.Tag?.ToString() == s.Language)
            {
                item.IsSelected = true;
                break;
            }
        }
        if (LanguageCombo.SelectedIndex < 0)
            LanguageCombo.SelectedIndex = 0;

        ClipboardFallbackCheck.IsChecked = s.UseClipboardFallback;

        MicDeviceCombo.Items.Clear();
        var resolvedId = AudioRecorderService.ResolveDeviceId(s.MicrophoneName, s.MicrophoneDeviceId);
        var selectedMic = 0;
        for (var i = 0; i < _micDevices.Count; i++)
        {
            MicDeviceCombo.Items.Add(_micDevices[i].Name);
            if (_micDevices[i].Id == resolvedId)
                selectedMic = i;
        }
        MicDeviceCombo.SelectedIndex = selectedMic;

        PopulateLengths(s.MaxRecordingMinutes);
        UpdateLengthHint();

        _initialized = true;
    }

    private void PopulateLengths(int selectedMinutes)
    {
        // Keep a hand-edited value from settings.json rather than silently rounding it away.
        var values = LengthPresets.ToList();
        if (!values.Contains(selectedMinutes))
        {
            values.Add(selectedMinutes);
            values.Sort();
        }

        MaxLengthCombo.Items.Clear();
        var selectedIndex = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var minutes = values[i];
            MaxLengthCombo.Items.Add(new ComboBoxItem
            {
                Content = minutes == 1 ? "1 minute" : $"{minutes} minutes",
                Tag = minutes
            });

            if (minutes == selectedMinutes)
                selectedIndex = i;
        }

        MaxLengthCombo.SelectedIndex = selectedIndex;
    }

    /// <summary>
    /// Spells out the provider's own ceiling, which is where the surprise used to be: a
    /// 30 minute setting means nothing if Gemini stops accepting audio after 7.
    /// </summary>
    private void UpdateLengthHint()
    {
        var cap = TranscriptionService.MaxRecordingMinutesFor(_currentProvider);
        LengthHintText.Text =
            $"{_currentProvider} accepts about {cap} min per recording - anything longer is capped at that.";
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;

        // Stash current edits for the provider we are leaving.
        _apiKeys[_currentProvider] = ApiKeyBox.Password;
        _models[_currentProvider] = ModelCombo.SelectedItem?.ToString() ?? GetDefaultModel(_currentProvider);

        _currentProvider = ProviderCombo.SelectedIndex == 1 ? "Gemini" : "OpenAI";

        ApiKeyBox.Password = _apiKeys.TryGetValue(_currentProvider, out var key) ? key : "";
        PopulateModels(_currentProvider, _models.TryGetValue(_currentProvider, out var m) ? m : null);
        UpdateLengthHint();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Capture current edits for the active provider.
        _apiKeys[_currentProvider] = ApiKeyBox.Password;
        _models[_currentProvider] = ModelCombo.SelectedItem?.ToString() ?? GetDefaultModel(_currentProvider);

        // Edit a copy: the live instance may be read by a transcription on another thread.
        var s = _settingsService.Settings.Clone();
        s.Provider = _currentProvider;

        foreach (var kvp in _apiKeys)
            s.SetApiKey(kvp.Key, kvp.Value);
        foreach (var kvp in _models)
            s.SetModel(kvp.Key, kvp.Value);

        s.Language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        s.UseClipboardFallback = ClipboardFallbackCheck.IsChecked ?? true;

        var micIndex = MicDeviceCombo.SelectedIndex;
        var mic = micIndex >= 0 && micIndex < _micDevices.Count ? _micDevices[micIndex] : _micDevices[0];
        s.MicrophoneName = mic.Name;
        s.MicrophoneDeviceId = mic.Id;

        if ((MaxLengthCombo.SelectedItem as ComboBoxItem)?.Tag is int maxMinutes)
            s.MaxRecordingMinutes = maxMinutes;

        _settingsService.Save(s);
        Close();
    }

    private void PopulateModels(string provider, string? selectedModel)
    {
        if (!_providerModels.TryGetValue(provider, out var models))
            models = _providerModels["OpenAI"];

        ModelCombo.Items.Clear();
        foreach (var model in models)
            ModelCombo.Items.Add(model);

        var modelToSelect = models.Contains(selectedModel) ? selectedModel : models[0];
        ModelCombo.SelectedItem = modelToSelect;
    }

    private string GetDefaultModel(string provider)
    {
        return _providerModels.TryGetValue(provider, out var models) ? models[0] : _providerModels["OpenAI"][0];
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
