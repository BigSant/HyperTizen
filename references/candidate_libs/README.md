# Tizen 9 Candidate Library Binaries

This folder contains the top candidate libraries extracted from the Tizen 9 OS files export for implementing screen capture and pixel sampling.

## Files

### Full Screen Capture Candidates

1. **libvideo-capture.so.0.1.0** (32K) ⭐⭐⭐⭐⭐
   - Most promising for YUV frame capture
   - Contains: `secvideo_api_capture_screen_video_only()`, `ppi_video_capture_get_video_main_yuv()`
   - Plain C API and C++ IVideoCapture class

2. **libcapi-video-capture.so.0.1.0** (14K) ⭐⭐⭐⭐
   - C++ wrapper API for video capture
   - Contains: `VideoCapture::getInstance()`, `VideoCapture::getVideoMainYUV()`
   - Cleaner API than raw ppi_* functions

3. **libdisplay-capture-api.so.0.0** (30K) ⭐⭐⭐⭐
   - Alternative capture API with YUV→RGB conversion
   - Contains: `dc_request_capture_sync()`, `convertYUV2RGB()`
   - Synchronous and asynchronous modes

4. **libep-common-screencapture.so** (18K) ⭐⭐⭐
   - High-level screen capture API
   - Contains: `EPScreenCapture::CaptureScreen()`, JPEG compression
   - Uses IVideoCapture internally

### Pixel Sampling Candidates

5. **libvideoenhance.so.0.1** (124K) ⭐⭐⭐⭐⭐
   - Known working on Tizen 8 (per AGENTS.md)
   - Contains: `ppi_ve_get_rgb_measure_pixel()`, `ppi_ve_set_rgb_measure_position()`
   - Best fallback if frame capture is blocked

### Remote/Streaming Capture

6. **libcapi-rm-video-capture.so.0.0.1** (14K) ⭐⭐
   - Remote/streaming video capture
   - Contains: DRM framebuffer access, encoder functions
   - Lower priority - more complex

## Analysis Files

Full symbol analysis available in:
- `../analysis/*.analysis.txt` - Individual library analysis
- `../analysis/MASTER_ANALYSIS.md` - Summary of all libraries
- `../TIZEN9_CAPTURE_CANDIDATES.md` - Comprehensive guide

## Usage

These binaries can be analyzed with:
```bash
# View exported symbols
nm -D libvideo-capture.so.0.1.0 | grep " T "

# View dynamic symbols
readelf --dyn-syms libvideo-capture.so.0.1.0

# Check dependencies
readelf -d libvideo-capture.so.0.1.0 | grep NEEDED

# Disassemble specific function
objdump -d libvideo-capture.so.0.1.0 | grep -A 20 "secvideo_api_capture_screen"
```

## Next Steps

1. Create P/Invoke declarations for top 3 APIs
2. Implement test harness in DiagnosticCapture.cs
3. Test on actual TV hardware
4. Document results in README.md

See `TIZEN9_CAPTURE_CANDIDATES.md` for detailed implementation recommendations.
