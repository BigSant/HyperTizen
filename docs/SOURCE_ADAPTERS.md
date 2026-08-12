# Full-frame source adapters

HyperTizen uses two independent layers:

1. the TV service exposes control, status and logs over the LAN;
2. source adapters obtain coherent video frames and submit them to HyperHDR.

The first working full-frame adapter is `PlexSessionAdapter` in
`tools/source_bridge.py`. It discovers the active Plex session for the selected
TV, reads its current playback position and media part URL, decodes a 320x180
24 FPS copy with FFmpeg, and sends NV12 frames to HyperHDR's FlatBuffers API.

This is deliberately not implemented as Plex-specific HyperHDR transport.
Future adapters implement `MediaSourceAdapter.current_session()` and return a
`SourceSession`; synchronization, decoding and HyperHDR output remain shared.

Ubuntu dependencies are `ffmpeg` and Python package `flatbuffers==25.2.10`.

Run a short validation from Ubuntu/WSL:

```bash
python3 tools/source_bridge.py --run-seconds 10
```

Run continuously:

```bash
python3 tools/source_bridge.py
```

To control the bridge from `controls.html`, start the lightweight supervisor
once on the same Windows/WSL computer:

```bash
python3 tools/source_bridge_control.py
```

Keep the control panel's Source Bridge value at
`http://127.0.0.1:19445`. The Start, Stop, Pause and Resume buttons then manage
the Plex bridge process through the supervisor. If the control panel is opened
on another device, run the supervisor with `--listen 0.0.0.0` and enter the
Linux host's LAN address instead of `127.0.0.1`.

Plex reports `viewOffset` in roughly 10-second steps. The adapter detects those
updates at 5 Hz and interpolates the playback position with a monotonic clock,
then resynchronizes if the corrected timeline differs by more than 750 ms.
The default `--sync-lead 1.0` compensates for measured FFmpeg startup and the
short FlatBuffers/preview path. For frame-accurate calibration, play a video
with a per-frame timecode and record the TV and HyperHDR preview together with
a 120 FPS camera; the timecode difference is the remaining display constant.

The adapter follows pause, resume, seek and media changes. It only handles
sources that the owner can read from the local Plex server. It does not bypass
DRM or HDCP. Other TV applications require their own legitimate source adapter
or a working TV capture backend.

On NVIDIA-equipped Linux/WSL hosts, `--hardware-decoder auto` uses CUVID to
decode HEVC/H.264 and resize the source directly on the GPU. The QE77S95F HDR
test reduced FFmpeg from about 118% of one CPU core to roughly 8% CPU time.
Use `--hardware-decoder off` only for troubleshooting; a failed GPU decoder
automatically falls back to software.
