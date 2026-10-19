# MicMix

Play music or any app's audio through your microphone, and change your voice while you're at it. Works with Discord, Zoom, Teams, games, or anything else that uses a mic.

### [⬇ Download MicMix.exe (Windows)](https://github.com/MasonHeadley/MicMix/releases/latest/download/MicMix.exe)

No installer, just run it. You also need the free **[VB-Cable virtual audio driver](https://vb-audio.com/Cable/)** (one-time setup, see below).

---

## What it does

- **Share an app's audio over your mic.** Pick Spotify, a browser, a game, etc. and people on your call hear it alongside your voice. Only that app is shared, not your whole PC.
- **Voice effects.** B1 Battle Droid, Robot, Radio, Chipmunk and Deep.
- **Separate volume sliders** for your voice and the shared app, plus mute buttons and level meters.
- **Hear yourself** in your headphones to check how the effect sounds.

```
 mic ──> voice effect ──┐
                        ├──> mixer ──> CABLE Input  ==>  "CABLE Output" mic in Discord/Zoom/etc.
 app audio (one app) ───┘
```

## Requirements

- Windows 11, or Windows 10 build 20348 or newer
- [VB-Cable](https://vb-audio.com/Cable/) (free)

## Setup (one time)

1. Download VB-Cable from [vb-audio.com/Cable](https://vb-audio.com/Cable/) and extract the zip.
2. Right-click `VBCABLE_Setup_x64.exe` → **Run as administrator** → **Install Driver**.
3. Restart your PC.
4. Download [MicMix.exe](https://github.com/MasonHeadley/MicMix/releases/latest/download/MicMix.exe) and put it anywhere you like (Desktop is fine).

Windows may show a "Windows protected your PC" warning the first time because the app isn't signed. Click **More info → Run anyway**.

## How to use it

1. Open **MicMix**.
2. **Microphone:** pick your real mic.
3. **Voice effect:** pick one, or leave it on Off.
4. **Share app audio:** pick the app whose sound you want people to hear (hit **Refresh** if it's not listed). Choose "Nothing" for voice only.
5. **Send to:** leave it on **CABLE Input**.
6. Press **Start**.
7. In your call app, open voice settings and set the **input device / microphone** to **CABLE Output (VB-Audio Virtual Cable)**.

That's it. You can change the effect, the shared app, and the volumes while it's running.

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
| My voice is delayed in "Hear myself" | A small delay is normal for the monitor; the call itself isn't affected. |

## Building from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Run `build.bat`; the exe ends up in `publish\`.

Releases are built automatically: publishing a new release on GitHub (e.g. `v0.2.0`) builds the exe and attaches it to that release.
