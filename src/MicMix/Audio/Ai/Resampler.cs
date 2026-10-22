namespace MicMix.Audio.Ai;

/// <summary>
/// Streaming windowed-sinc resampler for mono float audio. Keeps its own history,
/// so it can be fed blocks of any size and stays continuous across them.
/// </summary>
public sealed class Resampler
{
    private const int HalfTaps = 16;

    private readonly double _step;     // input samples per output sample
    private readonly double _cutoff;   // relative to the input Nyquist
    private readonly List<float> _hist = new();
    private double _pos;               // next output position, in input samples, relative to _hist[0]

    public Resampler(int fromRate, int toRate)
    {
        FromRate = fromRate;
        ToRate = toRate;
        _step = (double)fromRate / toRate;
        _cutoff = Math.Min(1.0, (double)toRate / fromRate) * 0.95;
        // Start with silence before the first sample so the filter has history.
        for (int i = 0; i < HalfTaps; i++) _hist.Add(0f);
        _pos = HalfTaps;
    }

    public int FromRate { get; }
    public int ToRate { get; }

    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        if (FromRate == ToRate)
        {
            foreach (var s in input) output.Add(s);
            return;
        }

        foreach (var s in input) _hist.Add(s);

        while (_pos + HalfTaps < _hist.Count)
        {
            int center = (int)Math.Floor(_pos);
            double frac = _pos - center;
            double sum = 0, norm = 0;
            for (int k = -HalfTaps + 1; k <= HalfTaps; k++)
            {
                double t = k - frac;
                double w = Kernel(t);
                sum += _hist[center + k] * w;
                norm += w;
            }
            output.Add((float)(norm != 0 ? sum / norm : 0));
            _pos += _step;
        }

        // Drop history we no longer need.
        int keep = (int)Math.Floor(_pos) - HalfTaps;
        if (keep > 0)
        {
            _hist.RemoveRange(0, keep);
            _pos -= keep;
        }
    }

    private double Kernel(double t)
    {
        double x = t * _cutoff;
        double sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
        double n = (t + HalfTaps) / (2.0 * HalfTaps);
        double blackman = n <= 0 || n >= 1 ? 0 : 0.42 - 0.5 * Math.Cos(2 * Math.PI * n) + 0.08 * Math.Cos(4 * Math.PI * n);
        return sinc * blackman;
    }
}
