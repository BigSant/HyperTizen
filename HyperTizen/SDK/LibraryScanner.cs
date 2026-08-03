using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace HyperTizen.SDK
{
    /// <summary>
    /// Scans /usr/lib/ to discover which shared libraries are accessible on this Tizen device
    /// and probes them for known screen capture symbols.
    /// Call ScanForAlternatives() in diagnostic mode to find alternative capture paths.
    /// Results are streamed to the WebSocket log at port 45678.
    /// </summary>
    public static class LibraryScanner
    {
        private const int RTLD_LAZY = 1;

        #region P/Invoke — libdl.so.2

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlopen(string filename, int flags);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dlclose(IntPtr handle);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlerror();

        #endregion

        #region Known symbols to probe

        private static readonly string[] KnownCaptureSymbols = new string[]
        {
            // SecVideoCapture / libvideo-capture
            "secvideo_api_capture_screen",
            "secvideo_api_capture_screen_video_only",
            "getInstance",

            // libdisplay-capture-api
            "dc_request_capture_sync",
            "dc_request_capture",

            // libvideoenhance — all known prefixes
            "ppi_ve_get_rgb_measure_condition",
            "ppi_ve_set_rgb_measure_position",
            "ppi_ve_get_rgb_measure_pixel",
            "ve_get_rgb_measure_condition",
            "ve_set_rgb_measure_position",
            "ve_get_rgb_measure_pixel",
            "cs_ve_get_rgb_measure_condition",
            "cs_ve_set_rgb_measure_position",
            "cs_ve_get_rgb_measure_pixel",
            "tizen_ve_get_rgb_measure_condition",
            "samsung_ve_get_rgb_measure_condition",
            "get_rgb_measure_condition",

            // EFL screenshot + TBM surface
            "efl_util_screenshot_initialize",
            "efl_util_screenshot_take_tbm_surface",
            "efl_util_screenshot_deinitialize",
            "tbm_surface_map",
            "tbm_surface_unmap",

            // Wayland
            "wl_display_connect",
            "wl_registry_bind",

            // ppi_video_capture (T9 alternate API in libvideo-capture)
            "ppi_video_capture_lock_global",
            "ppi_video_capture_unlock_global",
            "ppi_video_capture_get_video_main_yuv",
            "ppi_video_capture_get_screen_post_yuv",
            "ppi_video_capture_is_protect_capture",
        };

        // Substrings for heuristic symbol search (partial match)
        private static readonly string[] HeuristicPatterns = new string[]
        {
            "capture", "screenshot", "screen_grab", "grab_frame",
            "yuv", "nv12", "framebuffer", "fb_open",
        };

        #endregion

        // Filename substrings used to filter which .so files get dlopen'd.
        // Only files whose name contains at least one of these keywords are probed.
        // This prevents crashing from __attribute__((constructor)) in unrelated libs.
        private static readonly string[] SafeFilenameFilters = new string[]
        {
            "capture", "screen", "video", "display", "frame",
            "yuv", "grab", "tbm", "efl", "wayland", "videoenhance",
            "enhance", "ppi", "secvideo",
        };

        /// <summary>
        /// Main entry point. Safely scans /usr/lib/ in two phases:
        ///   Phase 1 — enumerate ALL filenames (no dlopen, completely safe)
        ///   Phase 2 — filter to capture-relevant names only (~20-50 files)
        ///   Phase 3 — dlopen only the filtered files, probe symbols
        ///   Phase 4 — summary
        /// All output goes to Helper.Log (WebSocket port 45678).
        /// </summary>
        public static void ScanForAlternatives()
        {
            try
            {
                Helper.Log.Write(Helper.eLogType.Info, "");
                Helper.Log.Write(Helper.eLogType.Info, "╔══════════════════════════════════════════════╗");
                Helper.Log.Write(Helper.eLogType.Info, "║        LIBRARY SCANNER — STARTING            ║");
                Helper.Log.Write(Helper.eLogType.Info, "╚══════════════════════════════════════════════╝");
                Helper.Log.Write(Helper.eLogType.Info, "Safe scan: enumerate names → filter → dlopen targeted libs only");
                Helper.Log.Write(Helper.eLogType.Info, "");

                // ── Phase 1: enumerate ALL filenames (no dlopen) ─────────────────────
                var allFiles = EnumerateSoFiles();
                Helper.Log.Write(Helper.eLogType.Info, $"[Scanner] Phase 1: found {allFiles.Count} .so files in /usr/lib/");
                Helper.Log.Write(Helper.eLogType.Info, "[Scanner] Full file list:");
                foreach (var f in allFiles)
                    Helper.Log.Write(Helper.eLogType.Info, $"[Scanner]   {f}");
                Helper.Log.Write(Helper.eLogType.Info, "");

                // ── Phase 2: filter by capture-relevant name keywords ─────────────────
                var targeted = FilterTargetedLibraries(allFiles);
                Helper.Log.Write(Helper.eLogType.Info,
                    $"[Scanner] Phase 2: {targeted.Count} libraries match capture-related keywords — will dlopen these only:");
                foreach (var f in targeted)
                    Helper.Log.Write(Helper.eLogType.Info, $"[Scanner]   → {f}");
                Helper.Log.Write(Helper.eLogType.Info, "");

                // ── Phase 3: dlopen targeted files only, then probe symbols ──────────
                var loaded = new List<(string path, IntPtr handle)>();
                var failed = new List<(string path, string error)>();

                Helper.Log.Write(Helper.eLogType.Info, "[Scanner] Phase 3: dlopen + symbol probe on targeted libraries...");

                foreach (var path in targeted)
                {
                    TryDlopen(path, loaded, failed);
                    System.Threading.Thread.Sleep(20); // brief yield between opens
                }

                Helper.Log.Write(Helper.eLogType.Info,
                    $"[Scanner] dlopen complete: {loaded.Count} loaded, {failed.Count} blocked/failed");
                Helper.Log.Write(Helper.eLogType.Info, "");

                var symbolHits = new Dictionary<string, List<string>>();
                foreach (var (path, handle) in loaded)
                {
                    ProbeSymbols(path, handle, symbolHits);
                }

                foreach (var (path, handle) in loaded)
                {
                    try { dlclose(handle); }
                    catch { /* ignore */ }
                }

                Helper.Log.Write(Helper.eLogType.Info, "[Scanner] Phase 3 complete — all handles closed");
                Helper.Log.Write(Helper.eLogType.Info, "");

                // ── Phase 4: ELF symbol dump — read ALL exported symbols from .so files ──
                Helper.Log.Write(Helper.eLogType.Info, "╔══════════════════════════════════════════════╗");
                Helper.Log.Write(Helper.eLogType.Info, "║   Phase 4: ELF SYMBOL DUMP (all exports)    ║");
                Helper.Log.Write(Helper.eLogType.Info, "╚══════════════════════════════════════════════╝");

                // Dump symbols from key libraries (both loaded and failed — ELF read doesn't need dlopen)
                var elfTargets = new List<string>();
                foreach (var (path, _) in loaded) elfTargets.Add(path);
                foreach (var (path, _) in failed) elfTargets.Add(path);

                foreach (var path in elfTargets)
                {
                    try
                    {
                        DumpElfSymbols(path);
                    }
                    catch (Exception ex)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning,
                            $"[ELF] Failed to parse {path}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                Helper.Log.Write(Helper.eLogType.Info, "");

                // ── Phase 5: summary ──────────────────────────────────────────────────
                PrintSummary(allFiles.Count, loaded, failed, symbolHits);
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"[Scanner] FATAL: Unhandled exception in ScanForAlternatives: {ex.GetType().Name}: {ex.Message}");
            }
        }

        #region Private helpers

        private static List<string> FilterTargetedLibraries(List<string> allFiles)
        {
            var result = new List<string>();
            foreach (var path in allFiles)
            {
                string name = System.IO.Path.GetFileName(path).ToLowerInvariant();
                foreach (var keyword in SafeFilenameFilters)
                {
                    if (name.Contains(keyword))
                    {
                        result.Add(path);
                        break;
                    }
                }
            }
            return result;
        }

        private static List<string> EnumerateSoFiles()
        {
            var files = new List<string>();
            const string libDir = "/usr/lib/";

            try
            {
                foreach (var file in Directory.EnumerateFiles(libDir, "*.so*", SearchOption.TopDirectoryOnly))
                {
                    files.Add(file);
                }
                Helper.Log.Write(Helper.eLogType.Info, $"[Scanner] Enumerated {files.Count} files in {libDir}");
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"[Scanner] Directory enumeration failed: {ex.GetType().Name}: {ex.Message}");
            }

            return files;
        }

        private static void TryDlopen(
            string path,
            List<(string, IntPtr)> loaded,
            List<(string, string)> failed)
        {
            try
            {
                // Clear previous dlerror state
                dlerror();

                IntPtr handle = dlopen(path, RTLD_LAZY);

                if (handle != IntPtr.Zero)
                {
                    Helper.Log.Write(Helper.eLogType.Info, $"[Scanner] LOADED:   {path}");
                    loaded.Add((path, handle));
                }
                else
                {
                    IntPtr errPtr = dlerror();
                    string errMsg = errPtr != IntPtr.Zero
                        ? Marshal.PtrToStringAnsi(errPtr)
                        : "(no error message)";

                    Helper.Log.Write(Helper.eLogType.Warning, $"[Scanner] BLOCKED:  {path}  — {errMsg}");
                    failed.Add((path, errMsg));
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Warning,
                    $"[Scanner] EXCEPTION on dlopen({path}): {ex.GetType().Name}: {ex.Message}");
                failed.Add((path, ex.Message));
            }
        }

        private static void ProbeSymbols(
            string path,
            IntPtr handle,
            Dictionary<string, List<string>> symbolHits)
        {
            var found = new List<string>();

            // Probe exact known symbols
            foreach (var sym in KnownCaptureSymbols)
            {
                try
                {
                    IntPtr addr = dlsym(handle, sym);
                    if (addr != IntPtr.Zero)
                    {
                        found.Add(sym);
                        Helper.Log.Write(Helper.eLogType.Info,
                            $"[Scanner] SYMBOL FOUND: {sym}  in  {path}");
                    }
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"[Scanner] dlsym exception for '{sym}' in {path}: {ex.Message}");
                }
            }

            if (found.Count > 0)
            {
                symbolHits[path] = found;
            }
        }

        private static void PrintSummary(
            int totalFound,
            List<(string path, IntPtr handle)> loaded,
            List<(string path, string error)> failed,
            Dictionary<string, List<string>> symbolHits)
        {
            Helper.Log.Write(Helper.eLogType.Info, "╔══════════════════════════════════════════════╗");
            Helper.Log.Write(Helper.eLogType.Info, "║          LIBRARY SCAN RESULTS                ║");
            Helper.Log.Write(Helper.eLogType.Info, "╚══════════════════════════════════════════════╝");
            Helper.Log.Write(Helper.eLogType.Info, $"Total .so files in /usr/lib/:  {totalFound}");
            Helper.Log.Write(Helper.eLogType.Info, $"Targeted (capture-related):    {loaded.Count + failed.Count}");
            Helper.Log.Write(Helper.eLogType.Info, $"Libraries loaded (accessible): {loaded.Count}");
            Helper.Log.Write(Helper.eLogType.Info, $"Libraries blocked/failed:      {failed.Count}");
            Helper.Log.Write(Helper.eLogType.Info, $"Libraries with capture symbols: {symbolHits.Count}");
            Helper.Log.Write(Helper.eLogType.Info, "");

            // Accessible libraries
            Helper.Log.Write(Helper.eLogType.Info, "── ACCESSIBLE LIBRARIES ──────────────────────");
            foreach (var (path, _) in loaded)
            {
                Helper.Log.Write(Helper.eLogType.Info, $"  OK  {path}");
            }
            Helper.Log.Write(Helper.eLogType.Info, "");

            // Blocked libraries
            Helper.Log.Write(Helper.eLogType.Info, "── BLOCKED / FAILED LIBRARIES ────────────────");
            foreach (var (path, error) in failed)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"  ✗   {path}");
                Helper.Log.Write(Helper.eLogType.Warning, $"      └─ {error}");
            }
            Helper.Log.Write(Helper.eLogType.Info, "");

            // Capture symbols found
            if (symbolHits.Count > 0)
            {
                Helper.Log.Write(Helper.eLogType.Info, "── CAPTURE-RELATED SYMBOLS FOUND ─────────────");
                foreach (var kvp in symbolHits)
                {
                    Helper.Log.Write(Helper.eLogType.Info, $"  {kvp.Key}:");
                    foreach (var sym in kvp.Value)
                    {
                        Helper.Log.Write(Helper.eLogType.Info, $"    + {sym}");
                    }
                }
            }
            else
            {
                Helper.Log.Write(Helper.eLogType.Warning,
                    "── NO CAPTURE SYMBOLS FOUND in any accessible library ────");
            }

            Helper.Log.Write(Helper.eLogType.Info, "");
            Helper.Log.Write(Helper.eLogType.Info, "╔══════════════════════════════════════════════╗");
            Helper.Log.Write(Helper.eLogType.Info, "║        LIBRARY SCANNER — COMPLETE            ║");
            Helper.Log.Write(Helper.eLogType.Info, "╚══════════════════════════════════════════════╝");
            Helper.Log.Write(Helper.eLogType.Info, "");
        }

        /// <summary>
        /// Parse ELF binary directly to extract ALL exported dynamic symbols.
        /// Works without root, dlopen, or any special permissions — just File.Read.
        /// Supports 32-bit and 64-bit ELF (ARM TV is typically 32-bit userspace).
        /// </summary>
        private static void DumpElfSymbols(string path)
        {
            if (!File.Exists(path)) return;

            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"[ELF] Cannot read {path}: {ex.Message}");
                return;
            }

            if (data.Length < 64 || data[0] != 0x7F || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F')
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"[ELF] {path}: Not a valid ELF file");
                return;
            }

            bool is64 = data[4] == 2; // EI_CLASS: 1=32bit, 2=64bit
            bool isLE = data[5] == 1; // EI_DATA: 1=little-endian

            Helper.Log.Write(Helper.eLogType.Info, $"[ELF] ── {Path.GetFileName(path)} ({(is64 ? "64" : "32")}bit, {(isLE ? "LE" : "BE")}, {data.Length} bytes) ──");

            try
            {
                // Read section header table offset, entry size, count
                long shoff;
                int shentsize, shnum, shstrndx;

                if (is64)
                {
                    shoff = ReadInt64(data, 40, isLE);
                    shentsize = ReadUInt16(data, 58, isLE);
                    shnum = ReadUInt16(data, 60, isLE);
                    shstrndx = ReadUInt16(data, 62, isLE);
                }
                else
                {
                    shoff = ReadUInt32(data, 32, isLE);
                    shentsize = ReadUInt16(data, 46, isLE);
                    shnum = ReadUInt16(data, 48, isLE);
                    shstrndx = ReadUInt16(data, 50, isLE);
                }

                if (shoff <= 0 || shnum == 0 || shoff + (long)shnum * shentsize > data.Length)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"[ELF] {Path.GetFileName(path)}: Invalid section header table");
                    return;
                }

                // Read section header string table
                long shstrOff = is64
                    ? ReadInt64(data, (int)(shoff + shstrndx * shentsize + 24), isLE)
                    : ReadUInt32(data, (int)(shoff + shstrndx * shentsize + 16), isLE);

                // Find .dynsym and .dynstr sections
                long dynsymOff = 0, dynsymSize = 0;
                long dynstrOff = 0, dynstrSize = 0;
                int dynsymEntSize = is64 ? 24 : 16;

                for (int i = 0; i < shnum; i++)
                {
                    int shEntry = (int)(shoff + i * shentsize);
                    if (shEntry + shentsize > data.Length) break;

                    int nameIdx = ReadInt32(data, shEntry, isLE);
                    int shType = ReadInt32(data, shEntry + 4, isLE);
                    string secName = ReadStringAt(data, (int)(shstrOff + nameIdx));

                    if (secName == ".dynsym" && shType == 11) // SHT_DYNSYM
                    {
                        dynsymOff = is64 ? ReadInt64(data, shEntry + 24, isLE) : ReadUInt32(data, shEntry + 16, isLE);
                        dynsymSize = is64 ? ReadInt64(data, shEntry + 32, isLE) : ReadUInt32(data, shEntry + 20, isLE);
                        dynsymEntSize = is64 ? ReadInt32(data, shEntry + 56, isLE) : ReadInt32(data, shEntry + 36, isLE);
                        if (dynsymEntSize == 0) dynsymEntSize = is64 ? 24 : 16;
                    }
                    else if (secName == ".dynstr" && shType == 3) // SHT_STRTAB
                    {
                        dynstrOff = is64 ? ReadInt64(data, shEntry + 24, isLE) : ReadUInt32(data, shEntry + 16, isLE);
                        dynstrSize = is64 ? ReadInt64(data, shEntry + 32, isLE) : ReadUInt32(data, shEntry + 20, isLE);
                    }
                }

                if (dynsymOff == 0 || dynstrOff == 0)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"[ELF] {Path.GetFileName(path)}: No .dynsym/.dynstr sections found");
                    return;
                }

                // Parse symbol entries
                int numSymbols = (int)(dynsymSize / dynsymEntSize);
                var exportedFunctions = new List<string>();

                for (int i = 0; i < numSymbols; i++)
                {
                    int symEntry = (int)(dynsymOff + i * dynsymEntSize);
                    if (symEntry + dynsymEntSize > data.Length) break;

                    int stName;
                    byte stInfo;
                    int stShndx;

                    if (is64)
                    {
                        stName = ReadInt32(data, symEntry, isLE);
                        stInfo = data[symEntry + 4];
                        stShndx = ReadUInt16(data, symEntry + 6, isLE);
                    }
                    else
                    {
                        stName = ReadInt32(data, symEntry, isLE);
                        stInfo = data[symEntry + 12];
                        stShndx = ReadUInt16(data, symEntry + 14, isLE);
                    }

                    // Filter: only FUNC or OBJECT with GLOBAL/WEAK binding, defined (shndx != 0)
                    int bind = stInfo >> 4;   // STB_GLOBAL=1, STB_WEAK=2
                    int type = stInfo & 0xF;  // STT_FUNC=2, STT_OBJECT=1

                    if (stShndx == 0) continue; // undefined (imported)
                    if (bind == 0) continue;     // STB_LOCAL
                    if (type != 1 && type != 2) continue; // not FUNC or OBJECT

                    int nameOffset = (int)(dynstrOff + stName);
                    if (nameOffset >= data.Length) continue;

                    string symName = ReadStringAt(data, nameOffset);
                    if (string.IsNullOrEmpty(symName)) continue;

                    string typeStr = type == 2 ? "FUNC" : "OBJ ";
                    exportedFunctions.Add($"  {typeStr} {symName}");
                }

                Helper.Log.Write(Helper.eLogType.Info, $"[ELF] {Path.GetFileName(path)}: {exportedFunctions.Count} exported symbols:");
                foreach (var sym in exportedFunctions)
                {
                    Helper.Log.Write(Helper.eLogType.Info, $"[ELF] {sym}");
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"[ELF] Parse error in {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        #region ELF binary helpers

        private static string ReadStringAt(byte[] data, int offset)
        {
            if (offset < 0 || offset >= data.Length) return "";
            int end = offset;
            while (end < data.Length && data[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(data, offset, end - offset);
        }

        private static int ReadInt32(byte[] data, int offset, bool isLE)
        {
            if (offset + 4 > data.Length) return 0;
            if (isLE) return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
            return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }

        private static long ReadInt64(byte[] data, int offset, bool isLE)
        {
            if (offset + 8 > data.Length) return 0;
            uint lo = (uint)ReadInt32(data, offset, isLE);
            uint hi = (uint)ReadInt32(data, offset + 4, isLE);
            if (isLE) return (long)hi << 32 | lo;
            return (long)lo << 32 | hi;
        }

        private static uint ReadUInt32(byte[] data, int offset, bool isLE)
        {
            return (uint)ReadInt32(data, offset, isLE);
        }

        private static int ReadUInt16(byte[] data, int offset, bool isLE)
        {
            if (offset + 2 > data.Length) return 0;
            if (isLE) return data[offset] | (data[offset + 1] << 8);
            return (data[offset] << 8) | data[offset + 1];
        }

        #endregion

        #endregion
    }
}
