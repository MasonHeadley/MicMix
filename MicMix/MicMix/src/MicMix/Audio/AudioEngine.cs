using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicMix.Audio;

/// <summary>
/// Mixes the microphone (through the voice effect) and one app's audio, and
/// plays the result into the chosen output, normally the virtual cable.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private static readonly TimeSpan MaxBuffered = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan TrimTo = TimeSpan.FromMilliseconds(40);

    private WasapiCapture? _mic;
    private BufferedWaveProvider? _micBuffer;
    private ProcessLoopbackCapture? _app;
    private BufferedWaveProvider? _appBuffer;
    private WasapiOut? _output;
    private WasapiOut? _monitorOut;

    private VoiceEffectProvider? _voice;
    private GainMeter? _micGain;
    private GainMeter? _appGain;
    private Tee? _tee;
    private int _sampleRate;

    private float _micVolume = 1f, _appVolume = 0.6f;
    private bool _micMuted, _appMuted;
    private VoicePreset _preset = VoicePreset.Off;

    public bool IsRunning { get; private set; }

    /// <summary>Raised from a background thread when a device stops unexpectedly.</summary>
    public event EventHandler<string>? Problem;

    public void Start(string micDeviceId, string outputDeviceId, string? monitorDeviceId, int? appProcessId)
    {
        if (IsRunning) Stop();

        using var enumerator = new MMDeviceEnumerator();
        var outDevice = enumerator.GetDevice(outputDeviceId);
        var micDevice = enumerator.GetDevice(micDeviceId);

        var outFormat = outDevice.AudioClient.MixFormat;
        _sampleRate = outFormat.SampleRate;
        int outChannels = outFormat.Channels;

        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, outChannels)) { ReadFully = true };

        try
        {
            // Microphone -> mono -> engine rate -> voice effect -> gain -> (copy for monitor) -> output channels
            _mic = new WasapiCapture(micDevice, true, 20);
            _micBuffer = new BufferedWaveProvider(_mic.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(1) };
            _mic.DataAvailable += (_, e) => Feed(_micBuffer, e);
            _mic.RecordingStopped += (_, e) => { if (e.Exception != null) Problem?.Invoke(this, "Microphone stopped: " + e.Exception.Message); };

            ISampleProvider micChain = new MonoDownmix(_micBuffer.ToSampleProvider());
            if (micChain.WaveFormat.SampleRate != _sampleRate)
                micChain = new WdlResamplingSampleProvider(micChain, _sampleRate);
            _voice = new VoiceEffectProvider(micChain, _preset);
            _micGain = new GainMeter(_voice) { Gain = _micVolume, Muted = _micMuted };
            _tee = new Tee(_micGain);
            mixer.AddMixerInput(new ChannelAdapter(_tee, outChannels));

            // App audio arrives already at the engine rate in stereo.
            _appBuffer = new BufferedWaveProvider(new WaveFormat(_sampleRate, 16, 2)) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(1) };
            _appGain = new GainMeter(_appBuffer.ToSampleProvider()) { Gain = _appVolume, Muted = _appMuted };
            mixer.AddMixerInput(new ChannelAdapter(_appGain, outChannels));

            _output = new WasapiOut(outDevice, AudioClientShareMode.Shared, true, 30);
            _output.PlaybackStopped += (_, e) => { if (e.Exception != null) Problem?.Invoke(this, "Output stopped: " + e.Exception.Message); };
            _output.Init(mixer);

            if (monitorDeviceId != null)
                StartMonitor(enumerator.GetDevice(monitorDeviceId));

            _mic.StartRecording();
            _output.Play();
            IsRunning = true;
        }
        catch
        {
            Stop();
            throw;
        }

        // A failing app capture shouldn't take the mic down with it.
        try
        {
            SetApp(appProcessId);
        }
        catch (Exception ex)
        {
            Problem?.Invoke(this, "Couldn't capture that app: " + ex.Message);
        }
    }

    private void StartMonitor(MMDevice device)
    {
        var fmt = device.AudioClient.MixFormat;
        ISampleProvider chain = _tee!.Copy;
        if (fmt.SampleRate != _sampleRate) chain = new WdlResamplingSampleProvider(chain, fmt.SampleRate);
        chain = new ChannelAdapter(chain, fmt.Channels);

        _monitorOut = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
        _monitorOut.Init(chain);
        _tee.Enabled = true;
        _monitorOut.Play();
    }

    /// <summary>Switches which app is shared. Null stops sharing app audio.</summary>
    public void SetApp(int? processId)
    {
        if (_app != null)
        {
            _app.Dispose();
            _app = null;
        }
        _appBuffer?.ClearBuffer();

        if (processId == null || !IsRunning || _appBuffer == null) return;

        var capture = new ProcessLoopbackCapture(processId.Value, _sampleRate);
        var buffer = _appBuffer;
        capture.DataAvailable += (_, e) => Feed(buffer, e);
        capture.Failed += (_, ex) => Problem?.Invoke(this, "App audio stopped: " + ex.Message);
        try
        {
            capture.Start();
        }
        catch
        {
            capture.Dispose();
            throw;
        }
        _app = capture;
    }

    private static void Feed(BufferedWaveProvider buffer, WaveInEventArgs e)
    {
        buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        // Input and output devices run on slightly different clocks. Drop the
        // backlog when it builds up so the delay never creeps upward.
        if (buffer.BufferedDuration > MaxBuffered)
        {
            var fmt = buffer.WaveFormat;
            int excess = (int)((buffer.BufferedDuration - TrimTo).TotalSeconds * fmt.AverageBytesPerSecond);
            excess -= excess % fmt.BlockAlign;
            if (excess > 0) buffer.Read(new byte[excess], 0, excess);
        }
    }

    public float MicVolume
    {
        get => _micVolume;
        set { _micVolume = value; if (_micGain != null) _micGain.Gain = value; }
    }

    public float AppVolume
    {
        get => _appVolume;
        set { _appVolume = value; if (_appGain != null) _appGain.Gain = value; }
    }

    public bool MicMuted
    {
        get => _micMuted;
        set { _micMuted = value; if (_micGain != null) _micGain.Muted = value; }
    }

    public bool AppMuted
    {
        get => _appMuted;
        set { _appMuted = value; if (_appGain != null) _appGain.Muted = value; }
    }

    public VoicePreset Preset
    {
        get => _preset;
        set { _preset = value; _voice?.SetPreset(value); }
    }

    public float TakeMicPeak() => _micGain?.TakePeak() ?? 0f;
    public float TakeAppPeak() => _appGain?.TakePeak() ?? 0f;

    public void Stop()
    {
        IsRunning = false;
        _app?.Dispose(); _app = null;
        try { _mic?.StopRecording(); } catch { }
        _mic?.Dispose(); _mic = null;
        _output?.Dispose(); _output = null;
        _monitorOut?.Dispose(); _monitorOut = null;
        _micBuffer = null; _appBuffer = null;
        _voice = null; _micGain = null; _appGain = null; _tee = null;
    }

    public void Dispose() => Stop();
}
