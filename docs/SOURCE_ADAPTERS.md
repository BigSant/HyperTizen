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
then reduces polling to 1 Hz after the first calibration. A dedicated reader
continuously drains FFmpeg and retains only the newest complete frame. If
HyperHDR or WLED briefly stalls, intermediate frames are dropped instead of
building an ever-growing pipe backlog. A timeline difference over 0.75 seconds
must be observed twice before FFmpeg is restarted; a seek over 5 seconds is
handled immediately. A missing Plex session is tolerated for three seconds to
avoid restarting on a transient status response.
The default `--sync-lead 1.0` compensates for measured FFmpeg startup and the
short FlatBuffers/preview path. For frame-accurate calibration, play a video
with a per-frame timecode and record the TV and HyperHDR preview together with
a 120 FPS camera; the timecode difference is the remaining display constant.

The adapter follows pause, resume, seek and media changes. It only handles
sources that the owner can read from the local Plex server. It does not bypass
DRM or HDCP. Other TV applications require their own legitimate source adapter
or a working TV capture backend.

On NVIDIA-equipped Linux/WSL hosts, `--hardware-decoder auto` uses CUVID. On
Linux servers with `/dev/dri/renderD128`, it uses VAAPI; this is the TrueNAS
deployment path for the Intel HD 630. Both modes decode and resize on the GPU.
The QE77S95F HDR test with CUVID reduced FFmpeg from about 118% of one CPU core
to roughly 8% CPU time. Use `--hardware-decoder off` only for troubleshooting;
a failed GPU decoder automatically falls back to software.

When Plex and the adapter run on the same TrueNAS host, map the Plex metadata
prefix to the read-only dataset instead of downloading the movie through Plex:

```bash
python3 tools/source_bridge.py \
  --plex-path-prefix /data --local-media-root /media \
  --hardware-decoder vaapi
```

The complete two-container TrueNAS deployment is in
`deploy/truenas-hyperhdr/`.

The bridge emits a `Bridge timing` line every 30 seconds with current timeline
drift and the number of stale frames discarded. `Session pass` reports the same
discard counter when a decoder pass ends. These values distinguish healthy
latency control from decoder or network throughput problems.
