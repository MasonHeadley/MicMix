using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace MicMix.Audio;

public sealed record DeviceItem(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record AppItem(int ProcessId, string Name, string? Title)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? Name : $"{Name}  ({Truncate(Title, 50)})";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

public static class DeviceList
{
    public static List<DeviceItem> Inputs() => List(DataFlow.Capture);
    public static List<DeviceItem> Outputs() => List(DataFlow.Render);

    public static string? DefaultId(DataFlow flow)
    {
        using var en = new MMDeviceEnumerator();
        if (!en.HasDefaultAudioEndpoint(flow, Role.Communications)) return null;
        return en.GetDefaultAudioEndpoint(flow, Role.Communications).ID;
    }

    private static List<DeviceItem> List(DataFlow flow)
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new DeviceItem(d.ID, d.FriendlyName))
            .OrderBy(d => d.Name)
            .ToList();
    }

    /// <summary>
    /// Apps worth offering: anything with an audio session, plus anything with a window
    /// (so a paused player still shows up). One entry per executable name.
    /// </summary>
    public static List<AppItem> Apps()
    {
        int self = Environment.ProcessId;
        var withAudio = new HashSet<int>();

        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var sessions = dev.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    if (s.IsSystemSoundsSession) continue;
                    int pid = (int)s.GetProcessID;
                    if (pid != 0 && pid != self) withAudio.Add(pid);
                }
            }
        }
        catch
        {
            // Session enumeration can fail on odd drivers; windows alone still give a usable list.
        }

        var byName = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self) continue;
                    bool hasWindow = p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle);
                    if (!hasWindow && !withAudio.Contains(p.Id)) continue;

                    var item = new AppItem(p.Id, p.ProcessName, hasWindow ? p.MainWindowTitle : null);

                    // Prefer the windowed process: capture includes its children, which is
                    // where browsers and Electron apps actually play their audio from.
                    if (!byName.TryGetValue(p.ProcessName, out var existing) || (existing.Title == null && hasWindow))
                        byName[p.ProcessName] = item;
                }
                catch
                {
                    // Process exited or access denied.
                }
            }
        }

        return byName.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
