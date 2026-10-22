using MicMix.Audio.Ai;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicMix.Audio;

/// <summary>
/// Mixes the microphone (through the voice effect) and one app's audio, and
/// plays the result into the chosen output, normally the virtual cable.
/// Everything except the mic and output device can be changed while running.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private static readonly TimeSpan MaxBuffered = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan TrimTo = TimeSpan.FromMilliseconds(40);

    private readonly object _lock = new();

    private WasapiCapture? _mic;
    private BufferedWaveProvider? _micBuffer;
    private ProcessLoopbackCapture? _app;
    private BufferedWaveProvider? _appBuffer;
    private WasapiOut? _output;
    private WasapiOut? _monitorOut;

    private VoiceEffectProvider? _voice;
    private AiVoiceProvider? _ai;
    private GainMeter? _micIn;
    private GainMeter? _micOut;
    private GainMeter? _appOut;
    private Tee? _tee;
    private int _sampleRate;

    private float _micGain = 1f, _micVolume = 1f, _appGain = 1f, _appVolume = 0.6f;
    private bool _micMuted, _appMuted;
    private VoiceSettings _voiceSettings = VoiceSettings.Neutral;
    private int? _appPid;
    private string? _monitorId;

    public bool IsRunning { get; private set; }
    public string? MicId { get; private set; }
    public string? OutputId { get; private set; }

    /// <summary>Raised from a background thread when something stops unexpectedly.</summary>
    public event EventHandler<string>? Problem;

    public void Start(string micDeviceId, string outputDeviceId)
    {
        lock (_lock)
        {
            if (IsRunning) StopInternal();

            using var enumerator = new MMDeviceEnumerator();
            var outDevice = enumerator.GetDevice(outputDeviceId);
            var micDevice = enumerator.GetDevice(micDeviceId);

            var outFormat = outDevice.AudioClient.MixFormat;
            _sampleRate = outFormat.SampleRate;
            int outChannels = outFormat.Channels;

            var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, outChannels)) { ReadFully = true };

            try
            {
                // Microphone -> mono -> engine rate -> gain -> voice effect -> volume -> (copy for mirroring) -> output channels
                _mic = new WasapiCapture(micDevice, true, 20);
                _micBuffer = new BufferedWaveProvider(_mic.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(1) };
                var micBuffer = _micBuffer;
                _mic.DataAvailable += (_, e) => Feed(micBuffer, e);
                _mic.RecordingStopped += (_, e) => { if (e.Exception != null) Problem?.Invoke(this, "Microphone stopped: " + e.Exception.Message); };

                ISampleProvider micChain = new MonoDownmix(_micBuffer.ToSampleProvider());
                if (micChain.WaveFormat.SampleRate != _sampleRate)
                    micChain = new WdlResamplingSampleProvider(micChain, _sampleRate);
                _micIn = new GainMeter(micChain) { Gain = _micGain };
                _ai = new AiVoiceProvider(_micIn);
                _ai.Status += msg => AiStatus?.Invoke(this, msg);
                _voice = new VoiceEffectProvider(_ai, _voiceSettings);
                _micOut = new GainMeter(_voice) { Gain = _micVolume, Muted = _micMuted };
                _tee = new Tee(_micOut);
                mixer.AddMixerInput(new KeepAlive(new ChannelAdapter(_tee, outChannels)));

                // App audio arrives already at the engine rate in stereo.
                _appBuffer = new BufferedWaveProvider(new WaveFormat(_sampleRate, 16, 2)) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(1) };
                _appOut = new GainMeter(_appBuffer.ToSampleProvider()) { Gain = _appGain * _appVolume, Muted = _appMuted };
                mixer.AddMixerInput(new KeepAlive(new ChannelAdapter(_appOut, outChannels)));

                _output = new WasapiOut(outDevice, AudioClientShareMode.Shared, true, 30);
                _output.PlaybackStopped += (_, e) => { if (e.Exception != null) Problem?.Invoke(this, "Output stopped: " + e.Exception.Message); };
                _output.Init(mixer);

                _mic.StartRecording();
                _output.Play();
                IsRunning = true;
                ApplyAiVoice();
                MicId = micDeviceId;
                OutputId = outputDeviceId;
            }
            catch
            {
                StopInternal();
                throw;
            }
        }

        // Optional parts shouldn't take the mic down with them if they fail.
        try { SetApp(_appPid); }
        catch (Exception ex) { Problem?.Invoke(this, "Couldn't capture that app: " + ex.Message); }

        try { SetMirror(_monitorId); }
        catch (Exception ex) { Problem?.Invoke(this, "Couldn't start mirroring: " + ex.Message); }
    }

    /// <summary>Plays your processed voice to this device so you can hear yourself. Null turns it off.</summary>
    public void SetMirror(string? deviceId)
    {
        lock (_lock)
        {
            _monitorId = deviceId;
            if (_tee != null) _tee.Enabled = false;
            _monitorOut?.Dispose();
            _monitorOut = null;

            if (deviceId == null || !IsRunning || _tee == null) return;

            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);
            var fmt = device.AudioClient.MixFormat;
            var copy = _tee.Copy;
            _monitorOut = OpenOutput(device, channels =>
            {
                ISampleProvider chain = copy;
                if (fmt.SampleRate != _sampleRate) chain = new WdlResamplingSampleProvider(chain, fmt.SampleRate);
                return new KeepAlive(new ChannelAdapter(chain, channels));
            }, fmt.Channels);
            _tee.Copy.Clear();
            _tee.Enabled = true;
            _monitorOut.Play();
        }
    }

    /// <summary>Switches which app is shared. Null stops sharing app audio.</summary>
    public void SetApp(int? processId)
    {
        lock (_lock)
        {
            _appPid = processId;
            _app?.Dispose();
            _app = null;
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
    }

    /// <summary>
    /// Opens a shared-mode output, falling back to plain stereo and to timer-driven
    /// playback for virtual devices (Sonar, Voicemeeter and friends) that refuse the
    /// device's own format or event-driven mode.
    /// </summary>
    private static WasapiOut OpenOutput(MMDevice device, Func<int, ISampleProvider> build, int channels)
    {
        Exception? first = null;
        foreach (var (ch, events) in new[] { (channels, true), (2, true), (channels, false), (2, false) })
        {
            var output = new WasapiOut(device, AudioClientShareMode.Shared, events, events ? 30 : 60);
            try
            {
                output.Init(build(ch));
                return output;
            }
            catch (Exception ex)
            {
                first ??= ex;
                output.Dispose();
            }
        }
        throw new InvalidOperationException($"{device.FriendlyName} won't accept audio ({first!.Message})", first);
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

    public VoiceSettings Voice
    {
        get => _voiceSettings;
        set
        {
            var old = _voiceSettings;
            _voiceSettings = value;
            _voice?.SetSettings(value);
            if (old.AiVoice != value.AiVoice) ApplyAiVoice();
            else if (_ai != null) _ai.Key = value.AiKey;
        }
    }

    /// <summary>Loading, ready and trouble messages from the AI voice, shown in its panel.</summary>
    public event EventHandler<string>? AiStatus;

    /// <summary>Installed AI voices, so a voice id can be turned into its model.</summary>
    public List<AiVoiceInfo> AiVoices { get; set; } = new();

    private void ApplyAiVoice()
    {
        if (_ai == null) return;
        var info = _voiceSettings.AiVoice == null ? null : AiVoices.FirstOrDefault(v => v.Id == _voiceSettings.AiVoice);
        if (_voiceSettings.AiVoice != null && info == null)
            AiStatus?.Invoke(this, "That AI voice isn't installed.");
        _ai.SetVoice(info, _voiceSettings.AiKey);
    }

    /// <summary>Boost before the voice effect, as a linear factor.</summary>
    public float MicGain
    {
        get => _micGain;
        set { _micGain = value; if (_micIn != null) _micIn.Gain = value; }
    }

    public float MicVolume
    {
        get => _micVolume;
        set { _micVolume = value; if (_micOut != null) _micOut.Gain = value; }
    }

    public float AppGain
    {
        get => _appGain;
        set { _appGain = value; if (_appOut != null) _appOut.Gain = _appGain * _appVolume; }
    }

    public float AppVolume
    {
        get => _appVolume;
        set { _appVolume = value; if (_appOut != null) _appOut.Gain = _appGain * _appVolume; }
    }

    public bool MicMuted
    {
        get => _micMuted;
        set { _micMuted = value; if (_micOut != null) _micOut.Muted = value; }
    }

    public bool AppMuted
    {
        get => _appMuted;
        set { _appMuted = value; if (_appOut != null) _appOut.Muted = value; }
    }

    public float TakeMicPeak() => _micIn?.TakePeak() ?? 0f;
    public float TakeAppPeak() => _appOut?.TakePeak() ?? 0f;

    public void Stop()
    {
        lock (_lock) StopInternal();
    }

    private void StopInternal()
    {
        IsRunning = false;
        _app?.Dispose(); _app = null;
        try { _mic?.StopRecording(); } catch { }
        _mic?.Dispose(); _mic = null;
        _output?.Dispose(); _output = null;
        _monitorOut?.Dispose(); _monitorOut = null;
        _ai?.Dispose(); _ai = null;
        _micBuffer = null; _appBuffer = null;
        _voice = null; _micIn = null; _micOut = null; _appOut = null; _tee = null;
    }

    public void Dispose() => Stop();
}
