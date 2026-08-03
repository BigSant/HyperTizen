# QE77S95F 1296.8 indirect Hyperion image paths

## Scope and coverage

This covers firmware components that are not public screen-capture APIs but
can still produce a full frame, per-zone colors, or fallback metadata for
Hyperion. Direct capture and encoder paths are documented in
`FIRMWARE_RESEARCH_QE77S95F_1296.md`.

The inventory is bounded to the four decoded 1296.8 trees for QE77S95FATXXH:

| Tree | Regular files | ELF files | Text metadata | Matches |
| --- | ---: | ---: | ---: | ---: |
| RSM platform | 59,565 | 4,578 | 5,129 | 1,550 |
| RSM product | 976 | 167 | 58 | 79 |
| RSW platform | 33,004 | 4,001 | 2,988 | 1,153 |
| RSW product | 621 | 130 | 46 | 60 |

`tools/firmware/scan_media_candidates.py` examined every regular file and
extracted strings from every ELF. The broad pass found 946 capture, 200
display-frame, 46 encoded-frame, 463 preview/thumbnail, 710 color-analysis,
721 video-pipeline, 82 graphics, 559 hardware-I/O, 131 external-input, 826
stream-sharing and 16 ambient-light matches. Groups overlap.

"Complete" means that every detectable ABI, service, plug-in, device path and
configuration match in these decoded trees was classified. It cannot prove
that an absent implementation, a downloaded optional package, or secure-world
code missing from the update image does not exist.

## Candidate registry

| Class | Mechanism | Hyperion result | Spatial data | Rate evidence | Decision |
| --- | --- | --- | --- | --- | --- |
| A | `rmdemon` screen-sharing | JPEG quality 90 | full frame | interval 0-10,000 ms | conditional fallback |
| B | `color-pick` D-Bus | packed RGB rectangles | configurable zones | fixed 2.0 s timer | static fallback only |
| B | video-enhancer histogram/APL | lux/backlight/histogram value | none | cheap poll/callback | black/brightness only |
| A/D | `tvmultihdmisrc` DMA-BUF | HDMI input frames | full HDMI frame | continuous source | HDMI-only fallback |
| A/D | TV live/tuner pipeline | broadcast frame/stream | full source frame | continuous source | tuner-only fallback |
| A/D | video-sink software decode | decoder-owned raw frame | full app frame | render cadence | cooperating app only |
| D | video-sink clone/swap | secondary scaler ownership | potential plane | render cadence | capability probe only |
| A/D | `libsign-finder-api` | caller-owned BGRA image | one full video frame | snapshot | same trusted-capture gate |
| D | `issue_report_agent` | JPEG/YUV files in `/tmp` | full diagnostic frame | configured timer/limit | reject for production |
| D | `libicewater` | frame used for watermarking | full post-YUV frame | callback/snapshot | same raw-capture limit |
| E | dominant-color/screen-analysis | derived from supplied image | input-dependent | processing only | post-processor |
| F | frame broker | widget transition surface | provider only | event based | reject as panel source |
| F | remote-wall API | UI/control state | none | event based | reject as image source |
| F | inbound Miracast | remote device stream | remote source only | continuous | reject for panel output |
| F | QPI CRC/diagnosis | CRC/status integers | none | fast | telemetry only |

Classes: A full frame, B derived colors/statistics, D source-specific, E
post-processor, F rejected producer. A candidate is not called working until
it passes the physical-TV acceptance test.

The broad scanner's remaining high-score matches were also dispositioned:

- Chromium, Cobalt, WebRTC, generic players/codecs, GStreamer RTP/RTSP and
  DLNA operate on media already owned by their process; they do not expose the
  panel or another application's hardware plane;
- Mali/EGL/Evas/DALI, photo filters and transition libraries can read only
  caller-owned render targets or compositor UI surfaces;
- camera, auto-zoom, object-detection and VR360 libraries acquire camera or
  supplied images, not display output;
- thumbnail, preview, poster and trick-play APIs decode stored media assets;
- Miracast/WFD and screen-mirroring sources are inbound remote streams unless
  paired with one of the separately listed display encoders;
- ambient/backlight/LED matches configure output lighting or return global
  state and contain no spatial frame source;
- factory, OQC, anomaly and debug-menu binaries are diagnostics, not stable
  callable services, and ultimately use the same capture backends.

## Trusted full-frame consumers already in firmware

Several owner/system processes acquire complete YUV frames through
`libvideo-capture.so.0`:

- `contents-recognition-service` calls `getVideoMainYUV` and performs frame
  histograms, OCR and black-screen/content analysis;
- `contents-ui-detector-service` calls `getVideoMainYUV`, converts YC to RGB
  and analyzes thumbnail regions;
- `sa-ui-detector` calls `getVideoMainYUV` and processes the complete image;
- `subtitle-recog-service` calls `getVideoYUV` for text recognition;
- `monitor-classification-service` calls `getVideoMainYUV` for scene analysis;
- `ai-cc-position-service`, `contents-ott-detector-service`,
  `game-text-recog-service`, and `screen-analysis-discovery-service` also
  capture full YUV/RGB inputs for their models;
- `mirroring-recognition-manager` and the minimap detector consume captured
  frames to identify layouts or regions.

This proves that trusted user-space consumers can read the raw video plane.
Their IPC exports detection/classification results, not captured buffers, so
none is currently a no-permission frame relay. Injecting into system services
would be less safe than calling the same lower backend and is not planned.

`libsign-finder-api` is a caller-owned wrapper that captures the main video
plane and converts YUV420/422 to BGRA in a `CAPTURED_IMG`. It may be convenient
if the library is readable by the application, but it imports the same trusted
capture/TZ stack and adds no new permission bypass or continuous stream.

## `color-pick`: zone colors without exporting an image

`/usr/bin/color-pick` runs as `owner:users` and owns system D-Bus service
`com.uifw.colorpick`, object `/com/uifw/colorpick`. It exposes `Subscribe`,
`Unsubscribe` and `GetAverageColor`.

A subscription has two identifiers and rectangle x/y/width/height. The timer
acquires a complete 480x272 post-YUV image, averages every pixel in each
rectangle, and caches packed `0xRRGGBB`. Saturated colors are hue-quantized to
a small palette; near-gray colors are quantized by brightness. This is area
averaging, not the legacy individual-pixel sampler.

The timer is hard-coded to 2.0 seconds. About 0.5 FPS and palette quantization
make it unsuitable for moving ambilight, but it is a light idle fallback and
permission diagnostic. A TV test can subscribe one rectangle per LED zone and
verify whether the normal HyperTizen label is allowed by D-Bus policy.

## `rmdemon`: authenticated JPEG screen sharing

`/usr/bin/rmdemon` is the only `libtzcapturec` allowlisted executable actually
present in immutable firmware. It runs as preloaded
`org.tizen.remote-management` with its own SMACK label and privileges.

It has two capture modes:

- `captureGraphics` invokes `enlightenment_info -dump_screen`, an OSD capture
  with the same overlay limitation as EFL;
- remote-control media calls `getScreenPostYUV`, checks protection, converts
  YUV420/422/444, resizes, JPEG-encodes at quality 90, adds a CRC and posts
  successive `ScreenFrame` records.

The stream accepts 0-10,000 ms intervals. Values in range are multiplied by
1,000 for `usleep`; larger values are clamped to ten seconds. Zero adds no
sleep, so there is no artificial low-FPS cap. Actual FPS depends on capture,
resize, JPEG and transport time.

The daemon starts only for Remote Management and maintains an authenticated
support session. HyperTizen must not forge or bypass it. The safe test is for
the owner to enable Remote Management, inspect state read-only and determine
whether its authorized stream is exposed through a supported endpoint.
Otherwise this remains a reference implementation for the lower backend.

## HDMI, tuner and decoder-owned alternatives

`libgsttvmultihdmisrc.so` requests HDMI DMA buffers through
`ppi_hdmi_input_control_*` and emits `dmabuf-fd` with resolution, color range,
HDR/HDCP and source metadata. For an active HDMI input HyperTizen could
downscale the input buffer and calculate zones before panel composition. It
does not cover Plex/internal OTT apps and must respect protected buffers.

The `tv-viewer`/TV-live pipeline owns broadcast frames. An appsink or encoder
branch can cover tuner channels only when HyperTizen legitimately owns or is
given that branch. It cannot see unrelated application UI or OTT planes.

`libvideo-mm-control.so` exports `ppi_video_renderer_get_sw_dec_data`, and
`libvideo-sink.so` exposes `getSWDecData`. These can return full frames from a
software buffer owned by the same sink, not another process's hardware plane.
This is viable only for a cooperating player/module.

The sink's clone/swap APIs manage scaler ownership instead of exporting pixel
bytes and may disrupt playback. They receive one non-mutating capability
probe, not production use, unless a documented clone target yields a readable
buffer without stealing the active sink.

Samsung's public TV player explicitly does not support `CaptureVideoAsync` or
the `VideoFrameDecoded` event, so no public player API replaces these paths:
<https://developer.samsung.com/smarttv/develop/tizen-net-tv/guides/multimedia.html>.

## Low-information fallbacks and rejected producers

`libvideoenhance` plus product `libave` exposes a global histogram value,
frame-lux and frame-backlight callbacks. They can detect black frames, fades
and global brightness but cannot determine colors at separate screen edges.
OLED frame-backlight data must not be assumed to contain local zones.

`libdominant_colors` and `libscreen-analysis-api` reduce a caller-supplied
image to colors/features but do not acquire it. Frame broker surfaces belong
to widget transitions. Remote-wall exports control state. Miracast components
receive another device's stream. None is a panel image producer.

`issue_report_agent` can periodically dump full JPEG/YUV diagnostics to
`/tmp/IRA_screen_capture/` according to policy fields `mode`, `timer`, and
`limit`. It writes fault-report files instead of streaming buffers, and the
trigger is policy-controlled. File polling adds latency and still uses the
snapshot backend, so this remains a single-frame diagnostic cross-check.
Factory/test binaries expose similar dump helpers but are not production
services.

`libicewater` calls `getScreenPostYUV` and receives a full frame for an
on-screen watermark path. It is another proof of post-plane access, not an
independent producer: it inherits the same permissions, protection checks and
snapshot-rate limit.

## Kernel and rate findings

Samsung `sdp_drm.ko` implements capture GEM allocation, hardware capture,
synchronous locks, protection status, last-captured data and displayed-buffer
queries. Product `libvideo-capture-impl-sec` maps them through
`/dev/dri/card1`.

The backend explicitly reports `post capture usage exceeded, 15 pages in a
second`. Repeated post-YUV snapshots cannot satisfy 24 FPS. Continuous encoder
input and `/dev/video30` streaming are separate and do not use this snapshot
check. CRC and diagnosis-fast-capture APIs return only status through their
public ABI, so they cannot generate colors.

## Permission conclusions

Public Samsung APIs select/show TV or HDMI sources but do not return frame
bytes. `TVWindow` is a public `tv.window`-privileged source-window controller:
<https://developer.samsung.com/smarttv/develop/api-references/tizen-web-device-api-references/tvwindow-api.html>.

No-new-permission experiments are existing D-Bus mediators, legitimate
GStreamer/source ownership and lower ABIs on already accessible device nodes.
Declaring a partner/platform privilege cannot elevate a developer certificate.
Runtime SMACK, resource ownership and content protection are measured for each
candidate.

No plan weakens policy, modifies immutable firmware, forges a trusted process,
bypasses Remote Management authentication, or defeats HDCP/DRM. Protected
denial is a valid result.

## Ordered physical-TV tests

1. Introspect `com.uifw.colorpick`, subscribe 16 edge rectangles and confirm
   the fixed update rate and packed RGB changes.
2. With Remote Management enabled by the owner, inspect daemon/session state;
   measure a 0/42 ms stream only if the normal workflow exposes it.
3. Build an HDMI `tvmultihdmisrc` to low-resolution appsink pipeline and
   validate DMA-BUF cadence/colors without circumventing HDCP.
4. Test a legitimate TV-live branch for tuner content without disrupting
   `tv-viewer` resource ownership.
5. Test `getSWDecData` inside a cooperating software-decoded player.
6. Calibrate histogram/frame-lux only for black/fade detection.
7. Perform a read-only clone/swap capability check; stop if it changes source
   or requests ownership.

Frame producers must deliver at least 240 changing, decodable, coherent frames
in ten seconds. Zone/statistics methods are degraded fallbacks and do not meet
the full-frame objective.

## Resulting fallback order

```text
full display H.264 encoder
  -> lower RM / continuous V4L2 encoder
  -> authorized Remote Management JPEG stream
  -> HDMI DMA-BUF source (HDMI only)
  -> tuner/live source (broadcast only)
  -> cooperating software-decoder frame
  -> raw scaler snapshot (maximum about 15 FPS)
  -> direct trusted capture, if admitted
  -> Wayland/EFL UI mirror
  -> color-pick zone RGB (about 0.5 FPS)
  -> histogram/frame-lux global brightness
  -> legacy individual-pixel sampler
```

Selection must validate changing frames and retry higher-quality methods after
a cooldown when resource ownership changes.

## Static-analysis loop closure

The firmware-analysis loop is closed only against the stated decoded-image
boundary, with these completion checks satisfied:

- [x] every regular file in all four RSM/RSW platform/product trees scanned;
- [x] every ELF and relevant service/package/policy metadata inspected;
- [x] direct full-frame, continuous encoder and kernel capture paths traced;
- [x] all binaries importing `libvideo-capture` dispositioned;
- [x] remote-sharing, HDMI, tuner, software-decoder and clone paths traced;
- [x] zone-color, histogram/APL and post-processing paths classified;
- [x] generic player, browser, camera, graphics, thumbnail and diagnostic
  false positives recorded by category;
- [x] ABI, device node, IPC, runtime-label and protected-content gates noted;
- [x] expected rate, image quality, spatial coverage and fallback role stated;
- [x] physical-TV acceptance tests ordered without claiming static findings as
  runtime success.

Physical validation is a separate loop. It can promote a conditional candidate
to production only from measurements on the target TV and must not reopen the
firmware inventory unless runtime evidence points to a previously absent or
downloaded component.
