# HyperTizen References - Complete Index

This directory contains comprehensive analysis of Tizen 9 libraries for screen capture implementation.

---

## 📋 Quick Start

**New to this analysis?** Read in this order:

1. **`ANALYSIS_SUMMARY.md`** - Overview of entire analysis
2. **`TIZEN9_CAPTURE_CANDIDATES.md`** - Detailed implementation guide
3. **`candidate_libs/README.md`** - Library binaries guide
4. **`analysis/MASTER_ANALYSIS.md`** - Symbol analysis results

---

## 📁 Directory Structure

```
references/
├── INDEX.md                           # This file
├── ANALYSIS_SUMMARY.md                # Complete analysis overview ⭐ START HERE
├── TIZEN9_CAPTURE_CANDIDATES.md       # Implementation guide ⭐⭐⭐
│
├── tizen9_os_files/                   # Full Tizen 9 OS export (4149 libraries)
│   └── usr/
│       ├── lib/                       # Main library directory
│       └── bin/                       # Binaries and utilities
│
├── candidates/                        # Search results
│   ├── all_matches.txt                # All 293 candidate libraries
│   ├── high_priority.txt              # 18 high-priority libraries
│   ├── known_libs_found.txt           # 35 previously documented libraries
│   ├── video_libs.txt                 # 76 video-related libraries
│   ├── capture_libs.txt               # 24 capture-related libraries
│   ├── screen_libs.txt                # 84 screen/display libraries
│   ├── pixel_libs.txt                 # 18 pixel/enhance libraries
│   ├── graphics_libs.txt              # 24 graphics libraries
│   └── buffer_libs.txt                # 27 buffer/frame libraries
│
├── candidate_libs/                    # Top 6 library binaries ⭐
│   ├── README.md                      # Library binaries guide
│   ├── libvideo-capture.so.0.1.0      # Frame capture (32K) ⭐⭐⭐⭐⭐
│   ├── libvideoenhance.so.0.1         # Pixel sampling (124K) ⭐⭐⭐⭐⭐
│   ├── libcapi-video-capture.so.0.1.0 # C++ wrapper (14K) ⭐⭐⭐⭐
│   ├── libdisplay-capture-api.so.0.0  # Display capture (30K) ⭐⭐⭐⭐
│   ├── libep-common-screencapture.so  # Screen capture (18K) ⭐⭐⭐
│   └── libcapi-rm-video-capture.so.0.0.1  # Remote capture (14K) ⭐⭐
│
└── analysis/                          # Detailed symbol analysis
    ├── MASTER_ANALYSIS.md             # Summary of all analyses
    ├── libvideo-capture.so.0.1.0.analysis.txt
    ├── libvideoenhance.so.0.1.analysis.txt
    ├── libcapi-video-capture.so.0.1.0.analysis.txt
    ├── libdisplay-capture-api.so.0.0.analysis.txt
    ├── libep-common-screencapture.so.analysis.txt
    ├── libcapi-rm-video-capture.so.0.0.1.analysis.txt
    ├── libgfx-video-output.so.0.2.6.analysis.txt
    ├── libscreen_connector_remote_surface.so.1.9.5.analysis.txt
    ├── libdisplay-panel.so.0.1.analysis.txt
    └── librm-video-capture.so.0.1.0.analysis.txt
```

---

## 📚 Document Guide

### Primary Documents (Start Here)

#### ANALYSIS_SUMMARY.md
**Purpose:** High-level overview of entire analysis
**Contains:**
- Analysis process and methodology
- Top candidates summary (Tier 1/2/3)
- Files created during analysis
- Key discoveries
- Recommended testing order
- Expected outcomes
- Integration strategy
- Next actions

**When to use:** First read for understanding what was found

---

#### TIZEN9_CAPTURE_CANDIDATES.md ⭐⭐⭐
**Purpose:** Comprehensive implementation guide
**Contains:**
- Detailed library analysis (all 10 candidates)
- Complete API documentation with function signatures
- P/Invoke patterns and examples
- Implementation recommendations (step-by-step)
- Testing strategy
- Error code reference
- Performance considerations

**When to use:** Implementation phase - reference while coding

---

### Supporting Documents

#### candidate_libs/README.md
**Purpose:** Guide to library binaries
**Contains:**
- File descriptions and sizes
- Analysis command examples
- Usage instructions
- Quick reference

**When to use:** When analyzing binaries directly with `nm`, `readelf`, etc.

---

#### analysis/MASTER_ANALYSIS.md
**Purpose:** Symbol analysis summary
**Contains:**
- Exported functions from all 10 libraries
- Key functions highlighted per library
- Quick reference for what each library exports

**When to use:** Quick lookup of available functions

---

#### analysis/*.analysis.txt files
**Purpose:** Detailed per-library analysis
**Contains:**
- Full `nm -D` output
- `readelf --dyn-syms` output
- Categorized function lists (capture, video, pixel, etc.)
- File metadata

**When to use:** Deep dive into specific library internals

---

## 🎯 Use Cases

### "I want to implement screen capture"
1. Read: `TIZEN9_CAPTURE_CANDIDATES.md` → Section: "Phase 1: Test Full Screen Capture"
2. Copy P/Invoke patterns from same document
3. Reference: `analysis/libvideo-capture.so.0.1.0.analysis.txt` for function details
4. Binary: `candidate_libs/libvideo-capture.so.0.1.0`

### "I want to implement pixel sampling"
1. Read: `TIZEN9_CAPTURE_CANDIDATES.md` → Section: "Phase 2: Pixel Sampling"
2. Reference: `analysis/libvideoenhance.so.0.1.analysis.txt`
3. Binary: `candidate_libs/libvideoenhance.so.0.1`
4. Note: **Already proven working on Tizen 8** (see AGENTS.md)

### "I want to understand what's available"
1. Read: `ANALYSIS_SUMMARY.md` → Section: "Top Candidates Summary"
2. Browse: `candidates/high_priority.txt` for quick list
3. Review: `analysis/MASTER_ANALYSIS.md` for function summaries

### "I want to test a specific library"
1. Find binary: `candidate_libs/[library-name]`
2. Read analysis: `analysis/[library-name].analysis.txt`
3. Get P/Invoke pattern: `TIZEN9_CAPTURE_CANDIDATES.md` → "P/Invoke Patterns"
4. Get testing strategy: `TIZEN9_CAPTURE_CANDIDATES.md` → "Testing Strategy"

### "I'm searching for a specific function"
1. Quick check: `analysis/MASTER_ANALYSIS.md` (search for function name)
2. Detailed check: `grep -r "function_name" analysis/*.analysis.txt`
3. Full search: `grep -r "function_name" candidates/`

---

## 🔧 Scripts

All scripts are in `../scripts/` (one level up):

### find_capture_candidates.sh
**Purpose:** Search for relevant libraries in Tizen 9 OS files
**Input:** `tizen9_os_files/` directory
**Output:** `candidates/` directory with categorized lists
**Usage:**
```bash
../scripts/find_capture_candidates.sh
```

### analyze_lib_symbols.sh
**Purpose:** Extract and analyze exported symbols from libraries
**Input:** High-priority libraries from candidates list
**Output:** `analysis/` directory with detailed analysis files
**Usage:**
```bash
../scripts/analyze_lib_symbols.sh
```

---

## 🏆 Top 6 Candidates (Quick Reference)

### Full Screen Capture

1. **libvideo-capture.so.0.1.0** ⭐⭐⭐⭐⭐
   - APIs: `secvideo_api_capture_screen_video_only()`, `ppi_video_capture_get_video_main_yuv()`
   - Type: Plain C and C++ (IVideoCapture)
   - Status: Test first

2. **libcapi-video-capture.so.0.1.0** ⭐⭐⭐⭐
   - APIs: `VideoCapture::getVideoMainYUV()`
   - Type: C++ wrapper
   - Status: Clean API alternative

3. **libdisplay-capture-api.so.0.0** ⭐⭐⭐⭐
   - APIs: `dc_request_capture_sync()`, `convertYUV2RGB()`
   - Type: Display capture with conversion
   - Status: Alternative approach

### Pixel Sampling

4. **libvideoenhance.so.0.1** ⭐⭐⭐⭐⭐
   - APIs: `ppi_ve_get_rgb_measure_pixel()`, `ppi_ve_set_rgb_measure_position()`
   - Type: RGB pixel measurement
   - Status: **Proven working on Tizen 8**

### Additional Options

5. **libep-common-screencapture.so** ⭐⭐⭐
   - APIs: `EPScreenCapture::CaptureScreen()`
   - Type: High-level screen capture
   - Status: Simpler API, uses IVideoCapture

6. **libcapi-rm-video-capture.so.0.0.1** ⭐⭐
   - APIs: DRM framebuffer, encoder functions
   - Type: Remote/streaming capture
   - Status: Advanced option

---

## 📊 Statistics

- **Total Tizen 9 libraries scanned:** 4,149
- **Candidate libraries found:** 293
- **High-priority candidates:** 18
- **Libraries with detailed analysis:** 10
- **Library binaries extracted:** 6
- **Documentation files created:** 25+
- **Scripts created:** 2

---

## 🚀 Next Steps

### Before TV Testing
1. ✅ Analysis complete
2. ⏭️ Read `TIZEN9_CAPTURE_CANDIDATES.md` implementation guide
3. ⏭️ Create P/Invoke declarations for top 3 APIs
4. ⏭️ Implement test harness in `DiagnosticCapture.cs`

### On TV Hardware
1. ⏭️ Test libvideoenhance.so (verify Tizen 9 compatibility)
2. ⏭️ Test libvideo-capture.so APIs in priority order
3. ⏭️ Monitor via WebSocket logs (`http://<TV_IP>:45678`)
4. ⏭️ Document error codes and results

### Post-Testing
1. ⏭️ Implement working capture method(s)
2. ⏭️ Update `CaptureMethodSelector.cs` priority
3. ⏭️ Update `README.md` and `AGENTS.md`
4. ⏭️ Performance benchmarking

---

## ⚠️ Important Notes

### From AGENTS.md

**Known to work on Tizen 8:**
- ✅ libvideoenhance.so pixel sampling (VideoEnhance_SamplePixel)

**Known blockers:**
- ❌ T8 vtable API returns -95 (operation not supported)
- ❌ T7 API doesn't exist on Tizen 8+
- ❌ libgfx-* libraries cause crashes (blacklisted)
- ❌ libscreen_connector* libraries unstable (blacklisted)

**Testing requirements:**
- 🔴 MUST test on actual TV hardware (not emulator)
- 🔴 MUST use WebSocket logs for debugging
- 🔴 MUST test with non-DRM content (error -4 = DRM protected)

---

## 🔍 Search Tips

### Find all libraries with specific keyword
```bash
grep -i "keyword" candidates/all_matches.txt
```

### Find functions in a library
```bash
grep "function_name" analysis/libname.analysis.txt
```

### Search all analyses for pattern
```bash
grep -r "pattern" analysis/
```

### List libraries by category
```bash
cat candidates/video_libs.txt
cat candidates/capture_libs.txt
cat candidates/pixel_libs.txt
```

---

## 📖 Additional Resources

### Project Documentation
- `../AGENTS.md` - AI assistant guide, architecture, workflows
- `../README.md` - Project status and user guide
- `../docs/README.md` - Installation instructions

### Reference Code
- `GetCaptureFromTZ.c` - Decompiled Samsung T8 implementation
- `../HyperTizen/Capture/*.cs` - Existing capture implementations

### Development Files
- `../scripts/` - Analysis and utility scripts
- `../HyperTizen/` - Main codebase

---

## ✅ Verification Checklist

Before starting implementation:

- [ ] Read `ANALYSIS_SUMMARY.md`
- [ ] Read `TIZEN9_CAPTURE_CANDIDATES.md` Phases 1-3
- [ ] Understand P/Invoke patterns
- [ ] Know error code meanings (-95, -4, 0, 4)
- [ ] Prepared to test on actual TV hardware
- [ ] WebSocket log viewer ready (`http://<TV_IP>:45678`)
- [ ] Diagnostic mode understood
- [ ] Have non-DRM content ready for testing

---

**Index Last Updated:** 2025-11-16
**Analysis Status:** ✅ COMPLETE
**Ready for Implementation:** YES
