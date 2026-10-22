namespace MicMix.Audio;

/// <summary>
/// Every knob on the voice page. Values are in the units shown in the UI, and a
/// value at its "off" end skips that stage entirely.
/// </summary>
public sealed record VoiceSettings
{
    /// <summary>Semitones, -12..12.</summary>
    public float Pitch { get; init; }

    /// <summary>
    /// Pitch snap, 0..200 %. Locks your pitch to the nearest note, like hard
    /// autotune, so it moves in stair steps instead of sliding. Above 100 % the
    /// steps get bigger, up to whole tones at 200 %.
    /// </summary>
    public float Snap { get; init; }

    /// <summary>Low cut in Hz, 20 (off)..1000.</summary>
    public float LowCut { get; init; } = 20;

    /// <summary>High cut in Hz, 1000..20000 (off).</summary>
    public float HighCut { get; init; } = 20000;

    /// <summary>Center of the tone boost in Hz, 200..5000.</summary>
    public float ToneFreq { get; init; } = 1200;

    /// <summary>Tone boost or cut in dB, -12..18.</summary>
    public float ToneBoost { get; init; }

    /// <summary>Ring modulator frequency in Hz, 2..300. Low values give a tremolo wobble.</summary>
    public float RingFreq { get; init; } = 50;

    /// <summary>Ring modulator amount, 0..100 %.</summary>
    public float RingDepth { get; init; }

    /// <summary>Metallic resonance size in ms, 0.3..15.</summary>
    public float MetalSize { get; init; } = 3;

    /// <summary>Metallic resonance amount, 0..90 %.</summary>
    public float MetalAmount { get; init; }

    /// <summary>Sample-rate reduction factor, 1 (off)..8.</summary>
    public float Crush { get; init; } = 1;

    /// <summary>Bit depth, 4..16 (off).</summary>
    public float Bits { get; init; } = 16;

    /// <summary>Distortion amount, 0..100 %.</summary>
    public float Drive { get; init; }

    /// <summary>Dry/wet mix, 0..100 %.</summary>
    public float Mix { get; init; } = 100;

    /// <summary>Effect output level, 0..200 %.</summary>
    public float Level { get; init; } = 100;

    /// <summary>Folder name of the AI voice to use, or null for none. The faders still apply on top.</summary>
    public string? AiVoice { get; init; }

    /// <summary>Semitones to move your pitch before the AI voice, -12..12.</summary>
    public float AiKey { get; init; }

    /// <summary>True when the fader rack itself does nothing, whatever the AI voice.</summary>
    public bool RackIsNeutral => this with { AiVoice = null, AiKey = 0 } == Neutral;

    public static readonly VoiceSettings Neutral = new();

    public static readonly IReadOnlyList<(string Name, VoiceSettings Settings)> Presets = new (string, VoiceSettings)[]
    {
        ("Off", Neutral),

        // Tuned from a reference clip: a moderate pitch-up snapped hard to notes (the
        // stair-step sound), no bass, a bright 2.5 kHz bump and nothing above about 5 kHz.
        ("B1 Battle Droid", new VoiceSettings
        {
            Pitch = 5, Snap = 140, LowCut = 250, HighCut = 5000,
            ToneFreq = 2600, ToneBoost = 6,
            Drive = 10, Level = 100,
        }),

        // Big, low and metallic, with some grit.
        ("B2 Super Battle Droid", new VoiceSettings
        {
            Pitch = -3, Snap = 60, LowCut = 110, HighCut = 5000,
            ToneFreq = 400, ToneBoost = 4, MetalSize = 2.2f, MetalAmount = 35,
            Drive = 30, Level = 140,
        }),

        // Harsh, buzzy assassin droid.
        ("IG Assassin Droid", new VoiceSettings
        {
            Pitch = -1, LowCut = 200, HighCut = 6000, RingFreq = 90, RingDepth = 60,
            MetalSize = 3, MetalAmount = 30, Crush = 3, Bits = 8, Level = 135,
        }),

        ("Robot", new VoiceSettings
        {
            LowCut = 120, RingFreq = 50, RingDepth = 100,
            MetalSize = 9, MetalAmount = 50, Drive = 25, Level = 140,
        }),

        // Classic ring modulator at 30 Hz, the old TV robot-overlord sound.
        ("Dalek", new VoiceSettings
        {
            LowCut = 150, HighCut = 7000, RingFreq = 30, RingDepth = 100,
            ToneFreq = 1200, ToneBoost = 4, Drive = 35, Level = 150,
        }),

        // Snapped to notes with a slight shimmer: a calm, slightly unhinged AI.
        ("Testing Facility AI", new VoiceSettings
        {
            Pitch = 1, Snap = 100, LowCut = 180, HighCut = 9000,
            ToneFreq = 2500, ToneBoost = 4, MetalSize = 1.2f, MetalAmount = 30, Level = 115,
        }),

        // Tiny, tinny, overdriven and low-resolution, like a $2 mic from a cereal box.
        ("Ridiculously Cheap Mic", new VoiceSettings
        {
            LowCut = 550, HighCut = 3000, ToneFreq = 1800, ToneBoost = 10,
            Crush = 3, Bits = 6, Drive = 70, Level = 90,
        }),

        // High, squeaky and gargly, with a wobble on everything it says.
        ("Glorp Alien Cat", new VoiceSettings
        {
            Pitch = 9, Snap = 40, LowCut = 250, HighCut = 9000,
            ToneFreq = 2200, ToneBoost = 5, RingFreq = 11, RingDepth = 45,
            MetalSize = 0.6f, MetalAmount = 25, Level = 115,
        }),

        // A warm, cosy, slightly nasal old-gentleman tone. The accent is up to you.
        ("Wallace", new VoiceSettings
        {
            Pitch = 1, LowCut = 110, HighCut = 8000, ToneFreq = 900, ToneBoost = 5,
            Drive = 6, Level = 110,
        }),

        ("Radio", new VoiceSettings
        {
            LowCut = 400, HighCut = 3400, ToneFreq = 1500, ToneBoost = 8,
            Drive = 60, Level = 120,
        }),

        ("Walkie-Talkie", new VoiceSettings
        {
            LowCut = 500, HighCut = 2800, ToneFreq = 1400, ToneBoost = 9,
            Crush = 2, Bits = 8, Drive = 75, Level = 110,
        }),

        ("Megaphone", new VoiceSettings
        {
            LowCut = 650, HighCut = 4500, ToneFreq = 1900, ToneBoost = 12,
            MetalSize = 2.5f, MetalAmount = 20, Drive = 55, Level = 120,
        }),

        ("Chipmunk", new VoiceSettings { Pitch = 10, Level = 110 }),

        ("Helium", new VoiceSettings { Pitch = 6, LowCut = 200, ToneFreq = 2800, ToneBoost = 6, Level = 110 }),

        ("Deep", new VoiceSettings { Pitch = -6, HighCut = 6000, ToneFreq = 250, ToneBoost = 4, Level = 130 }),

        // Way down, gritty and metallic.
        ("Demon", new VoiceSettings
        {
            Pitch = -10, HighCut = 5000, ToneFreq = 180, ToneBoost = 6,
            RingFreq = 70, RingDepth = 20, MetalSize = 4, MetalAmount = 35, Drive = 40, Level = 150,
        }),

        // Fast warble, like talking through a fan.
        ("Fan Blades", new VoiceSettings { RingFreq = 14, RingDepth = 80, Level = 130 }),

        // A distant 8-bit game character.
        ("Retro Game", new VoiceSettings
        {
            Pitch = 3, Snap = 100, LowCut = 200, HighCut = 6000, Crush = 6, Bits = 5, Level = 100,
        }),
    };
}
