using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MicMix.Audio;
using MicMix.Audio.Ai;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NAudio.CoreAudioApi;

namespace MicMix;

/// <summary>
/// Hosts the HTML interface and connects it to the audio engine. The page sends
/// small JSON messages for every control change; nothing here needs a restart
/// except picking a different mic or output device, which restarts quietly.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Shown as 0.4.01: the last part is the build count between releases.</summary>
    public static readonly string Version = FormatVersion(Assembly.GetExecutingAssembly().GetName().Version);

    private readonly AudioEngine _engine = new();
    private readonly Settings _settings = Settings.Load();
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(10, 10, 16) };
    private readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 60 };
    private List<AppItem> _apps = new();
    private bool _ready;
    private bool _dirty;
    private int _ticks;

    public MainForm()
    {
        Text = "MicMix v" + Version;
        BackColor = Color.FromArgb(10, 10, 16);
        MinimumSize = new Size(660, 460);
        Size = new Size(1180, 800);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Controls.Add(_web);

        ApplyMixer();
        try { AiVoiceLibrary.InstallPacksNextToApp(); } catch { }
        _engine.AiVoices = AiVoiceLibrary.Voices();
        _engine.Voice = _settings.Voice;
        _engine.Problem += (_, msg) => BeginInvoke(() => SendStatus(msg));
        _engine.AiStatus += (_, msg) => BeginInvoke(() => Post(new { type = "aiStatus", text = msg }));

        _meterTimer.Tick += (_, _) =>
        {
            if (_ready)
                Post(new { type = "levels", mic = _engine.TakeMicPeak(), app = _engine.TakeAppPeak() });

            // Save a couple of seconds after the last change rather than on every slider tick.
            if (_dirty && ++_ticks > 30)
            {
                _dirty = false;
                _ticks = 0;
                _settings.Save();
            }
        };

        Load += async (_, _) => await InitWebAsync();
    }

    private async Task InitWebAsync()
    {
        try
        {
            string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicMix", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "MicMix needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11 and most Windows 10 PCs.\n\n" +
                "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and start MicMix again.\n\n" + ex.Message,
                "MicMix", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.WebMessageReceived += (_, e) => OnMessage(e.WebMessageAsJson);
        core.NavigateToString(LoadPage());
    }

    private static string LoadPage()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MicMix.UI.index.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void OnMessage(string json)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(json); }
        catch { return; }
        if (msg == null) return;
        _dirty = true;
        _ticks = 0;

        try
        {
            switch ((string?)msg["type"])
            {
                case "ready":
                    _ready = true;
                    SendInit();
                    _meterTimer.Start();
                    AutoStart();
                    break;

                case "power":
                    if ((bool?)msg["on"] == true) StartEngine();
                    else { _engine.Stop(); SendStatus("Stopped"); }
                    break;

                case "voice":
                    _settings.Voice = msg["voice"]!.Deserialize<VoiceSettings>(Json) ?? VoiceSettings.Neutral;
                    _settings.ActivePreset = (string?)msg["preset"] ?? "";
                    _engine.Voice = _settings.Voice;
                    break;

                case "mixer":
                    SetMixer((string)msg["key"]!, msg["value"]!);
                    break;

                case "app":
                    SelectApp((int?)msg["pid"]);
                    break;

                case "refreshApps":
                    SendApps();
                    break;

                case "devices":
                    _settings.MicId = (string?)msg["mic"];
                    _settings.OutputId = (string?)msg["output"];
                    _settings.MirrorId = (string?)msg["mirror"];
                    if (_engine.IsRunning && (_engine.MicId != _settings.MicId || _engine.OutputId != _settings.OutputId))
                        StartEngine();
                    else
                        UpdateMirror();
                    break;

                case "theme":
                    _settings.Theme = (string?)msg["theme"] ?? "cyber";
                    break;

                case "saveProfile":
                {
                    string name = ((string?)msg["name"] ?? "").Trim();
                    if (name.Length == 0) break;
                    var voice = msg["voice"]!.Deserialize<VoiceSettings>(Json) ?? VoiceSettings.Neutral;
                    _settings.Profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    _settings.Profiles.Add(new VoiceProfile { Name = name, Voice = voice });
                    _settings.ActivePreset = name;
                    _settings.Save();
                    SendProfiles();
                    break;
                }

                case "installAiPack":
                    InstallAiPack();
                    break;

                case "openUrl":
                {
                    // Only our own links, opened in the normal browser.
                    string url = (string?)msg["url"] ?? "";
                    bool ours = url.StartsWith("https://github.com/MasonHeadley/", StringComparison.OrdinalIgnoreCase);
                    bool credit = _engine.AiVoices.Any(v => v.Link == url) || CreditLinks.Contains(url);
                    if (ours || credit)
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                    break;
                }

                case "deleteProfile":
                {
                    string name = (string?)msg["name"] ?? "";
                    _settings.Profiles.RemoveAll(p => p.Name == name);
                    _settings.Save();
                    SendProfiles();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            SendStatus(ex.Message);
        }
    }

    private void InstallAiPack()
    {
        using var dialog = new OpenFileDialog { Title = "Pick an AI voice pack", Filter = "AI voice pack (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            int n = AiVoiceLibrary.Install(dialog.FileName);
            _engine.AiVoices = AiVoiceLibrary.Voices();
            SendAiVoices();
            SendStatus(n == 1 ? "Installed 1 AI voice" : $"Installed {n} AI voices");
        }
        catch (Exception ex)
        {
            SendStatus("Couldn't install that pack: " + ex.Message);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private object AiVoiceList() => new
    {
        ready = AiVoiceLibrary.HasSharedModels,
        voices = _engine.AiVoices.Select(v => new { id = v.Id, name = v.Name, credit = v.Credit, link = v.Link, key = v.Key }),
    };

    private void SendAiVoices() => Post(new { type = "aiVoices", ai = AiVoiceList() });

    private void AutoStart()
    {
        if (_settings.MicId != null && _settings.OutputId != null) StartEngine();
        else SendStatus("Pick your devices in Settings, then press Power.");
    }

    private void StartEngine()
    {
        var inputs = DeviceList.Inputs();
        var outputs = DeviceList.Outputs();
        string? mic = inputs.FirstOrDefault(d => d.Id == _settings.MicId)?.Id ?? inputs.FirstOrDefault(d => !IsCable(d))?.Id;
        string? output = outputs.FirstOrDefault(d => d.Id == _settings.OutputId)?.Id ?? outputs.FirstOrDefault(IsCable)?.Id;

        if (mic == null || output == null)
        {
            SendStatus(output == null ? "No virtual cable found. Install VB-Cable and restart MicMix." : "No microphone found.");
            return;
        }

        try
        {
            Cursor = Cursors.WaitCursor;
            _engine.Start(mic, output);
            UpdateMirror();
            SendStatus("Live");
        }
        catch (Exception ex)
        {
            SendStatus("Couldn't start: " + ex.Message);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void UpdateMirror()
    {
        try
        {
            string? mirror = _settings.Mirror ? _settings.MirrorId ?? DeviceList.DefaultId(DataFlow.Render) : null;
            _engine.SetMirror(mirror);
        }
        catch (Exception ex)
        {
            SendStatus("Couldn't start mirroring: " + ex.Message);
        }
    }

    private void SelectApp(int? pid)
    {
        if (pid == 0) pid = null;
        _settings.AppName = _apps.FirstOrDefault(a => a.ProcessId == pid)?.Name;
        try
        {
            _engine.SetApp(pid);
            if (_engine.IsRunning) SendStatus("Live");
        }
        catch (Exception ex)
        {
            SendStatus("Couldn't capture that app: " + ex.Message);
        }
    }

    private void SetMixer(string key, JsonNode value)
    {
        switch (key)
        {
            case "micGain": _settings.MicGain = (int)value; break;
            case "micVolume": _settings.MicVolume = (int)value; break;
            case "appGain": _settings.AppGain = (int)value; break;
            case "appVolume": _settings.AppVolume = (int)value; break;
            case "micMuted": _settings.MicMuted = (bool)value; break;
            case "appMuted": _settings.AppMuted = (bool)value; break;
            case "mirror": _settings.Mirror = (bool)value; UpdateMirror(); break;
        }
        ApplyMixer();
    }

    private void ApplyMixer()
    {
        _engine.MicGain = DbToGain(_settings.MicGain);
        _engine.MicVolume = _settings.MicVolume / 100f;
        _engine.AppGain = DbToGain(_settings.AppGain);
        _engine.AppVolume = _settings.AppVolume / 100f;
        _engine.MicMuted = _settings.MicMuted;
        _engine.AppMuted = _settings.AppMuted;
    }

    private static float DbToGain(int db) => MathF.Pow(10f, Math.Clamp(db, 0, 24) / 20f);

    private void SendInit()
    {
        _apps = DeviceList.Apps();
        var inputs = DeviceList.Inputs().Where(d => !IsCable(d)).ToList();
        var outputs = DeviceList.Outputs();

        _settings.MicId ??= DeviceList.DefaultId(DataFlow.Capture);
        _settings.OutputId ??= outputs.FirstOrDefault(IsCable)?.Id;
        _settings.MirrorId ??= DeviceList.DefaultId(DataFlow.Render);

        var savedApp = _apps.FirstOrDefault(a => a.Name == _settings.AppName);
        _engine.SetApp(savedApp?.ProcessId);

        Post(new
        {
            type = "init",
            version = Version,
            theme = _settings.Theme,
            hasCable = outputs.Any(IsCable),
            devices = new
            {
                inputs = inputs.Select(d => new { id = d.Id, name = d.Name }),
                outputs = outputs.Select(d => new { id = d.Id, name = d.Name }),
                mirrors = outputs.Where(d => !IsCable(d)).Select(d => new { id = d.Id, name = d.Name }),
                mic = _settings.MicId,
                output = _settings.OutputId,
                mirror = _settings.MirrorId,
            },
            apps = AppList(),
            app = savedApp?.ProcessId ?? 0,
            mixer = new
            {
                micGain = _settings.MicGain,
                micVolume = _settings.MicVolume,
                appGain = _settings.AppGain,
                appVolume = _settings.AppVolume,
                micMuted = _settings.MicMuted,
                appMuted = _settings.AppMuted,
                mirror = _settings.Mirror,
            },
            presets = VoiceSettings.Presets.Select(p => new { name = p.Name, voice = p.Settings }),
            ai = AiVoiceList(),
            profiles = _settings.Profiles.Select(p => new { name = p.Name, voice = p.Voice }),
            activePreset = _settings.ActivePreset,
            voice = _settings.Voice,
        });
    }

    private object AppList() => _apps.Select(a => new { pid = a.ProcessId, name = a.Name, title = a.Title });

    private void SendApps()
    {
        int? current = _apps.FirstOrDefault(a => a.Name == _settings.AppName)?.ProcessId;
        _apps = DeviceList.Apps();
        var match = _apps.FirstOrDefault(a => a.Name == _settings.AppName);
        // The same app may have restarted with a new process id.
        if (match != null && match.ProcessId != current) SelectApp(match.ProcessId);
        Post(new { type = "apps", apps = AppList(), app = match?.ProcessId ?? 0 });
    }

    private void SendProfiles() =>
        Post(new { type = "profiles", profiles = _settings.Profiles.Select(p => new { name = p.Name, voice = p.Voice }), activePreset = _settings.ActivePreset });

    private void SendStatus(string text) => Post(new { type = "status", text, running = _engine.IsRunning });

    private void Post(object message)
    {
        if (!_ready || _web.CoreWebView2 == null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _settings.Save();
        _meterTimer.Stop();
        _engine.Dispose();
        base.OnFormClosing(e);
    }

    private static string FormatVersion(Version? v) => v == null ? "dev" : $"{v.Major}.{v.Minor}.{v.Build:D2}";

    /// <summary>Links shown on the credits list that may open in the browser.</summary>
    private static readonly HashSet<string> CreditLinks = new()
    {
        "https://vb-audio.com/Cable/",
        "https://github.com/naudio/NAudio",
        "https://github.com/microsoft/onnxruntime",
        "https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI",
        "https://github.com/mrdoob/three.js",
    };

    private static bool IsCable(DeviceItem d) => d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase);
}
