using NAudio.Dsp;

namespace MicMix.Audio;

/// <summary>
/// Mono effect chain built from <see cref="VoiceSettings"/>. Not thread safe;
/// build a new one when the settings change.
/// </summary>
public sealed class VoiceChain
{
    private readonly VoiceSettings _s;
    private readonly bool _bypass;
    private readonly BiQuadFilter? _lowCut;
    private readonly BiQuadFilter? _highCut;
    private readonly BiQuadFilter? _tone;
    private readonly PitchShifter? _pitch;
    private readonly PitchTracker? _tracker;
    private readonly float _baseRatio;
    private readonly float _snap;
    private readonly float _snapStep;
    private float _heldNote = float.NaN;

    private readonly float _ringDepth;
    private readonly double _ringStep;
    private double _ringPhase;

    private readonly float[]? _comb;
    private readonly float _combFeedback;
    private int _combPos;

    private readonly int _crush;
    private readonly float _quantStep;
    private int _crushCount;
    private float _crushHold;

    private readonly float _drive;
    private readonly float _driveNorm;
    private readonly float _wet;
    private readonly float _level;

    public VoiceChain(VoiceSettings s, int sampleRate)
    {
        _s = s;
        _bypass = s.RackIsNeutral;

        float nyquist = sampleRate / 2f - 100;
        if (s.LowCut > 25) _lowCut = BiQuadFilter.HighPassFilter(sampleRate, s.LowCut, 0.707f);
        if (s.HighCut < 19500) _highCut = BiQuadFilter.LowPassFilter(sampleRate, Math.Min(s.HighCut, nyquist), 0.707f);
        if (Math.Abs(s.ToneBoost) > 0.1f) _tone = BiQuadFilter.PeakingEQ(sampleRate, s.ToneFreq, 1.2f, s.ToneBoost);
        _baseRatio = MathF.Pow(2f, s.Pitch / 12f);
        _snap = Math.Clamp(s.Snap / 100f, 0f, 1f);
        // Past 100 % the notes it snaps to spread out: 200 % jumps in whole tones.
        _snapStep = 1f + Math.Clamp(s.Snap - 100f, 0f, 100f) / 100f;
        if (Math.Abs(s.Pitch) > 0.05f || _snap > 0) _pitch = new PitchShifter(sampleRate, _baseRatio);
        if (_snap > 0) _tracker = new PitchTracker(sampleRate);

        _ringDepth = Math.Clamp(s.RingDepth / 100f, 0f, 1f);
        _ringStep = 2 * Math.PI * s.RingFreq / sampleRate;

        if (s.MetalAmount > 0.5f)
        {
            _comb = new float[Math.Max(1, (int)(sampleRate * s.MetalSize / 1000f))];
            _combFeedback = Math.Clamp(s.MetalAmount / 100f, 0f, 0.9f);
        }

        _crush = Math.Max(1, (int)MathF.Round(s.Crush));
        int bits = (int)MathF.Round(s.Bits);
        _quantStep = bits < 16 ? 1f / (1 << (Math.Max(2, bits) - 1)) : 0f;

        _drive = Math.Clamp(s.Drive / 100f, 0f, 1f) * 6f;
        _driveNorm = _drive > 0.01f ? 1f / MathF.Tanh(_drive) : 1f;
        _wet = Math.Clamp(s.Mix / 100f, 0f, 1f);
        _level = Math.Clamp(s.Level / 100f, 0f, 2f);
    }

    public bool IsBypass => _bypass;

    public void Process(float[] buffer, int offset, int count)
    {
        if (_bypass) return;

        for (int i = offset; i < offset + count; i++)
        {
            float dry = buffer[i];
            float x = dry;

            if (_lowCut != null) x = _lowCut.Transform(x);

            if (_tracker != null && _tracker.Push(dry))
            {
                float f0 = _tracker.Frequency;
                if (f0 <= 0) _heldNote = float.NaN;
                else
                {
                    // Where the voice would land after the base shift, held on the nearest
                    // step until it clearly moves to another one, so each step stays flat.
                    float target = f0 * _baseRatio;
                    float note = 12f * MathF.Log2(target / 440f);
                    if (float.IsNaN(_heldNote) || Math.Abs(note - _heldNote) > _snapStep * 0.5f + 0.15f)
                        _heldNote = MathF.Round(note / _snapStep) * _snapStep;
                    float correction = MathF.Pow(2f, (_heldNote - note) * _snap / 12f);
                    _pitch!.SetRatio(_baseRatio * correction);
                }
            }

            if (_pitch != null) x = _pitch.Process(x);

            if (_ringDepth > 0)
            {
                float carrier = (float)Math.Sin(_ringPhase);
                _ringPhase += _ringStep;
                if (_ringPhase > 2 * Math.PI) _ringPhase -= 2 * Math.PI;
                x *= 1f - _ringDepth + _ringDepth * carrier;
            }

            if (_tone != null) x = _tone.Transform(x);

            if (_crush > 1 || _quantStep > 0)
            {
                // Sample-and-hold plus coarse quantizing: a cheap speaker/DAC sound.
                if (_crushCount <= 0)
                {
                    _crushHold = _quantStep > 0 ? MathF.Round(x / _quantStep) * _quantStep : x;
                    _crushCount = _crush;
                }
                _crushCount--;
                x = _crushHold;
            }

            if (_comb != null)
            {
                float y = x + _comb[_combPos] * _combFeedback;
                _comb[_combPos] = y;
                if (++_combPos >= _comb.Length) _combPos = 0;
                x = y * (1f - _combFeedback * 0.5f);
            }

            if (_drive > 0.01f) x = MathF.Tanh(x * _drive) * _driveNorm;
            if (_highCut != null) x = _highCut.Transform(x);

            x = (dry * (1f - _wet) + x * _wet) * _level;
            buffer[i] = float.IsFinite(x) ? SoftClip(x) : 0f;
        }
    }

    /// <summary>Leaves normal levels alone and rounds off peaks instead of hard clipping.</summary>
    private static float SoftClip(float x)
    {
        float a = Math.Abs(x);
        if (a <= 0.7f) return x;
        return MathF.CopySign(0.7f + 0.3f * MathF.Tanh((a - 0.7f) / 0.3f), x);
    }

    /// <summary>
    /// Small YIN pitch detector. Works on a 4x downsampled copy of the input and
    /// re-estimates every 10 ms, which is cheap enough to run on any PC.
    /// </summary>
    private sealed class PitchTracker
    {
        private const int Decimate = 4;
        private const float Threshold = 0.15f;
        private readonly int _rate;
        private readonly int _minLag, _maxLag, _window, _hop;
        private readonly float[] _buf;
        private readonly float[] _diff;
        private int _write, _filled, _sinceHop, _decCount;
        private float _decSum;

        public float Frequency { get; private set; }

        public PitchTracker(int sampleRate)
        {
            _rate = sampleRate / Decimate;
            _minLag = _rate / 900;   // highest voice pitch we care about
            _maxLag = _rate / 70;    // lowest
            _window = _rate * 30 / 1000;
            _hop = _rate / 100;
            _buf = new float[_window + _maxLag + 2];
            _diff = new float[_maxLag + 2];
        }

        /// <summary>Feeds one input sample. Returns true when a new estimate is ready.</summary>
        public bool Push(float x)
        {
            _decSum += x;
            if (++_decCount < Decimate) return false;
            float s = _decSum / Decimate;
            _decSum = 0;
            _decCount = 0;

            _buf[_write] = s;
            if (++_write >= _buf.Length) _write = 0;
            if (_filled < _buf.Length) _filled++;
            if (++_sinceHop < _hop || _filled < _buf.Length) return false;
            _sinceHop = 0;

            Frequency = Estimate();
            return true;
        }

        private float At(int i)
        {
            // i = 0 is the oldest sample in the ring.
            int idx = _write + i;
            if (idx >= _buf.Length) idx -= _buf.Length;
            return _buf[idx];
        }

        private float Estimate()
        {
            float energy = 0;
            for (int i = 0; i < _window; i++) { float v = At(i); energy += v * v; }
            if (energy / _window < 1e-5f) return 0; // silence

            float running = 0;
            _diff[0] = 1;
            int best = -1;
            for (int lag = 1; lag <= _maxLag; lag++)
            {
                float sum = 0;
                for (int i = 0; i < _window; i++)
                {
                    float d = At(i) - At(i + lag);
                    sum += d * d;
                }
                running += sum;
                _diff[lag] = running > 0 ? sum * lag / running : 1;
            }

            for (int lag = _minLag; lag < _maxLag; lag++)
            {
                if (_diff[lag] < Threshold)
                {
                    while (lag + 1 < _maxLag && _diff[lag + 1] < _diff[lag]) lag++;
                    best = lag;
                    break;
                }
            }
            if (best < 0) return 0; // unvoiced

            // Parabolic interpolation for a finer lag.
            float a = _diff[best - 1], b = _diff[best], c = _diff[best + 1];
            float denom = a - 2 * b + c;
            float lagF = best + (Math.Abs(denom) > 1e-9f ? 0.5f * (a - c) / denom : 0);
            return _rate / lagF;
        }
    }

    /// <summary>
    /// Two-tap delay-line pitch shifter. Cheap and low latency, with a slightly
    /// grainy character that suits robot voices.
    /// </summary>
    private sealed class PitchShifter
    {
        private readonly float[] _buf;
        private readonly int _window;
        private double _step;
        private double _phase;
        private int _write;

        public PitchShifter(int sampleRate, float ratio)
        {
            _window = sampleRate * 25 / 1000;
            _buf = new float[_window * 2 + 4];
            _step = (1.0 - ratio) / _window;
        }

        public void SetRatio(float ratio) => _step = (1.0 - ratio) / _window;

        public float Process(float x)
        {
            _buf[_write] = x;

            _phase += _step;
            if (_phase >= 1) _phase -= 1;
            if (_phase < 0) _phase += 1;

            double phase2 = _phase + 0.5;
            if (phase2 >= 1) phase2 -= 1;

            float y = Tap(_phase) * Window(_phase) + Tap(phase2) * Window(phase2);

            if (++_write >= _buf.Length) _write = 0;
            return y;
        }

        private static float Window(double phase) => (float)(1 - Math.Abs(2 * phase - 1));

        private float Tap(double phase)
        {
            double pos = _write - phase * _window;
            if (pos < 0) pos += _buf.Length;
            int i0 = Math.Min((int)pos, _buf.Length - 1);
            int i1 = i0 + 1 >= _buf.Length ? 0 : i0 + 1;
            float frac = (float)(pos - i0);
            return _buf[i0] + (_buf[i1] - _buf[i0]) * frac;
        }
    }
}
