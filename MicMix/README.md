# MicMix

A small Windows app that sends your mic **plus** audio from another app (Spotify, a browser, a game) into a virtual microphone, so people on a call hear both. It also has a few voice effects, including a B1 battle droid preset.

```
 mic ──> voice effect ──┐
                        ├──> mixer ──> CABLE Input  ==>  "CABLE Output" mic in Discord/Zoom/etc.
 app audio (one app) ───┘
```

## Requirements

- Windows 11, or Windows 10 build 20348 or newer (needed for per-app audio capture)
- [VB-Cable](https://vb-audio.com/Cable/) (free virtual audio cable)

## Setup

1. Install VB-Cable and reboot.
2. Run `MicMix.exe`.
3. Pick your real microphone, a voice effect, and the app to share.
4. Leave **Send to** on `CABLE Input` and press **Start**.
5. In your call app, set the input/microphone device to `CABLE Output`.

Tick **Hear myself** to monitor your processed voice in your headphones. App audio isn't included in the monitor since you already hear it.

## Voice effects

| Preset | What it does |
| --- | --- |
| B1 Battle Droid | Pitched up, narrow band-pass, short metallic comb, light ring mod and drive |
| Robot | Full ring modulation with a resonant comb |
| Radio | Telephone-band filter with saturation |
| Chipmunk / Deep | Pitch shift up or down |

Presets live in `src/MicMix/Audio/VoicePresets.cs` and are easy to tweak.

## Building

Needs the .NET 8 SDK.

```
build.bat
```

The single-file exe ends up in `publish\`. GitHub Actions also builds it on every push to `main` (see the workflow run's artifacts).

## Notes

- Settings are saved to `%AppData%\MicMix\settings.json`.
- Capturing a browser or Electron app picks up its child processes too, so choosing the main window works.
- Don't pick your call app (Discord, etc.) as the shared app, or people will hear themselves.
