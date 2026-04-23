# OsuHitsoundTuner (Windows)

A Windows-only WPF app for locating osu! skins and tuning osu!standard hitsound files.

## Features

- Auto-detects default osu! skins folder (`%LOCALAPPDATA%/osu!/Skins`)
- Lets you browse/load skin folders manually
- Skin dropdown and osu!standard hitsound dropdown
- Detects hitsound files in `.wav`, `.ogg`, `.mp3`
- Waveform view (Audacity-like peak display)
- Trim by start/end range and save to WAV
- One-click silence removal (leading/trailing)
- Convert loaded hitsound (`.ogg`/`.mp3`/`.wav`) to `.wav`

## Supported osu!standard hitsound names

- normal-hitnormal
- normal-hitwhistle
- normal-hitfinish
- normal-hitclap
- normal-slidertick
- normal-sliderslide
- normal-sliderwhistle
- drum-hitnormal
- drum-hitwhistle
- drum-hitfinish
- drum-hitclap
- drum-slidertick
- drum-sliderslide
- drum-sliderwhistle
- soft-hitnormal
- soft-hitwhistle
- soft-hitfinish
- soft-hitclap
- soft-slidertick
- soft-sliderslide
- soft-sliderwhistle

## Run

```powershell
dotnet run
```

From this directory:

```powershell
cd OsuHitsoundTuner
```

## Notes

- Waveform is downsampled for display only; edits/export use full sample data.
- Silence removal uses a fixed threshold (`0.01`) and trims only leading/trailing silence.
