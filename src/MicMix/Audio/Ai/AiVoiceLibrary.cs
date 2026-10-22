using System.IO.Compression;
using System.Text.Json;

namespace MicMix.Audio.Ai;

/// <summary>One installed AI voice, described by the voice.json next to its model.</summary>
public sealed record AiVoiceInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Credit { get; init; } = "";
    public string Link { get; init; } = "";
    public int SampleRate { get; init; } = 40000;
    /// <summary>Suggested starting pitch shift in semitones.</summary>
    public float Key { get; init; }
    public string ModelPath { get; init; } = "";
}

/// <summary>
/// Finds AI voices under %LocalAppData%\MicMix\AI and installs voice packs.
/// A pack is a zip holding shared\hubert.onnx, shared\rmvpe.onnx and one
/// folder per voice with voice.onnx and voice.json.
/// </summary>
public static class AiVoiceLibrary
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicMix", "AI");

    public static string SharedDir => Path.Combine(Root, "shared");

    public static bool HasSharedModels =>
        File.Exists(Path.Combine(SharedDir, "hubert.onnx")) && File.Exists(Path.Combine(SharedDir, "rmvpe.onnx"));

    public static List<AiVoiceInfo> Voices()
    {
        var list = new List<AiVoiceInfo>();
        string dir = Path.Combine(Root, "voices");
        if (!Directory.Exists(dir)) return list;

        foreach (var folder in Directory.GetDirectories(dir))
        {
            try
            {
                string model = Path.Combine(folder, "voice.onnx");
                string meta = Path.Combine(folder, "voice.json");
                if (!File.Exists(model) || !File.Exists(meta)) continue;
                var info = JsonSerializer.Deserialize<AiVoiceInfo>(File.ReadAllText(meta), Json);
                if (info == null) continue;
                list.Add(info with { Id = Path.GetFileName(folder), ModelPath = model });
            }
            catch
            {
                // Skip a broken voice rather than hiding the rest.
            }
        }
        return list.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Installs a voice pack zip, replacing voices with the same folder name.</summary>
    public static int Install(string zipPath)
    {
        int voices = 0;
        string rootFull = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            string rel = entry.FullName.Replace('\\', '/');
            if (!rel.StartsWith("shared/") && !rel.StartsWith("voices/")) continue;

            string dest = Path.GetFullPath(Path.Combine(Root, rel));
            if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue; // no escaping the folder
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
            if (entry.Name == "voice.json") voices++;
        }
        return voices;
    }

    /// <summary>Installs any voice pack sitting next to MicMix.exe, then renames it so it only happens once.</summary>
    public static int InstallPacksNextToApp()
    {
        int voices = 0;
        string dir = AppContext.BaseDirectory;
        foreach (var zip in Directory.GetFiles(dir, "MicMix-AI*.zip"))
        {
            try
            {
                voices += Install(zip);
                File.Move(zip, zip + ".installed", overwrite: true);
            }
            catch
            {
                // Leave it for the Install button, which reports the error.
            }
        }
        return voices;
    }
}
