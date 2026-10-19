using System.Text.Json;

namespace MicMix;

public sealed class Settings
{
    public string? MicId { get; set; }
    public string? OutputId { get; set; }
    public string? MonitorId { get; set; }
    public bool MonitorEnabled { get; set; }
    public string? AppName { get; set; }
    public string Preset { get; set; } = "Off";
    public int MicVolume { get; set; } = 100;
    public int AppVolume { get; set; } = 60;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicMix", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // Corrupt or unreadable settings just fall back to defaults.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
