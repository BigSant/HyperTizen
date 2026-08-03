# QE77S95F firmware 1296.8 research

## Acquired package

- Model: `QE77S95FATXXH`
- Tizen generation: Tizen 9.0 / 2025 TV
- Samsung package: `T-RSMFDEUC-0090_TB-RSWF4DEUC-0090.zip`
- Firmware version: `1296.8`
- Build date declared by Samsung: `2026-04-16`
- ZIP size: `2,479,379,839` bytes

The firmware archive is intentionally not stored in Git. The working copy is
under `Samsung/firmware/QE77S95FATXXH_1296.8`.

## Container findings

The ZIP contains two `MSDU11` containers:

- `T-RSMFDEUC-0090/image/upgrade.msd` (`1,516,607,106` bytes)
- `TB-RSWF4DEUC-0090/image/upgrade.msd` (`962,379,412` bytes)

Both containers expose a valid clear-text section table with nine sections.
Their OUIT metadata and section contents are encrypted. `info.txt` identifies
the cipher suite as `aes-256-cbc-sha512` and the production key family as
`RoseM 2025 rel key`.

The current public extractors understand the MSDU11 layout, but their public
key sets contain Samsung TV generations through `PontusM 2025` (`T-PTMF*`).
They do not contain the `RoseM 2025` key required by `T-RSMFDEUC`.

Known plaintext in the OUIT header is sufficient to validate a candidate key,
but it is not sufficient to derive an AES-256 key. Brute force is not a
practical path.

## Repeatable inspection

Use `tools/firmware/inspect_msd.py` to print the clear section table and test
the public unixtract MSD11 key collection:

```sh
python3 tools/firmware/inspect_msd.py /path/to/upgrade.msd \
  --keys /path/to/unixtract/src/keys.ukf
```

The checker tests the legacy MD5 IV derivation and both SHA-256 and SHA-512 IV
derivations used by AES-256 generations.

## Physical TV results (QE77S95FATXXH, firmware 1296.8)

SDB pairing succeeded at `192.168.10.100:26101`. The TV reports Tizen 9.0,
ARM, model group `25TV_PREMIUM3`. Interactive shell support is disabled, so
the tests were performed with the separately packaged `HyperTizenProbe`
service application.

### Compositor capture

`libcapi-ui-efl-util.so.0` exposes both screenshot and screen-mirror APIs.
Contrary to the public API warning that these calls are platform-only, a
normally signed Samsung developer application can use them on this TV.

- `efl_util_screenshot_initialize(320, 180)` succeeds.
- `efl_util_screenshot_take_tbm_surface` returns a mappable `XR24`
  (`XRGB8888`) TBM surface with stride 1280.
- A full TV Home frame was captured and decoded successfully.
- All five `efl_util_screenmirror_*` symbols are present.
- The callback signature observed on ARM is
  `(screenmirror_handle, tbm_surface_handle, user_data)`.
- A two-second test delivered 49-61 frames (approximately 25-30 FPS).

This path captures the complete Wayland UI layer, not only sampled pixels.

### Video-plane limitation

The same tests were repeated while Plex was playing video. Three screenshots
taken at different times were byte-for-byte identical while the player UI was
visible. Once playback entered full-screen video:

- the screenshot surface contained `0/230400` non-zero bytes;
- the continuously delivered screen-mirror TBM surface was also black.

The decoded video is therefore on a separate hardware/DRM overlay plane that
is deliberately absent from the compositor capture. Screen mirror improves
latency and frame rate for UI capture, but does not bypass this boundary.

### Internal video-capture paths

The TV contains several relevant components:

- `libvideo-capture.so.0.1.0`, `libdisplay-capture-api.so.0`, and
  `libep-common-screencapture.so` all depend on `libtzcapturec.so`;
- `libtzcapturec.so` cannot be read or loaded under the developer app label
  (`Operation not permitted`), so that complete chain is blocked;
- `librm-video-capture.so.0`, `libcapi-rm-video-capture.so.0`, and the backend
  `/prd/usr/lib/librm-video-capture-impl.so` are readable;
- the public RM capability check returns `is_supported=0` because
  `com.samsung/featureconf/rm.h264_support` is disabled on this model;
- nevertheless, the developer application can open `/dev/video30` and
  `/dev/dri/card0` read/write.

Static analysis of the RM backend shows that `/dev/video30` is the Samsung
H.264 encoder and `/dev/dri/card0` is configured with Samsung-specific DRM
ioctls (`DRM_IOCTL_SDP_SET_DP_SOURCE`, `DRM_IOCTL_SDP_SET_ONOFF`). This is the
most promising remaining software-only route, but it requires reproducing the
DRM source-to-V4L2 encoder setup. The high-level API refuses it because the
model feature flag is off.

### SWU TrustZone result

The installed SWU trusted application accepts the known session UUID: opening
the session returned success (`0x00000000`, origin 4). Invoking legacy command
3 with the RoseM encrypted passphrase and MSD header salt returned
`0xffff0000` at the invoke stage. Static analysis of the installed 2025
`SWUCoreTV` then recovered the current command-3 operation layout. Unlike the
older public Python tooling, Tizen's header encodes parameter types in bytes:

```
TEEC_PARAM_TYPES(PARTIAL_INPUT, PARTIAL_OUTPUT, VALUE_INOUT, NONE)
```

The input and output are 64 KiB registered shared-memory blocks and
`params[2].value.a` is a one-byte mode selected by the SWU caller. Replaying
this exact layout reached the TA, but mode 0 returned `0xffff0000` and modes
1-3 returned `0xffff000a`. A preceding command-0 state setup visible in
`SWUCoreTV` is therefore also required. Firmware decryption is not complete
and no key material is stored in this repository.

### HyperTizen integration test

Both proven compositor paths are now capture methods in the production
fallback chain. `EflScreenMirrorCaptureMethod` continuously copies callback
TBM surfaces to NV12, while `EflScreenshotCaptureMethod` provides synchronous
snapshots. Both detect black video-overlay frames and advance the chain;
pixel sampling remains the final video fallback.

With full-screen Plex video playing, the installed build rejected both EFL
methods as black, automatically selected the Tizen 9 `ppi_ve_*` pixel path,
and registered successfully with Hyperion. This verifies the selection
behavior on the actual TV, not only the individual probe calls.

## Paths that remain viable

1. **Reconstruct the command-0 SWU setup.** Command 3 is now understood, but
   `SWUCoreTV` first supplies two buffers and two packed value fields to command
   0. Recover those caller inputs and reproduce the state initialization before
   requesting the RoseM passphrase.
2. **Inspect the installed SWU components.** The TV inventory already showed
   `libSWUProductionConfig.so`, `libSWUProductionConfigRelease.so`, and
   `libSoftwareUpgradeAPI.so`. Once SDB is paired, copy permitted binaries and
   inspect their imports, IPC endpoints, trusted-application UUIDs, and policy
   failures.
3. **Prototype the DRM-to-V4L2 path.** The device nodes are accessible even
   though RM reports unsupported. Start with read-only capability ioctls and
   reproduce Samsung's backend sequence with strict cleanup and timeouts.
4. **Keep EFL screen mirror for UI capture.** It is proven at 25-30 FPS and can
   be used when UI-only frames are useful, with pixel sampling as the video
   fallback.

No firmware write or update was attempted. All TV probes were read-only except
for installing the separately named diagnostic TPK.
