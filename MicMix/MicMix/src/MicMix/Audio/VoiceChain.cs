using NAudio.Dsp;

namespace MicMix.Audio;

/// <summary>
/// Mono effect chain built from a <see cref="VoicePreset"/>. Not thread safe;
/// build a new one when the preset changes.
/// </summary>
public sealed class VoiceChain
{
    private readonly VoicePreset _p;
    private readonly BiQuadFilter? _highPass;
    private readonly BiQuadFilter? _lowPass;
    private readonly BiQuadFilter? _band;
    private readonly PitchShifter? _pitch;
    private readonly float[]? _comb;
    private int _combPos;
    private readonly double _ringStep;
    private double _ringPhase;
    private readonly float _driveNorm;

    public VoiceChain(VoicePreset preset, int sampleRate)
    {
        _p = preset;
        if (preset.HighPassHz > 0) _highPass = BiQuadFilter.HighPassFilter(sampleRate, preset.HighPassHz, 0.707f);
        if (preset.LowPassHz > 0) _lowPass = BiQuadFilter.LowPassFilter(sampleRate, preset.LowPassHz, 0.707f);
        if (preset.BandHz > 0 && preset.BandQ > 0) _band = BiQuadFilter.BandPassFilterConstantPeakGain(sampleRate, preset.BandHz, preset.BandQ);
        if (Math.Abs(preset.Pitch - 1f) > 0.01f) _pitch = new PitchShifter(sampleRate, preset.Pitch);
        if (preset.CombMs > 0) _comb = new float[Math.Max(1, (int)(sampleRate * preset.CombMs / 1000f))];
        _ringStep = 2 * Math.PI * preset.RingHz / sampleRate;
        _driveNorm = preset.Drive > 0 ? 1f / MathF.Tanh(preset.Drive) : 1f;
    }

    public bool IsBypass => ReferenceEquals(_p, VoicePreset.Off);

    public void Process(float[] buffer, int offset, int count)
    {
        if (IsBypass) return;

        for (int i = offset; i < offset + count; i++)
        {
            float x = buffer[i];

            if (_highPass != null) x = _highPass.Transform(x);
            if (_pitch != null) x = _pitch.Process(x);

            if (_p.RingDepth > 0)
            {
                float carrier = (float)Math.Sin(_ringPhase);
                _ringPhase += _ringStep;
                if (_ringPhase > 2 * Math.PI) _ringPhase -= 2 * Math.PI;
                x *= 1f - _p.RingDepth + _p.RingDepth * carrier;
            }

            if (_band != null) x = _band.Transform(x);

            if (_comb != null)
            {
                float delayed = _comb[_combPos];
                float y = x + delayed * _p.CombFeedback;
                _comb[_combPos] = y;
                if (++_combPos >= _comb.Length) _combPos = 0;
                x = y * (1f - _p.CombFeedback * 0.5f);
            }

            if (_p.Drive > 0) x = MathF.Tanh(x * _p.Drive) * _driveNorm;
            if (_lowPass != null) x = _lowPass.Transform(x);

            x *= _p.Gain;
            buffer[i] = Math.Clamp(x, -1f, 1f);
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
        private readonly double _step;
        private double _phase;
        private int _write;

        public PitchShifter(int sampleRate, float ratio)
        {
            _window = sampleRate * 30 / 1000;
            _buf = new float[_window * 2 + 4];
            _step = (1.0 - ratio) / _window;
        }

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
            int i0 = (int)pos;
            int i1 = i0 + 1 >= _buf.Length ? 0 : i0 + 1;
            float frac = (float)(pos - i0);
            return _buf[i0] + (_buf[i1] - _buf[i0]) * frac;
        }
    }
}
