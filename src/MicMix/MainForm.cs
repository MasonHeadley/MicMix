using MicMix.Audio;
using NAudio.CoreAudioApi;

namespace MicMix;

public sealed class MainForm : Form
{
    private static readonly AppItem NoApp = new(0, "Nothing (mic only)", null);

    private readonly AudioEngine _engine = new();
    private readonly Settings _settings = Settings.Load();

    private readonly ComboBox _mic = Combo();
    private readonly CheckBox _micMute = new() { Text = "Mute", AutoSize = true };
    private readonly TrackBar _micVolume = Slider();
    private readonly ProgressBar _micMeter = Meter();
    private readonly ComboBox _preset = Combo();
    private readonly ComboBox _app = Combo();
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly CheckBox _appMute = new() { Text = "Mute", AutoSize = true };
    private readonly TrackBar _appVolume = Slider();
    private readonly ProgressBar _appMeter = Meter();
    private readonly ComboBox _output = Combo();
    private readonly CheckBox _monitor = new() { Text = "Hear myself on", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox _monitorDevice = Combo();
    private readonly Button _start = new() { Text = "Start", Width = 120, Height = 34 };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
    private readonly Label _hint = new() { AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText };
    private readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 50 };

    public MainForm()
    {
        Text = "MicMix";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;

        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddRow(grid, "Microphone", _mic, _micMute);
        AddRow(grid, "Mic volume", _micVolume, _micMeter);
        AddRow(grid, "Voice effect", _preset, null);
        AddRow(grid, "Share app audio", _app, _refresh);
        AddRow(grid, "App volume", _appVolume, Stack(_appMeter, _appMute));
        AddRow(grid, "Send to", _output, null);

        grid.Controls.Add(_monitor, 0, grid.RowCount);
        grid.Controls.Add(_monitorDevice, 1, grid.RowCount);
        grid.RowCount++;

        var startRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 4), WrapContents = false };
        startRow.Controls.Add(_start);
        startRow.Controls.Add(_status);
        grid.Controls.Add(startRow, 0, grid.RowCount);
        grid.SetColumnSpan(startRow, 3);
        grid.RowCount++;

        grid.Controls.Add(_hint, 0, grid.RowCount);
        grid.SetColumnSpan(_hint, 3);
        grid.RowCount++;

        Controls.Add(grid);

        LoadChoices();
        WireEvents();
        UpdateRunningState();
    }

    private void LoadChoices()
    {
        var inputs = DeviceList.Inputs();
        var outputs = DeviceList.Outputs();

        // Don't offer the cable's own output as a mic, that just loops back on itself.
        _mic.Items.AddRange(inputs.Where(d => !IsCable(d)).Cast<object>().ToArray());
        Select(_mic, _settings.MicId ?? DeviceList.DefaultId(DataFlow.Capture));

        _output.Items.AddRange(outputs.Cast<object>().ToArray());
        string? cable = outputs.FirstOrDefault(IsCable)?.Id;
        Select(_output, _settings.OutputId ?? cable);

        _monitorDevice.Items.AddRange(outputs.Where(d => !IsCable(d)).Cast<object>().ToArray());
        Select(_monitorDevice, _settings.MonitorId ?? DeviceList.DefaultId(DataFlow.Render));
        _monitor.Checked = _settings.MonitorEnabled;

        _preset.Items.AddRange(VoicePreset.All.Cast<object>().ToArray());
        _preset.SelectedItem = VoicePreset.All.FirstOrDefault(p => p.Name == _settings.Preset) ?? VoicePreset.Off;
        _engine.Preset = (VoicePreset)_preset.SelectedItem;

        _micVolume.Value = Math.Clamp(_settings.MicVolume, 0, 200);
        _appVolume.Value = Math.Clamp(_settings.AppVolume, 0, 200);
        _engine.MicVolume = _micVolume.Value / 100f;
        _engine.AppVolume = _appVolume.Value / 100f;

        RefreshApps();

        _hint.Text = cable == null
            ? "No virtual cable found. Install VB-Cable (free, vb-audio.com), restart this app, and pick \"CABLE Input\" under Send to."
            : "In Discord, Zoom, a game, etc., set your microphone / input device to \"CABLE Output\".";
    }

    private void RefreshApps()
    {
        string? keep = (_app.SelectedItem as AppItem)?.Name ?? _settings.AppName;
        _app.BeginUpdate();
        _app.Items.Clear();
        _app.Items.Add(NoApp);
        _app.Items.AddRange(DeviceList.Apps().Cast<object>().ToArray());
        _app.SelectedItem = _app.Items.Cast<AppItem>().FirstOrDefault(a => a.Name == keep) ?? NoApp;
        _app.EndUpdate();
    }

    private void WireEvents()
    {
        _start.Click += (_, _) => ToggleRunning();
        _refresh.Click += (_, _) => RefreshApps();

        _micVolume.ValueChanged += (_, _) => _engine.MicVolume = _micVolume.Value / 100f;
        _appVolume.ValueChanged += (_, _) => _engine.AppVolume = _appVolume.Value / 100f;
        _micMute.CheckedChanged += (_, _) => _engine.MicMuted = _micMute.Checked;
        _appMute.CheckedChanged += (_, _) => _engine.AppMuted = _appMute.Checked;
        _preset.SelectedIndexChanged += (_, _) => { if (_preset.SelectedItem is VoicePreset p) _engine.Preset = p; };

        _app.SelectedIndexChanged += (_, _) =>
        {
            if (!_engine.IsRunning) return;
            try
            {
                _engine.SetApp(SelectedAppId());
                SetStatus("Running");
            }
            catch (Exception ex)
            {
                SetStatus("Couldn't capture that app: " + ex.Message);
            }
        };

        _engine.Problem += (_, msg) => BeginInvoke(() => SetStatus(msg));

        _meterTimer.Tick += (_, _) =>
        {
            _micMeter.Value = Decay(_micMeter.Value, _engine.TakeMicPeak());
            _appMeter.Value = Decay(_appMeter.Value, _engine.TakeAppPeak());
        };
        _meterTimer.Start();
    }

    private void ToggleRunning()
    {
        if (_engine.IsRunning)
        {
            _engine.Stop();
            SetStatus("Stopped");
        }
        else
        {
            if (_mic.SelectedItem is not DeviceItem mic || _output.SelectedItem is not DeviceItem output)
            {
                SetStatus("Pick a microphone and an output first.");
                return;
            }

            string? monitor = _monitor.Checked ? (_monitorDevice.SelectedItem as DeviceItem)?.Id : null;
            try
            {
                Cursor = Cursors.WaitCursor;
                _engine.Start(mic.Id, output.Id, monitor, SelectedAppId());
                SetStatus("Running");
            }
            catch (Exception ex)
            {
                SetStatus("Couldn't start: " + ex.Message);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }
        UpdateRunningState();
    }

    private int? SelectedAppId() => _app.SelectedItem is AppItem a && a.ProcessId != 0 ? a.ProcessId : null;

    private void UpdateRunningState()
    {
        bool running = _engine.IsRunning;
        _start.Text = running ? "Stop" : "Start";
        _mic.Enabled = _output.Enabled = _monitor.Enabled = _monitorDevice.Enabled = !running;
    }

    private void SetStatus(string text) => _status.Text = text;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _settings.MicId = (_mic.SelectedItem as DeviceItem)?.Id;
        _settings.OutputId = (_output.SelectedItem as DeviceItem)?.Id;
        _settings.MonitorId = (_monitorDevice.SelectedItem as DeviceItem)?.Id;
        _settings.MonitorEnabled = _monitor.Checked;
        _settings.AppName = _app.SelectedItem is AppItem a && a != NoApp ? a.Name : null;
        _settings.Preset = (_preset.SelectedItem as VoicePreset)?.Name ?? "Off";
        _settings.MicVolume = _micVolume.Value;
        _settings.AppVolume = _appVolume.Value;
        _settings.Save();

        _meterTimer.Stop();
        _engine.Dispose();
        base.OnFormClosing(e);
    }

    private static bool IsCable(DeviceItem d) => d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase);

    private static void Select(ComboBox box, string? id)
    {
        box.SelectedItem = box.Items.Cast<DeviceItem>().FirstOrDefault(d => d.Id == id);
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static int Decay(int current, float peak)
    {
        int target = (int)(Math.Sqrt(peak) * 100);
        return Math.Max(target, current - 6);
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control main, Control? extra)
    {
        int row = grid.RowCount;
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 10, 6) }, 0, row);
        grid.Controls.Add(main, 1, row);
        if (extra != null) grid.Controls.Add(extra, 2, row);
        grid.RowCount++;
    }

    private static Control Stack(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static ComboBox Combo() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private static TrackBar Slider() => new() { Minimum = 0, Maximum = 200, TickFrequency = 25, Dock = DockStyle.Fill, AutoSize = false, Height = 32 };
    private static ProgressBar Meter() => new() { Width = 110, Height = 12, Anchor = AnchorStyles.Left, Maximum = 100 };
}
