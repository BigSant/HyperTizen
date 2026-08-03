# Tizen 9 Screen Capture Candidate Libraries - Summary

**Analysis Date:** 2025-11-16
**Source:** Tizen 9 OS Files Export
**Total Libraries Scanned:** 4149
**Candidate Libraries Found:** 293
**High-Priority Candidates:** 18

---

## Executive Summary

This document summarizes the most promising libraries for implementing **full screen capture** and **pixel sampling** on Tizen 9 TVs. Analysis was performed using `nm` and `readelf` to identify exported function symbols.

### Key Findings

1. ✅ **libvideo-capture.so.0.1.0** - MOST PROMISING for full screen capture (YUV frame capture)
2. ✅ **libvideoenhance.so.0.1** - PROMISING for pixel sampling (RGB measurement)
3. ✅ **libdisplay-capture-api.so.0.0** - Alternative for screen capture (YUV/RGB conversion)
4. ✅ **libcapi-video-capture.so.0.1.0** - C++ wrapper API for IVideoCapture
5. ⚠️ **libep-common-screencapture.so** - Potential alternative (uses IVideoCapture internally)

---

## TOP PRIORITY: Full Screen Capture Libraries

### 1. libvideo-capture.so.0.1.0 ⭐⭐⭐⭐⭐

**Path:** `/usr/lib/libvideo-capture.so.0.1.0`
**Size:** 32K
**Architecture:** ARM 32-bit
**Status:** HIGHEST PRIORITY - Direct successor to T8 API

#### Key Exported Functions

**Primary Capture Functions (Plain C API - ppi_video_capture_*):**
```c
// Main video capture
ppi_video_capture_get_video_main_yuv      // Main video YUV capture
ppi_video_capture_get_video_post_yuv      // Post-processed video YUV
ppi_video_capture_get_video_sub_yuv       // Sub video YUV
ppi_video_capture_get_video_bg_yuv        // Background video YUV
ppi_video_capture_get_video_yuv           // Generic video YUV
ppi_video_capture_get_screen_post_yuv     // Screen post-processing YUV

// Cropped variants
ppi_video_capture_get_cropped_video_main_yuv
ppi_video_capture_get_cropped_video_post_yuv
ppi_video_capture_get_cropped_video_sub_yuv

// Lock/Unlock for thread safety
ppi_video_capture_lock_global
ppi_video_capture_unlock_global
ppi_video_capture_lock_global_all
ppi_video_capture_unlock_global_all

// DRM protection checks
ppi_video_capture_is_protect_capture
ppi_video_capture_is_protect_capture_all

// Backend access
ppi_video_capture_get_backend
ppi_video_capture_get_backend_data
ppi_video_capture_get_backend_data_address
```

**Secondary API (secvideo_api_*):**
```c
// Screen capture API
secvideo_api_capture_screen                    // Full screen capture
secvideo_api_capture_screen_video_only         // Screen video only (no UI)
secvideo_api_capture_screen_video_only_crop    // Cropped screen video
secvideo_api_capture_screen_lock               // Lock for screen capture
secvideo_api_capture_screen_unlock             // Unlock after capture

// Video capture API
secvideo_api_capture                           // Generic capture
secvideo_api_capture_video_main                // Main video capture
secvideo_api_capture_video_main_crop           // Cropped main video
secvideo_api_capture_video_sub                 // Sub video capture
secvideo_api_capture_video_sub_crop            // Cropped sub video
secvideo_api_capture_video_bg                  // Background video
secvideo_api_capture_encoder_input             // Encoder input capture

// Protection checks
secvideo_api_capture_is_protect
secvideo_api_capture_plane_is_protect
secvideo_api_capture_lock
secvideo_api_capture_unlock
```

**C++ API (IVideoCapture class):**
```c++
IVideoCapture::getInstance()                   // Singleton instance
IVideoCapture::getVideoMainYUV(InputParams, OutputParams)
IVideoCapture::getVideoPostYUV(InputParams, OutputParams)
IVideoCapture::getVideoSubYUV(InputParams, OutputParams)
IVideoCapture::getVideoBGYUV(InputParams, OutputParams)
IVideoCapture::getScreenPostYUV(InputParams, OutputParams)
IVideoCapture::getCroppedVideoMainYUV(InputParams, OutputParams)
IVideoCapture::getCroppedVideoPostYUV(InputParams, OutputParams)
IVideoCapture::getCroppedVideoSubYUV(InputParams, OutputParams)
IVideoCapture::lockGlobal()
IVideoCapture::unlockGlobal()
IVideoCapture::isProtectCapture(int&)
```

#### Recommended Approach

1. **Try `secvideo_api_capture_screen_video_only()` FIRST**
   - Plain C function - easiest to P/Invoke
   - Captures screen video without UI overlay
   - Likely returns YUV data

2. **Fallback to `ppi_video_capture_get_video_main_yuv()`**
   - Direct main video capture
   - May require lock/unlock

3. **Last resort: C++ IVideoCapture API**
   - More complex P/Invoke (requires getInstance())
   - Use `getVideoMainYUV()` or `getScreenPostYUV()`

#### Dependencies

- Uses TrustZone capture: `TZCAPTURE_Initialize`, `TZCAPTURE_Capture_screen`, `TZCAPTURE_Finalize`
- Uses OpenCV: `cv::Mat`, `cv::flip`
- May require initialization before use

---

### 2. libcapi-video-capture.so.0.1.0 ⭐⭐⭐⭐

**Path:** `/usr/lib/libcapi-video-capture.so.0.1.0`
**Size:** Not specified
**Architecture:** ARM 32-bit
**Status:** HIGH PRIORITY - Clean C++ API wrapper

#### Key Exported Functions

```c++
// Main class
VideoCapture::getInstance()                    // Get singleton instance
VideoCapture::getVideoMainYUV(InputParams, OutputParams)
VideoCapture::getVideoPostYUV(InputParams, OutputParams)
VideoCapture::getVideoSubYUV(InputParams, OutputParams)
VideoCapture::getVideoBGYUV(InputParams, OutputParams)
VideoCapture::getScreenPostYUV(InputParams, OutputParams)
VideoCapture::getCroppedVideoMainYUV(InputParams, OutputParams)
VideoCapture::getCroppedVideoPostYUV(InputParams, OutputParams)
VideoCapture::getCroppedVideoSubYUV(InputParams, OutputParams)
VideoCapture::getVideoYUVToEncoder(InputParams, OutputParams)

// Lock/Unlock
VideoCapture::lockGlobal()
VideoCapture::lockGlobal(CaptureLockType, int)
VideoCapture::unlockGlobal()
VideoCapture::unlockGlobal(CaptureLockType, int)

// Protection
VideoCapture::isProtectCapture(int&)
VideoCapture::isProtectCapture(CaptureLockType, int, int&)
```

#### Notes

- This is likely a **wrapper** around libvideo-capture.so
- Provides cleaner C++ API with RAII semantics
- May be easier to use than raw ppi_* functions
- Versioned as 0.1.0 (same as libvideo-capture.so.0.1.0)

---

### 3. libdisplay-capture-api.so.0.0 ⭐⭐⭐⭐

**Path:** `/usr/lib/libdisplay-capture-api.so.0.0`
**Size:** Not specified
**Architecture:** ARM 32-bit
**Status:** HIGH PRIORITY - Alternative capture API with YUV→RGB conversion

#### Key Exported Functions

```c
// Request-based capture API
dc_request_capture()                           // Async capture request
dc_request_capture_sync()                      // Synchronous capture
dc_request_capture_to_file_sync()              // Capture directly to file

// Internal functions (C++)
ppicapture(RequestedData*, unsigned char*, unsigned char*, int)
convertYUV2RGB(uchar, uchar, uchar, uchar*, uchar*, uchar*)
generate_rgb_buffer(uchar*, uchar*, uchar*, int, int, int, int)
_convertYUV420toBGRA(uchar*, uchar*, uchar*, int, int, int)
_convertYUV422toBGRA(uchar*, uchar*, uchar*, int, int, int)
m_capture(dc_app_type_e, dc_mode_e, int, int, const char*, const char*)
m_capture_to_file_sync(_dc_capt_req_data*)

// Image processing
crop_and_stretch_left_img_YUV420(uchar*, uchar*, int, int, int)
crop_and_stretch_left_img_YUV422(uchar*, uchar*, int, int, int)
```

#### Notes

- Provides **YUV→RGB conversion** built-in
- Has **synchronous** and **asynchronous** capture modes
- Can capture **directly to file**
- Supports YUV420 and YUV422 formats
- May emit D-Bus signals on completion

---

## TOP PRIORITY: Pixel Sampling Libraries

### 4. libvideoenhance.so.0.1 ⭐⭐⭐⭐⭐

**Path:** `/usr/lib/libvideoenhance.so.0.1`
**Size:** 124K
**Architecture:** ARM 32-bit
**Status:** HIGHEST PRIORITY - Known working on Tizen 8 (per AGENTS.md)

#### Key Exported Functions

**RGB Pixel Measurement:**
```c
ppi_ve_get_rgb_measure_pixel()                 // Get RGB value at specific pixel
ppi_ve_set_rgb_measure_position()              // Set pixel position to measure
ppi_ve_get_rgb_measure_condition()             // Get measurement conditions
ppi_ve_set_rgb_only_mode()                     // Enable RGB-only mode

// Frame-level functions
ppi_ve_get_framebacklight()                    // Get frame backlight info
ppi_ve_set_framebacklight()                    // Set frame backlight
ppi_ve_get_framelux_level()                    // Get frame lux level
ppi_ve_set_framelux_cb()                       // Set lux callback
ppi_ve_get_frame_lock()                        // Check frame lock status
```

**Video Enhancement:**
```c
ppi_ve_set_8k_streaming_enhance()
ppi_ve_set_aodmode_enhance()
ppi_ve_set_cameramode_enhance()
ppi_ve_set_lifestyle_enhance()
ppi_ve_set_picture_enhancement()
ppi_ve_set_sideblack_enhance()
```

**Pixel Shift (for testing):**
```c
ppi_ve_set_pixelShift()
ppi_ve_set_pixel_shift_onoff()
ppi_ve_qd_pixel_shift_test()
```

#### Recommended Approach

**Based on AGENTS.md - VideoEnhance is the ONLY working capture method on Tizen 8+**

1. Use `ppi_ve_set_rgb_measure_position(x, y)` to set pixel coordinates
2. Call `ppi_ve_get_rgb_measure_pixel()` to get RGB values
3. Sample multiple pixels across screen
4. Build frame from sampled pixels

**Performance Considerations:**
- Pixel sampling is **slower** than frame capture
- Need to optimize batch sizes and sleep times
- May need different sampling patterns (grid, edge detection, etc.)

---

## ALTERNATIVE: Screen Capture Libraries

### 5. libep-common-screencapture.so ⭐⭐⭐

**Path:** `/usr/lib/libep-common-screencapture.so`
**Size:** Not specified
**Architecture:** ARM 32-bit
**Status:** MODERATE PRIORITY - May use IVideoCapture internally

#### Key Exported Functions

```c++
// Main capture functions
EPScreenCapture::CaptureScreen(ImageProperties*, bool)
EPScreenCapture::ScreenCapture::capture()
EPScreenCapture::ScreenCapture::saveToBuffer(Buffer&)
EPScreenCapture::ScreenCapture::saveTo(string&)
EPScreenCapture::ScreenCapture::compressToJpeg()

// Initialization
EPScreenCapture::ScreenCapture::ScreenCapture(uint, uint)  // Constructor with width/height
EPScreenCapture::ScreenCapture::initInput(uint, uint)
EPScreenCapture::ScreenCapture::initOutput()
EPScreenCapture::ScreenCapture::initJpeg()

// YUV buffer handling
EPScreenCapture::YUVBuffer::getYUV420(uint)
EPScreenCapture::YUVBuffer::getYUV422(uint)
EPScreenCapture::YUVBuffer::getYUV444(uint)
EPScreenCapture::YUVBuffer::YUVBuffer(IVideoCapture::OutputParams&)  // Uses IVideoCapture!

// Graphic layer capture
ns_graphiccapture::GraphicLayerCapture(string, string, bool)
```

#### Notes

- **Uses IVideoCapture internally** (see `YUVBuffer` constructor)
- Provides **JPEG compression** built-in
- Has **file saving** capabilities
- May be easier to use than raw IVideoCapture
- C++ API (requires more complex P/Invoke)

---

### 6. librm-video-capture.so.0.1.0 ⭐⭐

**Path:** `/usr/lib/librm-video-capture.so.0.1.0`
**Size:** Not specified
**Architecture:** ARM 32-bit
**Status:** LOW PRIORITY - Remote/streaming capture (encoder-focused)

#### Key Exported Functions

```c
// Initialization
ppi_rm_video_capture_init()
ppi_rm_video_capture_check_init()

// Streaming
ppi_rm_video_capture_subscribe_stream()
ppi_rm_video_capture_unsubscribe_stream()
ppi_rm_video_capture_set_stream_on()
ppi_rm_video_capture_set_stream_off()

// Encoder configuration
ppi_rm_video_capture_set_resolution()
ppi_rm_video_capture_get_resolution()
ppi_rm_video_capture_set_framerate()
ppi_rm_video_capture_set_bitrate()
ppi_rm_video_capture_encoder_open()
ppi_rm_video_capture_encoder_close()

// DRM functions
ppi_rm_video_capture_drm_open()
ppi_rm_video_capture_drm_close()
ppi_rm_video_capture_drm_create_framebuffer()
ppi_rm_video_capture_drm_set_plane()
ppi_rm_video_capture_drm_set_source()
```

#### Notes

- Focused on **remote streaming** and **encoding**
- Uses **DRM (Direct Rendering Manager)** for framebuffer access
- May be for **screen mirroring** or **remote desktop** functionality
- Lower priority - more complex than direct capture

---

## SUPPLEMENTARY: Graphics Libraries

### 7. libgfx-video-output.so.0.2.6 ⚠️

**Path:** `/usr/lib/libgfx-video-output.so.0.2.6`
**Status:** BLACKLISTED in AGENTS.md - Known to crash

**DO NOT USE:** Per AGENTS.md, libgfx-* libraries cause undefined symbol crashes.

---

### 8. libscreen_connector_remote_surface.so.1.9.5 ⚠️

**Path:** `/usr/lib/libscreen_connector_remote_surface.so.1.9.5`
**Status:** BLACKLISTED in AGENTS.md - Wayland/graphics issues

**DO NOT USE:** Per AGENTS.md, screen_connector libraries are unstable.

---

### 9. libdisplay-panel.so.0.1 ⭐

**Path:** `/usr/lib/libdisplay-panel.so.0.1`
**Status:** LOW PRIORITY - Utility functions only

#### Key Functions

```c
ppi_displaypanel_get_pixel_ic_power()
ppi_displaypanel_get_fpga_vx1_lock_status()
```

#### Notes

- Very limited API
- Utility functions for panel status
- Not useful for capture

---

## Implementation Recommendations

### Phase 1: Test Full Screen Capture (Highest Priority)

**Try in this order:**

1. **libvideo-capture.so.0.1.0 → secvideo_api_capture_screen_video_only()**
   - Simplest C API
   - Direct screen video capture
   - Document results vs T8 API behavior

2. **libvideo-capture.so.0.1.0 → ppi_video_capture_get_video_main_yuv()**
   - With lock/unlock
   - May require backend initialization

3. **libcapi-video-capture.so.0.1.0 → VideoCapture::getVideoMainYUV()**
   - Clean C++ API
   - Requires getInstance() pattern

4. **libdisplay-capture-api.so.0.0 → dc_request_capture_sync()**
   - Alternative API
   - Built-in YUV→RGB conversion

### Phase 2: Pixel Sampling (Fallback)

**If full screen capture fails/is blocked:**

1. **libvideoenhance.so.0.1 → ppi_ve_get_rgb_measure_pixel()**
   - Known to work on Tizen 8 (per AGENTS.md)
   - Slower but reliable
   - Optimize sampling pattern

### Phase 3: Alternative Approaches

1. **libep-common-screencapture.so**
   - If simpler API needed
   - Uses IVideoCapture internally

---

## P/Invoke Patterns

### Pattern 1: Plain C Functions

```csharp
[DllImport("libvideo-capture.so.0.1.0", CallingConvention = CallingConvention.Cdecl)]
private static extern int secvideo_api_capture_screen_video_only(
    ref InputParams inputParams,
    ref OutputParams outputParams
);
```

### Pattern 2: C++ Singleton Pattern

```csharp
// Step 1: Get getInstance function pointer
[DllImport("libcapi-video-capture.so.0.1.0", CallingConvention = CallingConvention.Cdecl)]
private static extern IntPtr _ZN12VideoCapture11getInstanceEv();

// Step 2: Call method via vtable
// (Requires vtable analysis - see T8SdkCaptureMethod.cs)
```

### Pattern 3: dlopen/dlsym Safe Probing

```csharp
const int RTLD_LAZY = 1;
IntPtr handle = dlopen("/usr/lib/libvideo-capture.so.0.1.0", RTLD_LAZY);
if (handle != IntPtr.Zero)
{
    IntPtr symbol = dlsym(handle, "secvideo_api_capture_screen_video_only");
    if (symbol != IntPtr.Zero)
    {
        // Symbol exists - safe to DllImport
    }
    dlclose(handle);
}
```

---

## Testing Strategy

### Step 1: Symbol Verification (On TV Hardware)

SSH to TV and verify symbols exist:

```bash
nm -D /usr/lib/libvideo-capture.so.0.1.0 | grep -i "capture_screen"
nm -D /usr/lib/libvideoenhance.so.0.1 | grep -i "rgb_measure_pixel"
```

### Step 2: dlopen Testing

Use dlopen/dlsym in C# to verify libraries load without crashing:

```csharp
Log.Info("Testing libvideo-capture.so.0.1.0...");
IntPtr handle = dlopen("/usr/lib/libvideo-capture.so.0.1.0", RTLD_LAZY);
if (handle == IntPtr.Zero)
{
    string error = Marshal.PtrToStringAnsi(dlerror());
    Log.Error($"dlopen failed: {error}");
}
else
{
    Log.Info("Library loaded successfully!");
    dlclose(handle);
}
```

### Step 3: Function Call Testing

Implement test in DiagnosticCapture.cs:

```csharp
public void TestVideoCaptureAPIs()
{
    // Test 1: secvideo_api_capture_screen_video_only
    TestAPI("secvideo_api_capture_screen_video_only", () => {
        // P/Invoke call here
    });

    // Test 2: ppi_video_capture_get_video_main_yuv
    TestAPI("ppi_video_capture_get_video_main_yuv", () => {
        // P/Invoke call here
    });

    // Test 3: VideoCapture::getVideoMainYUV
    TestAPI("VideoCapture::getVideoMainYUV", () => {
        // C++ getInstance + vtable call
    });
}
```

### Step 4: Monitor via WebSocket Logs

Watch `http://<TV_IP>:45678` for:
- Success codes (0, 4)
- Error -95 (API blocked)
- Error -4 (DRM content)
- Crashes (library load failures)

---

## Error Code Reference

| Code | Meaning | Action |
|------|---------|--------|
| **0** | Success | Validate frame data |
| **4** | Success (alternate) | Validate frame data |
| **-4** | DRM protected | Switch to non-DRM content |
| **-95** | Operation not supported | Try alternative API |
| **-99** | Not initialized | Check initialization sequence |

---

## Next Steps

1. ✅ **Copy this summary to references/** (DONE - this file)
2. ⏭️ **Create P/Invoke declarations** for top 3 APIs
3. ⏭️ **Implement test harness** in DiagnosticCapture.cs
4. ⏭️ **Test on actual TV hardware** via WebSocket logs
5. ⏭️ **Document results** in README.md and AGENTS.md

---

## Files for Further Investigation

Detailed symbol analysis available in:
- `/references/analysis/libvideo-capture.so.0.1.0.analysis.txt`
- `/references/analysis/libvideoenhance.so.0.1.analysis.txt`
- `/references/analysis/libdisplay-capture-api.so.0.0.analysis.txt`
- `/references/analysis/libcapi-video-capture.so.0.1.0.analysis.txt`
- `/references/analysis/MASTER_ANALYSIS.md`

---

## References

- **AGENTS.md** - Tizen capture architecture and known blockers
- **GetCaptureFromTZ.c** - Decompiled Samsung T8 implementation
- **T8SdkCaptureMethod.cs** - Existing T8 vtable implementation
- **PixelSamplingCaptureMethod.cs** - Working VideoEnhance implementation (Tizen 8)

---

**Document Status:** Initial analysis complete
**Hardware Testing:** Required
**Confidence Level:** HIGH for libvideo-capture.so, VERY HIGH for libvideoenhance.so
