using System.Text.Json;
using whispershortkey.Models;

namespace whispershortkey.Services;

public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceTray", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private volatile AppSettings _settings = new();

    /// <summary>
    /// The current settings. Replaced wholesale by <see cref="Save"/> rather than mutated,
    /// so a reader on the transcription thread always sees a consistent snapshot instead of
    /// a dictionary halfway through being edited by the Settings window.
    /// </summary>
    public AppSettings Settings => _settings;

    public SettingsService()
    {
        Load();
    }

    public void Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            _settings = new AppSettings();
        }
    }

    public void Save(AppSettings updated)
    {
        _settings = updated;

        try
        {
            AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(updated, JsonOptions));
        }
        catch
        {
            // best effort
        }
    }
}
