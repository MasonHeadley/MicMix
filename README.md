# MicMix

Play music or any app's audio through your microphone, and change your voice while you're at it. Works with Discord, Zoom, Teams, games, or anything else that uses a mic.

### [⬇ Download MicMix.exe (Windows)](https://github.com/MasonHeadley/MicMix/releases/latest/download/MicMix.exe)

Optional: [⬇ MicMix-AI.zip](https://github.com/MasonHeadley/MicMix/releases/latest/download/MicMix-AI.zip), the AI voices pack (see [AI voices](#ai-voices)).

No installer, just run it. You also need the free **[VB-Cable virtual audio driver](https://vb-audio.com/Cable/)** (one-time setup, see below).

---

## What it does

- **Share an app's audio over your mic.** Pick Spotify, a browser, a game, etc. and people on your call hear it alongside your voice. Only that app is shared, not your whole PC.
- **A full voice rack.** Faders for pitch, pitch snap (the stair-step autotune sound), tone, wobble, metallic resonance, bit-crush, drive and more, all live while you talk.
- **Presets and profiles.** 18 presets in one dropdown, including B1 Battle Droid, B2 Super Battle Droid, Dalek, Ridiculously Cheap Mic, Glorp Alien Cat, Wallace, Walkie-Talkie, Demon and Retro Game. Tweak the faders and save your own profile to get that exact voice back in two clicks.
- **AI voices.** Install a voice pack and your voice is turned into a trained voice model, like B1 Battle Droid or SpongeBob, with the faders still working on top. It only runs while an AI voice is picked and you're talking, so it costs nothing the rest of the time.
- **Always-on control bar.** Mic and app volume + gain, mute switches, level meters and mic mirroring (hear yourself) at the bottom of every page.
- **Five themes.** Cyberpunk, Vaporwave, Blueprint, Blueprint Night and Studio Wood, from the settings gear.

```
 mic ──> voice effect ──┐
                        ├──> mixer ──> CABLE Input  ==>  "CABLE Output" mic in Discord/Zoom/etc.
 app audio (one app) ───┘
```

## Requirements

- Windows 11, or Windows 10 build 20348 or newer
- Microsoft Edge WebView2 Runtime (already on Windows 11 and most Windows 10 PCs)
- [VB-Cable](https://vb-audio.com/Cable/) (free)

## Setup (one time)

1. Download VB-Cable from [vb-audio.com/Cable](https://vb-audio.com/Cable/) and extract the zip.
2. Right-click `VBCABLE_Setup_x64.exe` → **Run as administrator** → **Install Driver**.
3. Restart your PC.
4. Download [MicMix.exe](https://github.com/MasonHeadley/MicMix/releases/latest/download/MicMix.exe) and put it anywhere you like (Desktop is fine).

Windows may show a "Windows protected your PC" warning the first time because the app isn't signed. Click **More info → Run anyway**.

## How to use it

1. Open **MicMix**. The first time, click the **gear** and check your microphone and that **Send to** is **CABLE Input**.
2. Press the **power** button in the bottom bar (it remembers and starts by itself next time).
3. On the **Voice** tab, pick a preset or move the faders. Hit **Save as profile** to keep a voice you like.
4. On the **App Audio** tab, pick the app whose sound you want people to hear. Choose "Nothing" for voice only.
5. In your call app, open voice settings and set the **input device / microphone** to **CABLE Output (VB-Audio Virtual Cable)**.

Everything can be changed while it's running.

### AI voices

1. Download [MicMix-AI.zip](https://github.com/MasonHeadley/MicMix/releases/latest/download/MicMix-AI.zip) (about 425 MB, don't unzip it) and put it next to MicMix.exe, then start MicMix. Or use **Settings → AI voices → Install AI voice pack** and pick the zip.
2. Pick a voice from the **AI voices** part of the voice menu. The first time takes a few seconds to load.
3. Move **AI pitch** until the voice sounds right. Each voice starts at a suggested setting.

AI voices add about a quarter of a second of delay and run on your graphics card. Pick **Off** or a normal preset while gaming and the AI voice unloads completely. Voice credits are under **Settings → Credits**.

**Tips**
- Keep the app volume around 40–60% so people can still hear you over the music.
- Turn off noise suppression in Discord (or set it to "None"), or it may try to filter out the music.
- Don't pick your call app itself as the shared app, or people will hear themselves echo.
- Settings are remembered between launches.

## Troubleshooting

| Problem | Fix |
| --- | --- |
| "No virtual cable found" | VB-Cable isn't installed, or you haven't restarted since installing it. |
| People can't hear anything | Make sure your call app's input is **CABLE Output** and MicMix says *Running*. |
| App audio doesn't come through | Make sure the app is actually playing, then hit Refresh and re-select it. Needs Windows 11 or a recent Windows 10. |
| Spotify pauses itself after about 30 seconds | Discord does this when Spotify is connected to your Discord account. Remove it under Discord > Settings > Connections, or play music from another app. |
| AI voice is choppy | Your PC can't keep up. It runs best on a graphics card; on older PCs pick a normal preset instead. |
| My voice is delayed with mirroring on | A small delay is normal for the mirror; the call itself isn't affected. |

## Versions

The version shows next to the MicMix logo, in the window title and under Settings > About.

- **0.0.XX** goes up with every test build.
- **0.X.00** goes up with every release pushed to GitHub.
- **X.0.00** goes up for a major overhaul.

## Credits

- AI voice models: **B1 Battle Droid** by Discord user 508415387252293656 ([voice-models.com](https://voice-models.com)), **SpongeBob** by [YourLocalWorm](https://huggingface.co/YourLocalWorm/Spongebobmodels).
- AI voice engine: [RVC](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI) with its ContentVec and RMVPE models (MIT), running on [ONNX Runtime](https://github.com/microsoft/onnxruntime) (MIT).
- Audio: [NAudio](https://github.com/naudio/NAudio) (MIT). Virtual cable: [VB-Cable](https://vb-audio.com/Cable/) by VB-Audio.
- Studio Wood texture from [three.js](https://github.com/mrdoob/three.js) (MIT).

## Building from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Run `build.bat`; the exe ends up in `publish\`.

Releases are built automatically: publishing a new release on GitHub (e.g. `v1.0.0`) builds the exe and attaches it to that release. The AI voices pack is too big for the repository, so upload `MicMix-AI.zip` to the release by hand.
