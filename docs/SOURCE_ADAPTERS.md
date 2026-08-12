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

The default `--sync-lead 2.8` compensates for Plex session reporting, decoder
startup and the measured TV-to-HyperHDR path latency on the QE77S95F. If the
preview leads or trails on another setup, adjust this value when starting the
supervisor.

The adapter follows pause, resume, seek and media changes. It only handles
sources that the owner can read from the local Plex server. It does not bypass
DRM or HDCP. Other TV applications require their own legitimate source adapter
or a working TV capture backend.
