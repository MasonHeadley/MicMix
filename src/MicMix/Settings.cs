using System.Text.Json;
using MicMix.Audio;

namespace MicMix;

public sealed class VoiceProfile
{
    public string Name { get; set; } = "";
    public VoiceSettings Voice { get; set; } = VoiceSettings.Neutral;
}

public sealed class Settings
{
    public string Theme { get; set; } = "cyber";
    public string? MicId { get; set; }
    public string? OutputId { get; set; }
    public string? MirrorId { get; set; }
    public bool Mirror { get; set; }
    public string? AppName { get; set; }

    public int MicGain { get; set; }        // dB, 0..24
    public int MicVolume { get; set; } = 100; // %, 0..150
    public int AppGain { get; set; }        // dB, 0..24
    public int AppVolume { get; set; } = 60;  // %, 0..150
    public bool MicMuted { get; set; }
    public bool AppMuted { get; set; }

    public string ActivePreset { get; set; } = "Off";
    public VoiceSettings Voice { get; set; } = VoiceSettings.Neutral;
    public List<VoiceProfile> Profiles { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicMix", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
        }
    }
}
