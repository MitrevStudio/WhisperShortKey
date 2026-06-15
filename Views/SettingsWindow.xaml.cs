using System.Windows;
using System.Windows.Controls;
using whispershortkey.Services;

namespace whispershortkey.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
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
            "gemini-3.5-flash",
            "gemini-3.1-pro-preview",
            "gemini-3-flash-preview",
            "gemini-3.1-flash-lite",
            "gemini-2.5-flash",
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

        _initialized = true;
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
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Capture current edits for the active provider.
        _apiKeys[_currentProvider] = ApiKeyBox.Password;
        _models[_currentProvider] = ModelCombo.SelectedItem?.ToString() ?? GetDefaultModel(_currentProvider);

        var s = _settingsService.Settings;
        s.Provider = _currentProvider;

        foreach (var kvp in _apiKeys)
            s.SetApiKey(kvp.Key, kvp.Value);
        foreach (var kvp in _models)
            s.SetModel(kvp.Key, kvp.Value);

        s.Language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        s.UseClipboardFallback = ClipboardFallbackCheck.IsChecked ?? true;

        _settingsService.Save();
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
