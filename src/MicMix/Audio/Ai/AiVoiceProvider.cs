using System.Diagnostics;
using NAudio.Wave;

namespace MicMix.Audio.Ai;

/// <summary>
/// Sits in the mic chain and swaps your voice for an AI voice. With no AI voice
/// picked it passes audio straight through and nothing is loaded. The model runs
/// on its own thread, so a slow block delays your voice slightly but never
/// stalls the rest of the audio.
/// </summary>
internal sealed class AiVoiceProvider : ISampleProvider, IDisposable
{
    private const float SilenceRms = 0.004f;   // about -48 dBFS

    private readonly ISampleProvider _source;
    private readonly int _rate;
    private readonly object _lock = new();
    private readonly Queue<float> _in = new();
    private readonly Queue<float> _out = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;

    private AiVoiceEngine? _engine;      // only touched on the worker thread
    private AiVoiceEngine? _pending;     // handed over from the loader
    private bool _pendingSet;
    private volatile bool _active;       // an AI voice is picked (loaded or loading)
    private volatile bool _ready;
    private bool _started;
    private int _loadId;
    private float _key;
    private int _blockOut;

    /// <summary>Raised from a background thread with a short message for the status pill.</summary>
    public event Action<string>? Status;

    public AiVoiceProvider(ISampleProvider source)
    {
        _source = source;
        _rate = source.WaveFormat.SampleRate;
        _thread = new Thread(Worker) { IsBackground = true, Name = "AI voice", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public bool UseGpu { get; set; } = true;

    /// <summary>Picks an AI voice, or null for none. Loading happens in the background.</summary>
    public void SetVoice(AiVoiceInfo? voice, float key)
    {
        int id = Interlocked.Increment(ref _loadId);
        _key = key;
        if (voice == null)
        {
            _active = false;
            _ready = false;
            Hand(null);
            return;
        }

        if (_active && _ready && _currentId == voice.Id)
        {
            // Same voice, only the pitch moved.
            return;
        }

        _active = true;
        _ready = false;
        Hand(null);
        Status?.Invoke("Loading AI voice…");
        Task.Run(() =>
        {
            try
            {
                var engine = new AiVoiceEngine(voice, AiVoiceLibrary.SharedDir, UseGpu) { Key = _key };
                if (id != _loadId) { engine.Dispose(); return; }
                _currentId = voice.Id;
                Hand(engine);
                _ready = true;
                Status?.Invoke(engine.UsingGpu ? "AI voice ready" : "AI voice ready (running on CPU)");
            }
            catch (Exception ex)
            {
                if (id == _loadId)
                {
                    _active = false;
                    Status?.Invoke("Couldn't load AI voice: " + ex.Message);
                }
            }
        });
    }

    private volatile string? _currentId;

    public float Key
    {
        get => _key;
        set { _key = value; }
    }

    private void Hand(AiVoiceEngine? engine)
    {
        lock (_lock)
        {
            _pending?.Dispose();
            _pending = engine;
            _pendingSet = true;
            _in.Clear();
            _out.Clear();
            _started = false;
        }
        _wake.Set();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (!_active) return read;

        lock (_lock)
        {
            if (!_ready)
            {
                // Loading: stay quiet rather than letting your real voice through.
                Array.Clear(buffer, offset, read);
                return read;
            }

            for (int i = 0; i < read; i++) _in.Enqueue(buffer[offset + i]);

            // Wait for a block and a little cushion before starting, then keep the delay from growing.
            int cushion = _rate * 60 / 1000;
            if (!_started && _out.Count >= _blockOut + cushion) _started = true;
            if (_started && _out.Count > _blockOut * 3 + cushion)
                while (_out.Count > _blockOut + cushion) _out.Dequeue();

            for (int i = 0; i < read; i++)
                buffer[offset + i] = _started && _out.Count > 0 ? _out.Dequeue() : 0f;
        }
        _wake.Set();
        return read;
    }

    private void Worker()
    {
        Resampler? down = null, up = null;
        var in16 = new List<float>();
        var scratch = new List<float>();
        var chunk = new float[4096];
        var watch = new Stopwatch();
        int slowBlocks = 0, quietBlocks = 2;

        while (!_stopping)
        {
            _wake.WaitOne(50);

            lock (_lock)
            {
                if (_pendingSet)
                {
                    _engine?.Dispose();
                    _engine = _pending;
                    _pending = null;
                    _pendingSet = false;
                    in16.Clear();
                    down = _engine == null ? null : new Resampler(_rate, AiVoiceEngine.Rate16k);
                    up = _engine == null ? null : new Resampler(_engine.VoiceRate, _rate);
                    _blockOut = _engine == null ? 0 : (int)((long)_engine.BlockSize16k * _rate / AiVoiceEngine.Rate16k);
                    slowBlocks = 0;
                }
            }
            var engine = _engine;
            if (engine == null || down == null || up == null) continue;

            // Pull whatever the audio thread has queued.
            while (true)
            {
                int n;
                lock (_lock)
                {
                    n = Math.Min(chunk.Length, _in.Count);
                    for (int i = 0; i < n; i++) chunk[i] = _in.Dequeue();
                }
                if (n == 0) break;
                down.Process(chunk.AsSpan(0, n), in16);
            }

            int block = engine.BlockSize16k;
            while (in16.Count >= block && !_stopping)
            {
                var input = in16.GetRange(0, block).ToArray();
                in16.RemoveRange(0, block);
                engine.Key = _key;

                float rms = 0;
                foreach (var v in input) rms += v * v;
                rms = MathF.Sqrt(rms / input.Length);

                // Only rest after a couple of quiet blocks in a row, so short pauses
                // between words don't chop the end off the last one.
                quietBlocks = rms < SilenceRms ? quietBlocks + 1 : 0;

                float[] converted;
                if (quietBlocks >= 2)
                {
                    engine.Skip(input);
                    converted = new float[block * engine.VoiceRate / AiVoiceEngine.Rate16k];
                }
                else
                {
                    watch.Restart();
                    try
                    {
                        converted = engine.ProcessBlock(input);
                    }
                    catch (Exception ex)
                    {
                        Status?.Invoke("AI voice stopped: " + ex.Message);
                        _active = false;
                        break;
                    }
                    // Taking longer than the audio it covers means it can't keep up.
                    double budget = 1000.0 * block / AiVoiceEngine.Rate16k;
                    if (watch.Elapsed.TotalMilliseconds > budget * 0.95) { if (++slowBlocks == 10) Status?.Invoke("AI voice is too slow on this PC, expect choppy audio"); }
                    else slowBlocks = 0;
                }

                scratch.Clear();
                up.Process(converted, scratch);
                lock (_lock)
                {
                    foreach (var v in scratch) _out.Enqueue(v);
                }
            }
        }
        _engine?.Dispose();
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread.Join(1000);
        lock (_lock) { _pending?.Dispose(); _pending = null; }
    }
}
