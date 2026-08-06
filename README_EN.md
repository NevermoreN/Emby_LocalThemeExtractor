# Local Theme Extractor - Emby Plugin

An Emby plugin that extracts theme songs from local media files using ffmpeg — no external download services required.

[中文说明](README.md)

## Features

- **TV Intro Extraction**: Uses Emby's existing intro markers to extract opening theme music as `theme.mp3`; samples S01E02 by default (episode 1 often has a cold open / no OP) and skips the whole series if that episode has no qualified marker
- **TV Credits Fallback**: For shows without intro markers, automatically detects ending credits and extracts ending music
- **Movie Credits Extraction**: Uses audio silence detection and video black-frame detection to locate credits and extract ending music
- **Fixed Window Fallback**: For movies with no detectable black frames or silence, optionally extracts the last N seconds
- **3-Tier Failure Strategy**: 1-2 failures → normal retry → 3-5 failures → extract last 30s → 6+ failures → permanently skip

## Highlights

- **Zero External Dependencies**: Uses only Emby's built-in ffmpeg — no network downloads, no SkiaSharp, no image libraries
- **In-Memory Processing**: ffmpeg pipes output via `pipe:1` to memory — no temp files written to disk
- **Bandwidth Optimized**: Uses `-map 0:a:0` to read only audio stream data, dramatically reducing I/O for cloud/FUSE mounted media
- **3-Tier Detection**: silencedetect (~2MB) → blackdetect (~200MB) → fixed window (0 extra I/O)
- **GPU Acceleration**: blackdetect supports nvdec / vaapi / videotoolbox / dxva2
- **Read-Only FS Aware**: Automatically detects and skips read-only mount points, avoiding wasted detection bandwidth
- **Persistent Failure Tracking**: Failure counts saved to `/config/data/lte-failures.json`, preserved across task runs
- **STRM File Support**: Automatically resolves strm files to actual media paths
- **Multi-Library Selection**: GenericEdit UI with multi-select library picker
- **Post-Run Library Scan**: Automatically triggers a library scan when new theme.mp3 files were written, so Emby registers them immediately (no scan on empty runs)
- **48 kHz Output**: MP3 keeps the native 48 kHz sample rate of film/TV audio instead of resampling to 44.1 kHz
- **Undecodable-Track Avoidance**: Chinese 4K WEB-DLs often put an Audio Vivid (av3a) track first, which ffmpeg cannot decode; the plugin probes with ffprobe and automatically picks the first decodable track (DTS/EAC3/AAC/...) instead

## Installation

1. Download `LocalThemeExtractor.dll` from [Releases](../../releases)
2. Copy the DLL to Emby's plugin directory:
   - Docker: `/config/plugins/`
   - Windows: `%AppData%\Emby-Server\plugins\`
   - Linux: `/var/lib/emby/plugins/`
3. Restart Emby Server
4. Configure in Emby Dashboard → Plugins → Local Theme Extractor
5. Run manually from Emby Dashboard → Scheduled Tasks → "提取本地主题曲"

## Configuration

| Option | Description | Default |
|--------|-------------|---------|
| Library Scope | Select libraries to process (empty = all) | All |
| Max Parallelism | Concurrent processing threads | 1 |
| Throttle Seconds | Delay between items in sequential mode | 2 |
| Overwrite Existing | Re-extract even if theme.mp3 exists | No |
| **TV Shows** | | |
| Extract Intro | Use Emby intro markers | Yes |
| Prefer Season | Season to sample from (0 = earliest with markers; required 0 for shows without season numbers) | 1 |
| Prefer Episode | Episode of the target season to sample; series is skipped entirely if it has no qualified marker | 2 |
| Min Intro Seconds | Skip intros shorter than this (likely ads) | 20 |
| Fallback to Credits | Detect ending credits when no intro markers exist | No |
| **Movies** | | |
| Extract Credits | Detect movie credits via blackdetect/silencedetect | Yes |
| Lookback Seconds | How far from the end to scan | 600 |
| Min Credits Seconds | Skip credits shorter than this | 30 |
| Fallback Window | Extract last N seconds when detection fails | No |
| Fallback Window Seconds | Length of fallback extraction | 90 |
| **Hardware / Output** | | |
| Hardware Decode | Enable GPU acceleration for blackdetect | No |
| Decoder Name | nvdec / vaapi / videotoolbox / dxva2 | nvdec |
| Audio Bitrate | Output MP3 bitrate | 192 kbps |

## Detection Strategy

```
TV Shows (with intro markers):
  Sample only "Prefer Season × Prefer Episode" (default S01E02) → theme.mp3
  Episode missing or marker unqualified → skip whole series (also excluded from credits fallback)

TV Shows (no intro markers, fallback enabled):
  silencedetect → blackdetect → skip

Movies:
  silencedetect (audio only, ~2MB bandwidth)
    ↓ found → extract
    ↓ not found
  blackdetect (video decode, ~200MB bandwidth)
    ↓ found → extract
    ↓ not found
  fixed window (optional, 0 extra bandwidth)
    ↓ enabled → extract last N seconds
    ↓ disabled → record failure
```

## Failure Retry Mechanism

Failure records are persisted in `/config/data/lte-failures.json`.

| Failure Count | Behavior |
|--------------|----------|
| 1-2 times | Normal retry on next run (full detection) |
| 3-5 times | Skip detection, extract last 30 seconds directly |
| 6+ times | Permanently skip (source file likely broken) |
| Success | Clear failure record |

Delete `lte-failures.json` to reset all failure records.

## Building

```bash
dotnet build src/LocalThemeExtractor.csproj -c Release
```

Output: `src/bin/Release/netstandard2.0/LocalThemeExtractor.dll`

> Note: Requires Emby SDK NuGet packages (MediaBrowser.Common 4.8.0.24-beta, MediaBrowser.Server.Core 4.8.0.24-beta).

## Compatibility

- Emby Server 4.7+ (netstandard2.0)
- Linux / Windows / macOS
- Docker containers (linuxserver/emby, etc.)
- Works with or without GPU (UseHwDecode defaults to off)
- No dependency on SkiaSharp or any image processing library

## License

[MIT](LICENSE)
