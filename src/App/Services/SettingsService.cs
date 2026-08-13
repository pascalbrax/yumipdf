using System.Text.Json;

namespace Pdf26.Services;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON under the user's local app-data folder.
/// A plain file (rather than the WinRT <c>ApplicationData</c> API) keeps this independent
/// from packaged-vs-unpackaged activation quirks.
/// </summary>
public sealed class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDF26", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt or unreadable settings file: fall back to defaults rather than block startup.
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
