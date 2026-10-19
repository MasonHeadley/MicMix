namespace MicMix.Audio;

/// <summary>
/// Settings for one voice effect. Stages with a zero value are skipped.
/// </summary>
public sealed record VoicePreset(
    string Name,
    float HighPassHz = 0,
    float LowPassHz = 0,
    float Pitch = 1f,
    float RingHz = 0,
    float RingDepth = 0,
    float BandHz = 0,
    float BandQ = 0,
    float CombMs = 0,
    float CombFeedback = 0,
    float Drive = 0,
    float Gain = 1f)
{
    public override string ToString() => Name;

    public static readonly VoicePreset Off = new("Off");

    // Thin, nasal and a bit buzzy: pitched up, squeezed through a narrow band,
    // with a short metallic comb and a light ring mod for that radio-chassis sound.
    public static readonly VoicePreset BattleDroid = new(
        "B1 Battle Droid",
        HighPassHz: 350, LowPassHz: 5000,
        Pitch: 1.3f,
        RingHz: 65, RingDepth: 0.3f,
        BandHz: 1800, BandQ: 0.7f,
        CombMs: 3.2f, CombFeedback: 0.35f,
        Drive: 2.5f, Gain: 3f);

    public static readonly VoicePreset Robot = new(
        "Robot",
        HighPassHz: 120,
        RingHz: 50, RingDepth: 1f,
        CombMs: 9f, CombFeedback: 0.5f,
        Drive: 1.5f, Gain: 1.4f);

    public static readonly VoicePreset Radio = new(
        "Radio",
        HighPassHz: 400, LowPassHz: 3400,
        BandHz: 1500, BandQ: 1.1f,
        Drive: 4f, Gain: 1.6f);

    public static readonly VoicePreset Chipmunk = new("Chipmunk", Pitch: 1.75f, Gain: 1.1f);

    public static readonly VoicePreset Deep = new("Deep", Pitch: 0.72f, LowPassHz: 6000, Gain: 1.3f);

    public static IReadOnlyList<VoicePreset> All { get; } = new[] { Off, BattleDroid, Robot, Radio, Chipmunk, Deep };
}
