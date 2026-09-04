using System.IO;
using System.Text.Json;

namespace OriginalTaskbar;

internal sealed class AppSettings
{
    public bool ShowClock { get; set; } = true;
    public bool ShowClockSeconds { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;
    public bool ShowBattery { get; set; } = true;
    public bool ShowDesktopLabel { get; set; } = true;
    public bool ShowRunningApps { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OriginalTaskbar", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to load settings, using defaults:\n{ex}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to save settings:\n{ex}");
        }
    }
}
