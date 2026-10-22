using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MicMix.Audio.Ai;

/// <summary>
/// Converts speech to an AI voice model (RVC v2) one block at a time.
/// Feed it 16 kHz audio with <see cref="ProcessBlock"/>; each call returns
/// the same length of converted audio at the voice's own sample rate.
/// </summary>
public sealed class AiVoiceEngine : IDisposable
{
    public const int Rate16k = 16000;
    public const int Hop16k = 160;          // one pitch/feature frame = 10 ms
    private const float F0Min = 50, F0Max = 1100;

    private readonly InferenceSession _hubert;
    private readonly InferenceSession _rmvpe;
    private readonly InferenceSession _voice;
    private readonly int _block16k, _context16k, _fade16k, _search16k, _window16k;
    private readonly int _block, _fade, _search, _hop;   // at the voice's rate
    private readonly int _skipFrames;
    private readonly float[] _window;                    // rolling 16 kHz input
    private readonly float[] _solaTail;
    private readonly float[] _fadeIn, _fadeOut;
    private bool _first = true;
    private float _gain = 1f;

    /// <param name="blockMs">How much new audio each call converts. Smaller is lower delay but more work.</param>
    public AiVoiceEngine(AiVoiceInfo voice, string sharedDir, bool useGpu, int blockMs = 200)
    {
        VoiceRate = voice.SampleRate;
        _hop = VoiceRate / 100;

        InferenceSession? hubert = null, rmvpe = null, model = null;
        bool gpu = false;
        foreach (bool tryGpu in useGpu ? new[] { true, false } : new[] { false })
        {
            try
            {
                var (options, ok) = CreateOptions(tryGpu);
                hubert = new InferenceSession(Path.Combine(sharedDir, "hubert.onnx"), options);
                rmvpe = new InferenceSession(Path.Combine(sharedDir, "rmvpe.onnx"), options);
                model = new InferenceSession(voice.ModelPath, options);
                gpu = ok;
                break;
            }
            catch when (tryGpu)
            {
                // The graphics card couldn't take it; try again on the CPU.
                hubert?.Dispose(); rmvpe?.Dispose(); model?.Dispose();
                hubert = rmvpe = model = null;
            }
        }
        UsingGpu = gpu;
        _hubert = hubert!;
        _rmvpe = rmvpe!;
        _voice = model!;

        _block16k = Round160(Rate16k * blockMs / 1000);
        _context16k = Round160(Rate16k * 600 / 1000);   // history the models look back on
        _fade16k = Round160(Rate16k * 40 / 1000);
        _search16k = Hop16k;
        _window16k = _context16k + _fade16k + _search16k + _block16k;
        _window = new float[_window16k];

        // Only the newest frames get turned into sound; the rest is context. A few spare
        // frames in front keep the decoder's edge away from the audio we use.
        int keepFrames = (_block16k + _fade16k + _search16k) / Hop16k + 4;
        _skipFrames = Math.Max(0, _window16k / Hop16k - keepFrames);

        _block = _block16k / Hop16k * _hop;
        _fade = _fade16k / Hop16k * _hop;
        _search = _search16k / Hop16k * _hop;
        _solaTail = new float[_fade];
        _fadeIn = new float[_fade];
        _fadeOut = new float[_fade];
        for (int i = 0; i < _fade; i++)
        {
            double w = Math.Sin(0.5 * Math.PI * (i + 0.5) / _fade);
            _fadeIn[i] = (float)(w * w);
            _fadeOut[i] = 1f - _fadeIn[i];
        }
    }

    public int VoiceRate { get; }
    public bool UsingGpu { get; }
    public int BlockSize16k => _block16k;

    /// <summary>Semitones to move your pitch before it goes into the model.</summary>
    public float Key { get; set; }

    private static int Round160(int n) => Math.Max(Hop16k, n / Hop16k * Hop16k);

    private static (SessionOptions, bool) CreateOptions(bool useGpu)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (useGpu)
        {
            try
            {
                // DirectML wants these two off.
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.AppendExecutionProvider_DML(0);
                return (options, true);
            }
            catch
            {
                // No DirectML (or no usable graphics card): fall back to the CPU.
            }
        }
        options.IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        return (options, false);
    }

    /// <summary>
    /// Takes in a quiet block without converting it, so nothing runs on the
    /// graphics card while you aren't talking. The next real block starts fresh.
    /// </summary>
    public void Skip(ReadOnlySpan<float> input)
    {
        Array.Copy(_window, _block16k, _window, 0, _window16k - _block16k);
        input.CopyTo(_window.AsSpan(_window16k - _block16k));
        _first = true;
    }

    /// <summary>
    /// Converts one block. <paramref name="input"/> must be exactly
    /// <see cref="BlockSize16k"/> samples; returns that much time at <see cref="VoiceRate"/>.
    /// </summary>
    public float[] ProcessBlock(ReadOnlySpan<float> input)
    {
        if (input.Length != _block16k) throw new ArgumentException("Wrong block size.", nameof(input));

        // Slide the window and append the new audio.
        Array.Copy(_window, _block16k, _window, 0, _window16k - _block16k);
        input.CopyTo(_window.AsSpan(_window16k - _block16k));

        float inRms = Rms(input);
        int frames = _window16k / Hop16k;
        var feats = ContentFeatures(frames);
        var (pitch, pitchf) = Pitch(frames);
        var audio = Synthesize(feats, pitch, pitchf, frames);

        // The newest block plus the crossfade and search room sits at the end.
        int need = _block + _fade + _search;
        int start = Math.Max(0, audio.Length - need);
        var tail = audio.AsSpan(start);

        var output = new float[_block];
        if (_first)
        {
            tail.Slice(0, Math.Min(_block, tail.Length)).CopyTo(output);
            _first = false;
            int s0 = Math.Min(_block + _fade, tail.Length) - _fade;
            if (s0 >= 0) tail.Slice(s0, _fade).CopyTo(_solaTail);
            return MatchLoudness(output, inRms);
        }

        // Line the new block up with the end of the last one, then crossfade.
        int offset = BestOffset(tail);
        for (int i = 0; i < _block && offset + i < tail.Length; i++)
        {
            float s = tail[offset + i];
            if (i < _fade) s = s * _fadeIn[i] + _solaTail[i] * _fadeOut[i];
            output[i] = s;
        }
        int t0 = offset + _block;
        for (int i = 0; i < _fade; i++)
            _solaTail[i] = t0 + i < tail.Length ? tail[t0 + i] : 0f;
        return MatchLoudness(output, inRms);
    }

    private static float Rms(ReadOnlySpan<float> a)
    {
        double sum = 0;
        foreach (var v in a) sum += v * v;
        return (float)Math.Sqrt(sum / Math.Max(1, a.Length));
    }

    /// <summary>
    /// The models come out much quieter than the voice that went in, so turn each block up
    /// to roughly your own loudness. The gain glides across the block so it never clicks.
    /// </summary>
    private float[] MatchLoudness(float[] output, float inRms)
    {
        float outRms = Rms(output), peak = 0;
        foreach (var v in output) peak = Math.Max(peak, Math.Abs(v));
        float target = outRms > 1e-4f ? Math.Clamp(inRms / outRms, 1f, 8f) : _gain;
        if (peak > 1e-4f) target = Math.Min(target, Math.Max(1f, 0.9f / peak));   // leave headroom
        for (int i = 0; i < output.Length; i++)
        {
            float g = _gain + (target - _gain) * i / output.Length;
            output[i] *= g;
        }
        _gain = target;
        return output;
    }

    private int BestOffset(ReadOnlySpan<float> tail)
    {
        int best = 0;
        double bestScore = double.NegativeInfinity;
        for (int off = 0; off <= _search && off + _fade <= tail.Length; off++)
        {
            double dot = 0, energy = 1e-8;
            for (int i = 0; i < _fade; i++)
            {
                float a = tail[off + i];
                dot += a * _solaTail[i];
                energy += a * a;
            }
            double score = dot / Math.Sqrt(energy);
            if (score > bestScore) { bestScore = score; best = off; }
        }
        return best;
    }

    /// <summary>HuBERT/ContentVec features, doubled to one per 10 ms frame.</summary>
    private DenseTensor<float> ContentFeatures(int frames)
    {
        var audio = new DenseTensor<float>(_window.ToArray(), new[] { 1, _window16k });
        using var results = _hubert.Run(new[] { NamedOnnxValue.CreateFromTensor("audio", audio) });
        var raw = results.First().AsTensor<float>();
        int t = raw.Dimensions[1], dim = raw.Dimensions[2];

        var feats = new DenseTensor<float>(new[] { 1, frames, dim });
        for (int f = 0; f < frames; f++)
        {
            int src = Math.Min(f / 2, t - 1);
            for (int d = 0; d < dim; d++) feats[0, f, d] = raw[0, src, d];
        }
        return feats;
    }

    private static readonly double MelMin = 1127 * Math.Log(1 + F0Min / 700.0);
    private static readonly double MelMax = 1127 * Math.Log(1 + F0Max / 700.0);

    /// <summary>RMVPE pitch per 10 ms frame, shifted by <see cref="Key"/>, as coarse bins and Hz.</summary>
    private (DenseTensor<long>, DenseTensor<float>) Pitch(int frames)
    {
        var audio = new DenseTensor<float>(_window.ToArray(), new[] { 1, _window16k });
        using var results = _rmvpe.Run(new[] { NamedOnnxValue.CreateFromTensor("audio", audio) });
        var sal = results.First().AsTensor<float>();
        int n = sal.Dimensions[0];

        var f0 = new float[frames];
        for (int f = 0; f < frames; f++) f0[f] = f < n ? DecodeFrame(sal, f) : 0f;

        // Fill unvoiced gaps from their neighbours, like the reference pipeline.
        FillGaps(f0);

        float shift = MathF.Pow(2f, Key / 12f);
        var pitch = new DenseTensor<long>(new[] { 1, frames });
        var pitchf = new DenseTensor<float>(new[] { 1, frames });
        for (int f = 0; f < frames; f++)
        {
            float hz = f0[f] * shift;
            pitchf[0, f] = hz;
            double mel = 1127 * Math.Log(1 + hz / 700.0);
            if (mel > 0) mel = (mel - MelMin) * 254 / (MelMax - MelMin) + 1;
            pitch[0, f] = (long)Math.Round(Math.Clamp(mel, 1, 255));
        }
        return (pitch, pitchf);
    }

    private static float DecodeFrame(Tensor<float> sal, int f)
    {
        const int bins = 360;
        int center = 0;
        float max = float.NegativeInfinity;
        for (int b = 0; b < bins; b++)
        {
            float v = sal[f, b];
            if (v > max) { max = v; center = b; }
        }
        if (max <= 0.03f) return 0f;

        double num = 0, den = 0;
        for (int b = Math.Max(0, center - 4); b <= Math.Min(bins - 1, center + 4); b++)
        {
            double cents = 20 * b + 1997.3794084376191;
            num += sal[f, b] * cents;
            den += sal[f, b];
        }
        double c = den > 0 ? num / den : 0;
        return (float)(10 * Math.Pow(2, c / 1200));
    }

    private static void FillGaps(float[] f0)
    {
        int firstVoiced = Array.FindIndex(f0, v => v > 0);
        if (firstVoiced < 0) return;
        for (int i = 0; i < firstVoiced; i++) f0[i] = f0[firstVoiced];
        int last = firstVoiced;
        for (int i = firstVoiced + 1; i < f0.Length; i++)
        {
            if (f0[i] <= 0) continue;
            for (int j = last + 1; j < i; j++)
                f0[j] = f0[last] + (f0[i] - f0[last]) * (j - last) / (float)(i - last);
            last = i;
        }
        for (int j = last + 1; j < f0.Length; j++) f0[j] = f0[last];
    }

    private float[] Synthesize(DenseTensor<float> feats, DenseTensor<long> pitch, DenseTensor<float> pitchf, int frames)
    {
        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("phone", feats),
            NamedOnnxValue.CreateFromTensor("phone_lengths", new DenseTensor<long>(new long[] { frames }, new[] { 1 })),
            NamedOnnxValue.CreateFromTensor("pitch", pitch),
            NamedOnnxValue.CreateFromTensor("pitchf", pitchf),
            NamedOnnxValue.CreateFromTensor("sid", new DenseTensor<long>(new long[] { 0 }, new[] { 1 })),
            NamedOnnxValue.CreateFromTensor("skip", new DenseTensor<long>(new long[] { _skipFrames }, new[] { 1 })),
        };
        using var results = _voice.Run(inputs);
        return results.First().AsTensor<float>().ToArray();
    }

    public void Dispose()
    {
        _hubert.Dispose();
        _rmvpe.Dispose();
        _voice.Dispose();
    }
}
