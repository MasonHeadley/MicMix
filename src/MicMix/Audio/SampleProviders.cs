using NAudio.Wave;

namespace MicMix.Audio;

/// <summary>Averages all channels down to one.</summary>
internal sealed class MonoDownmix : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _scratch = Array.Empty<float>();

    public MonoDownmix(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_channels == 1) return _source.Read(buffer, offset, count);

        int needed = count * _channels;
        if (_scratch.Length < needed) _scratch = new float[needed];
        int read = _source.Read(_scratch, 0, needed);
        int frames = read / _channels;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++) sum += _scratch[f * _channels + c];
            buffer[offset + f] = sum / _channels;
        }
        return frames;
    }
}

/// <summary>Maps mono or stereo input onto however many channels the output device has.</summary>
internal sealed class ChannelAdapter : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _in;
    private readonly int _out;
    private float[] _scratch = Array.Empty<float>();

    public ChannelAdapter(ISampleProvider source, int outputChannels)
    {
        _source = source;
        _in = source.WaveFormat.Channels;
        _out = outputChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outputChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_in == _out) return _source.Read(buffer, offset, count);

        int frames = count / _out;
        int needed = frames * _in;
        if (_scratch.Length < needed) _scratch = new float[needed];
        int framesRead = _source.Read(_scratch, 0, needed) / _in;

        for (int f = 0; f < framesRead; f++)
        {
            for (int c = 0; c < _out; c++)
            {
                // Mono goes everywhere; stereo goes to front left/right and the rest stay quiet.
                float s = _in == 1 ? _scratch[f] : c < _in ? _scratch[f * _in + c] : 0f;
                buffer[offset + f * _out + c] = s;
            }
        }
        return framesRead * _out;
    }
}

/// <summary>Applies a live-adjustable gain and tracks the peak level for the meters.</summary>
internal sealed class GainMeter : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float _peak;

    public GainMeter(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;
    public volatile float Gain = 1f;
    public volatile bool Muted;

    /// <summary>When set, the peak is measured before gain and mute (an input meter).</summary>
    public bool MeterInput { get; init; }

    /// <summary>Peak since the last call, 0..1.</summary>
    public float TakePeak()
    {
        float p = _peak;
        _peak = 0;
        return Math.Min(1f, p);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        float gain = Muted ? 0f : Gain;
        float peak = _peak;
        for (int i = offset; i < offset + read; i++)
        {
            float raw = buffer[i];
            float s = raw * gain;
            buffer[i] = s;
            float a = Math.Abs(MeterInput ? raw : s);
            if (a > peak) peak = a;
        }
        _peak = peak;
        return read;
    }
}

/// <summary>
/// Always returns exactly what was asked for, padding with silence. The mixer
/// drops any input that comes up short, which would silently kill the mic.
/// </summary>
internal sealed class KeepAlive : ISampleProvider
{
    private readonly ISampleProvider _source;

    public KeepAlive(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = 0;
        try
        {
            read = _source.Read(buffer, offset, count);
        }
        catch
        {
            // Never let one bad buffer take the whole output down.
        }
        if (read < count) Array.Clear(buffer, offset + read, count - read);
        return count;
    }
}

/// <summary>Runs mono audio through the current voice settings, which can be swapped while playing.</summary>
internal sealed class VoiceEffectProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private volatile VoiceChain _chain;

    public VoiceEffectProvider(ISampleProvider source, VoiceSettings settings)
    {
        _source = source;
        _chain = new VoiceChain(settings, source.WaveFormat.SampleRate);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public void SetSettings(VoiceSettings settings) => _chain = new VoiceChain(settings, WaveFormat.SampleRate);

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        _chain.Process(buffer, offset, read);
        return read;
    }
}

/// <summary>
/// Passes audio through unchanged and copies it into a side buffer, so the
/// processed mic can also be played back to the user's own headphones.
/// </summary>
internal sealed class Tee : ISampleProvider
{
    private readonly ISampleProvider _source;

    public Tee(ISampleProvider source)
    {
        _source = source;
        Copy = new FloatRingBuffer(source.WaveFormat, source.WaveFormat.SampleRate / 5) { MaxLatency = source.WaveFormat.SampleRate * 30 / 1000 };
    }

    public WaveFormat WaveFormat => _source.WaveFormat;
    public FloatRingBuffer Copy { get; }
    public volatile bool Enabled;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (Enabled) Copy.Write(buffer, offset, read);
        return read;
    }
}

/// <summary>Small thread-safe float FIFO that drops the oldest audio when full and reads silence when empty.</summary>
internal sealed class FloatRingBuffer : ISampleProvider
{
    private readonly float[] _data;
    private readonly object _lock = new();
    private int _read, _count;

    public FloatRingBuffer(WaveFormat format, int capacity)
    {
        WaveFormat = format;
        _data = new float[capacity];
    }

    public WaveFormat WaveFormat { get; }

    public void Clear()
    {
        lock (_lock) { _read = 0; _count = 0; }
    }

    /// <summary>When more than this many samples are waiting, skip ahead so the delay stays short.</summary>
    public int MaxLatency { get; init; } = int.MaxValue;

    public void Write(float[] src, int offset, int count)
    {
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                int w = (_read + _count) % _data.Length;
                _data[w] = src[offset + i];
                if (_count < _data.Length) _count++;
                else _read = (_read + 1) % _data.Length;
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_count > MaxLatency + count)
            {
                int skip = _count - MaxLatency;
                _read = (_read + skip) % _data.Length;
                _count -= skip;
            }
            int n = Math.Min(count, _count);
            for (int i = 0; i < n; i++)
            {
                buffer[offset + i] = _data[_read];
                _read = (_read + 1) % _data.Length;
            }
            _count -= n;
            Array.Clear(buffer, offset + n, count - n);
        }
        return count;
    }
}
