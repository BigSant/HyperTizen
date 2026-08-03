# QE77S95F 1296.8 full-frame capture research

## Objective and status

The target is a complete, spatially coherent TV frame at approximately 24 FPS
for Hyperion.  Sampling a small set of pixels is explicitly not an acceptable
capture method.  Firmware decoding is documented separately in
`FIRMWARE_DECODE_QE77S95F_1296.md`.
Indirect frame sources, zone-color services, HDMI/tuner paths and global
statistics are inventoried in `INDIRECT_HYPERION_PATHS_QE77S95F_1296.md`.

Two facts are now proven on the physical `QE77S95FATXXH`:

- a normal developer-signed app can capture the complete Wayland UI surface at
  25-30 FPS through EFL screen mirror, despite not declaring the documented
  platform screenshot privilege;
- full-screen Plex video is on a separate hardware/DRM plane and is black in
  both EFL screenshots and screen-mirror frames.

The decoded firmware exposes four independent lower-level routes that may
capture the video plane.  They have been statically reconstructed but have not
yet been executed on the TV.  No route is claimed working until it produces
changing, non-black, full-frame data under a timed test.

Both RSM and RSW platform images were checked.  The display encoder, CAPI
encoder, Wayland source, and RM implementation are byte-identical across them,
which makes those findings independent of one container variant.  The RSM
product partition additionally supplies the unstripped raw scaler backend.

## Proven compositor behavior

`libcapi-ui-efl-util.so.0` exports screenshot and screen-mirror APIs.  Tests
from the separately packaged Probe app showed:

- `efl_util_screenshot_initialize(320, 180)` succeeds;
- screenshots return a mappable `XR24`/XRGB8888 TBM surface, stride 1280;
- screen mirror delivers approximately 25-30 callbacks per second;
- TV Home/UI frames decode correctly as complete images;
- with full-screen Plex video, screenshot and mirror surfaces are entirely
  black or unchanged while overlay video continues on the panel.

The public Tizen API labels screenshot as platform-only and requiring
`http://tizen.org/privilege/screenshot`.  The observed 1296.8 TV policy is
therefore less restrictive for this particular compositor path than the API
contract promises.  This does not grant access to protected overlay planes.

Reference: <https://docs.tizen.org/application/native/api/common/9.0/group__CAPI__EFL__UTIL__SCREENSHOT__MODULE.html>

## Firmware capture-path inventory

| Priority | Route | Output | Expected rate | User-space gate | Remaining risk |
| ---: | --- | --- | --- | --- | --- |
| 1 | GStreamer `displayencodesrc` | H.264 byte stream | configurable, target 24 FPS | no Cynara import found | resource manager, DRM source selection, protected-content denial |
| 2 | `libcapi-encoder-tv` | H.264 packets | configurable | no explicit feature gate found | wrapper may require Samsung resource allocation |
| 3 | lower RM backend | H.264 from `/dev/video30` | configurable | bypasses disabled feature flag | Samsung DRM/V4L2 sequence and runtime SMACK |
| 4 | direct scaler backend | raw Y/C planes | snapshots or continuous encoder input | bypasses high-level/TZ allowlist | `/dev/dri/card1`, ioctl policy, possible 15 FPS post-capture cap |
| 5 | direct TZ capture protocol | raw Y/C planes | unknown | bypasses `libtzcapturec` process-name check | TA may deny DRM/protected content |
| 6 | GStreamer `waylandsrc` | BGRA/NV12/SN12 | continuous | compositor privilege notification | probably same overlay exclusion as EFL |

### 1. Hardware display encoder

`/usr/lib/gstreamer-1.0/libgstdisplayencodesrc.so` is a complete Samsung
display-to-H.264 source.  Static inspection found:

- output caps `video/x-h264, stream-format=byte-stream`;
- `/dev/video30` and `libencoder-control.so.0` integration;
- DRM plane setup through `tvvideoenc_drm_*` helpers;
- width, height, bitrate, frame-rate, device, trustzone, DRM type, and forced
  keyframe properties;
- DRM modes for HDCP, private TrustZone, and clear/unencrypted capture;
- an explicit access-denied branch for protected DRM content.

This is the strongest first candidate because it already combines display
selection, queueing, hardware encoding, and timestamps.  The first test must
use the clear mode and unprotected content.  It should not attempt to defeat
HDCP or decrypt protected media.

`/usr/lib/libcapi-encoder-tv.so.0.8.70` is a higher-level wrapper that builds a
pipeline around the same source and exports `encoder_create`, configuration,
start, callback, and `encoder_get_packet` operations.  It is simpler to embed
if resource allocation succeeds; raw GStreamer remains the fallback when the
wrapper rejects a third-party process.

### 2. RM capture below the model feature gate

`libcapi-rm-video-capture.so` returns unsupported because feature
`com.samsung/featureconf/rm.h264_support` is disabled on this model.  That is a
user-space product flag, not proof that the hardware is missing:

- `/dev/video30` is configured as `sec_vid_enc0/1`;
- `/dev/dri/card0` is the corresponding Samsung scaler/display DRM node;
- the developer app already opened both nodes read/write;
- `librm-video-capture-impl-sec.so.0.0.1` contains the complete lower backend.

The reconstructed sequence opens DRM, obtains framebuffer dimensions, creates
the capture framebuffer, selects source/property/plane and sync/mute behavior,
then configures and streams the V4L2 encoder.  Calling this lower ABI bypasses
only the high-level feature test; it does not bypass kernel or SMACK policy.

### 3. Raw Samsung scaler capture

`/prd/usr/lib/libvideo-capture-impl-sec.so.0.1.0` is an unstripped lower
backend exporting screen/video/post-YUV/main/sub/background/cropped capture,
lock/protect, and `scaler_capture*` functions.  It operates on
`/dev/dri/card1` with Samsung ioctls including capture GEM creation, hardware
capture, and last-captured-data retrieval.

The backend yields complete Y and C planes, supports crop/rotation/flip, and
does not import Cynara.  A diagnostic string limits post-capture use to 15
pages per second, so the generic post-snapshot call may not meet 24 FPS.
`getVideoYUVToEncoder` and continuous scaler/encoder paths must be tested
separately before rejecting the backend.

### 4. TZ capture without the proprietary client library

High-level `libvideo-capture.so`, `libdisplay-capture-api.so`, and
`libep-common-screencapture.so` route selected processes through
`libtzcapturec.so`.  A developer label cannot read or load that installed
library.  Static analysis nevertheless showed that its user-space admission
check is only an exact `/proc/self/cmdline` comparison against a hard-coded
Samsung-process allowlist.

Shipping Samsung's proprietary binary or impersonating a system pathname is
unnecessary.  The client protocol can be implemented directly with the public
TEEC ABI:

```text
TA UUID: 58d50001-0006-0006-a06a-39b256ad7de7
shared memory: three 0x80000-byte INPUT|OUTPUT blocks
secure capture command: 0
operation: PARTIAL_INOUT, PARTIAL_INOUT, PARTIAL_INOUT, NONE
```

The first two blocks return Y and C planes.  The third contains requested and
captured width/height, chroma/full-size selection, rotation/metadata, and the
normal capture command ID.  After success, Y is `width * height`; C is either
the same size or half-size depending on the returned selector.

This avoids the client library's process-name allowlist, but not TA policy.
The trusted application may still reject protected frames or unapproved
runtime labels.  That boundary is measured rather than assumed.

### 5. Wayland source and non-candidates

`libgstwaylandsrc.so` understands the Tizen screenshooter/screenmirror
protocol, raw BGRA/NV12/SN12 formats, and separate NORMAL/VIDEO events.  Its
VIDEO event is worth one controlled test, but it likely exposes the same
compositor surface already proven black for overlay video.

The following are not primary solutions:

- `libscreen-analysis-api` sends an already acquired image to an analysis
  service; it is not a capture producer;
- `libgsttvextvideosrc` appears to source external HDMI hardware around
  `/dev/video28`, not the composited panel output;
- repeated EFL screenshots cannot recover a plane the compositor never sees;
- pixel sampling remains useful only as a last-resort compatibility fallback,
  not as completion of this objective.

## Permission strategy

The approach is to use the narrowest existing ABI and bypass only redundant
user-space checks, never to modify firmware policy or weaken the TV globally.

The production native app currently declares only `notification`, `internet`,
`network.get`, `externalstorage`, `mediastorage`, `display`, and
`window.priority.set`.  The successful Probe declared only `internet` and
`network.get`; notably it did not declare screenshot, media-capture, partner,
or platform privileges.  Candidate tests therefore begin with this ordinary
developer-signed security context and treat any additional privilege as a
measured requirement, not an assumption.

| Gate | Evidence | Safe bypass/alternative |
| --- | --- | --- |
| screenshot platform privilege | EFL calls already succeed without declaration | use working ABI; retain black-frame detection |
| RM `h264_support` feature | false while devices exist and open R/W | call lower RM/backend ABI |
| `libtzcapturec` pathname allowlist | implemented entirely in client library | implement equivalent TEEC request in our code |
| unreadable installed library | SMACK denies `dlopen` | link no proprietary file; reimplement documented calls |
| kernel node policy | card0/video30 open succeeded; card1 unknown | probe capabilities first and fail closed |
| DRM/HDCP protection | encoder and TA contain denial paths | do not bypass content protection; mark method unavailable |

Samsung documents that partner/platform privileges normally require matching
vendor certificates.  Declaring such a privilege in a developer-signed TPK
does not grant it, so manifest-only changes are not treated as a solution:
<https://developer.samsung.com/smarttv/develop/extension-libraries/nacl/managing-nacl-projects/adding-privileges-and-permissions.html>.

## Ordered physical-TV test plan

All tests use a separately named Probe package, strict timeouts, explicit
cleanup, and no firmware writes.  Each method is tested first on TV Home, then
on a changing unprotected local video, and finally only observed (not
circumvented) on a protected streaming title.

### P0: baseline and acceptance criteria

1. Record device-node open results, labels, supported V4L2 formats, DRM driver
   version, and current resource ownership.
2. Capture the EFL UI baseline at 320x180 and 1280x720.
3. Require at least 240 frames over ten seconds for the 24 FPS target.
4. Reject constant, all-black, malformed, duplicated, or stale frames.
5. Record CPU, memory, dropped frames, end-to-end latency, and cleanup state.

### P1: encoded full-display routes

1. Build a minimal `displayencodesrc` pipeline in clear mode at 1280x720,
   24 FPS, conservative bitrate, and pull H.264 access units for ten seconds.
2. Decode several units locally and verify changing full images rather than
   accepting packets as proof.
3. Repeat through `libcapi-encoder-tv`; keep it only if it reduces integration
   complexity without new policy failures.
4. If the public wrappers reject the model flag, call the lower RM backend in
   its reconstructed DRM-to-V4L2 order.

### P2: raw scaler and trusted capture

1. Probe `/dev/dri/card1` and backend initialization without issuing capture.
2. Capture one small raw frame, validate dimensions/strides, then test
   continuous `YUVToEncoder` and measure whether the 15 FPS limiter applies.
3. Implement the TZ protocol in the Probe helper; request 320x180 command 0,
   validate returned metadata and plane sizes, then move to 1280x720.
4. Distinguish `TEEC_ERROR_ACCESS_DENIED`, unsupported command, black output,
   and protected-frame denial in logs.

### P3: compositor variant and last-resort ioctl work

1. Test `waylandsrc` VIDEO/NV12 mode once for completeness.
2. Only if wrappers fail while device access succeeds, reproduce the minimum
   Samsung DRM/V4L2 ioctls directly, preserving exact cleanup order.
3. Stop after three repeatable failures of the same gate and document the
   boundary instead of destabilizing the running TV.

## Intended Hyperion fallback chain

Only verified methods enter the production chain.  The expected order is:

```text
DisplayEncode H.264
  -> CAPI encoder / lower RM encoder
  -> authorized Remote Management JPEG stream
  -> HDMI DMA-BUF source (HDMI content only)
  -> tuner/live source (broadcast only)
  -> cooperating software-decoder frame
  -> raw scaler capture
  -> direct TZ capture
  -> Wayland/EFL screen mirror (UI)
  -> EFL screenshot (UI)
  -> color-pick zone RGB (about 0.5 FPS)
  -> histogram/frame-lux (global brightness only)
  -> existing pixel sampler (legacy last resort)
```

Each transition is triggered by initialization failure, policy denial,
insufficient measured FPS, repeated stale/black frames, or malformed output.
The chain must retry higher-quality methods after a bounded cooldown because
resource ownership can change when applications start or stop playback.

## Security and legal boundary

This research analyzes the owner's TV and firmware for local interoperability.
It does not extract keys, patch secure boot, flash modified firmware, defeat
HDCP, or promise access to DRM-protected frames.  The achievable software-only
result may be limited to complete unprotected video and UI; protected content
denial is an expected valid outcome.
