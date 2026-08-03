using System;
using System.IO;
using System.Runtime.InteropServices;

namespace HyperTizen.Capture
{
    /// <summary>
    /// EFL Screenshot capture method via libcapi-ui-efl-util.so.0
    /// Uses efl_util_screenshot_initialize → take_tbm_surface → tbm_surface_map
    /// Returns frame converted to NV12 for the HyperHDR pipeline.
    ///
    /// NOTE: EFL screenshot captures the Wayland compositor output.
    /// On Samsung TVs the video plane may be a separate hardware layer — this method
    /// may return a transparent frame for live video content, but worth testing.
    ///
    /// Priority: T8SDK = 3 (same as SecVideoCapture, after T9 methods, before PixelSampling)
    /// </summary>
    public class EflScreenshotCaptureMethod : ICaptureMethod
    {
        public string Name => "EflScreenshot (libcapi-ui-efl-util)";
        public CaptureMethodType Type => CaptureMethodType.EflScreenshot;

        private const string LibEflUtil = "libcapi-ui-efl-util.so.0";
        private const string LibTbm     = "libtbm.so.1";

        private const int TBM_SURF_OPTION_READ = (1 << 0);

        // ── P/Invoke ─────────────────────────────────────────────────────────

        [DllImport(LibEflUtil, EntryPoint = "efl_util_screenshot_initialize",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr Screenshot_Initialize(int width, int height);

        [DllImport(LibEflUtil, EntryPoint = "efl_util_screenshot_take_tbm_surface",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr Screenshot_TakeTbmSurface(IntPtr screenshotHandle);

        [DllImport(LibEflUtil, EntryPoint = "efl_util_screenshot_deinitialize",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int Screenshot_Deinitialize(IntPtr screenshotHandle);

        [DllImport(LibTbm, EntryPoint = "tbm_surface_map",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int Tbm_SurfaceMap(IntPtr surface, int opt, out TbmSurfaceInfo info);

        [DllImport(LibTbm, EntryPoint = "tbm_surface_unmap",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int Tbm_SurfaceUnmap(IntPtr surface);

        // tbm_surface_destroy frees the surface returned by take_tbm_surface.
        // Must be called after unmap.
        [DllImport(LibTbm, EntryPoint = "tbm_surface_destroy",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern int Tbm_SurfaceDestroy(IntPtr surface);

        // ── TBM structs ───────────────────────────────────────────────────────

        // tbm_surface_plane_s — Tizen libtbm header (ARM64):
        //   uint8_t *ptr;      // 8 bytes
        //   uint32_t size;     // 4 bytes
        //   uint32_t offset;   // 4 bytes
        //   uint32_t stride;   // 4 bytes
        //   void *reserved1;   // 8 bytes  ← required or next plane offset is wrong
        //   void *reserved2;   // 8 bytes
        //   void *reserved3;   // 8 bytes
        // Total: 48 bytes per plane
        [StructLayout(LayoutKind.Sequential)]
        private struct TbmPlane
        {
            public IntPtr ptr;      // uint8_t* — 8 bytes on ARM64
            public uint   size;
            public uint   offset;
            public uint   stride;
            public IntPtr reserved1;
            public IntPtr reserved2;
            public IntPtr reserved3;
        }

        // tbm_surface_info_s:
        //   uint32_t width, height, format, bpp, size, num_planes  (24 bytes)
        //   tbm_surface_plane_s planes[4]                           (4 × 48 = 192 bytes)
        // Total: 216 bytes
        [StructLayout(LayoutKind.Sequential)]
        private struct TbmSurfaceInfo
        {
            public uint     width;
            public uint     height;
            public uint     format;
            public uint     bpp;
            public uint     size;
            public uint     num_planes;
            public TbmPlane plane0;
            public TbmPlane plane1;
            public TbmPlane plane2;
            public TbmPlane plane3;
        }

        // ── ICaptureMethod ────────────────────────────────────────────────────

        public bool IsAvailable()
        {
            Helper.Log.Write(Helper.eLogType.Info, "[EflScreenshot] Checking availability...");

            bool eflExists = File.Exists("/usr/lib/libcapi-ui-efl-util.so.0");
            bool tbmExists = File.Exists("/usr/lib/libtbm.so.1");

            Helper.Log.Write(Helper.eLogType.Info,
                $"[EflScreenshot] libcapi-ui-efl-util.so.0: {eflExists}, libtbm.so.1: {tbmExists}");

            if (!eflExists || !tbmExists)
            {
                Helper.Log.Write(Helper.eLogType.Warning, "[EflScreenshot] Not available — required libs missing");
                return false;
            }

            Helper.Log.Write(Helper.eLogType.Info, "[EflScreenshot] Available");
            return true;
        }

        public bool Test()
        {
            Helper.Log.Write(Helper.eLogType.Info, "[EflScreenshot] Running capture test at 480x270...");

            try
            {
                var result = CaptureInternal(480, 270);
                if (result.Success)
                {
                    Helper.Log.Write(Helper.eLogType.Info, "[EflScreenshot] Test PASSED");
                    return true;
                }

                Helper.Log.Write(Helper.eLogType.Warning,
                    $"[EflScreenshot] Test FAILED: {result.ErrorMessage}");
                return false;
            }
            catch (DllNotFoundException ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"[EflScreenshot] Library not found: {ex.Message}");
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"[EflScreenshot] Entry point not found: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"[EflScreenshot] Test exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public CaptureResult Capture(int width, int height)
        {
            return CaptureInternal(width, height);
        }

        public void Cleanup()
        {
            if (_screenshotHandle != IntPtr.Zero)
            {
                try { Screenshot_Deinitialize(_screenshotHandle); } catch { }
                _screenshotHandle = IntPtr.Zero;
                Helper.Log.Write(Helper.eLogType.Info, "[EflScreenshot] Screenshot handle released");
            }
            Helper.Log.Write(Helper.eLogType.Info, "[EflScreenshot] Cleaned up");
        }

        // ── Private ───────────────────────────────────────────────────────────

        private bool   _firstFrameLogged = false;

        // Persistent screenshot handle — initialized once, reused every frame.
        // Calling screenshot_initialize/deinitialize per-frame opens/closes a Wayland
        // session each time, which is expensive (~4-9ms overhead) and overloads the TV
        // compositor under sustained load.
        private IntPtr _screenshotHandle = IntPtr.Zero;

        // Performance tracking — logged every 30 frames
        private int    _perfFrameCount  = 0;
        private long   _perfCaptureMs   = 0; // take_tbm_surface + map
        private long   _perfCopyMs      = 0; // Marshal.Copy loop
        private long   _perfConvertMs   = 0; // NV12 conversion
        private long   _perfCleanupMs   = 0; // unmap + surface destroy
        private const int PerfLogEvery  = 30;
        private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        private int _blackFrameCount = 0;

        private CaptureResult CaptureInternal(int width, int height)
        {
            IntPtr surface = IntPtr.Zero;

            try
            {
                // ── Phase A: take surface + map ──────────────────────────────
                // _screenshotHandle is initialized once and reused every frame.
                // If it's null (first call or after a failure), initialize now.
                _sw.Restart();

                if (_screenshotHandle == IntPtr.Zero)
                {
                    _screenshotHandle = Screenshot_Initialize(width, height);
                    if (_screenshotHandle == IntPtr.Zero)
                        return CaptureResult.CreateFailure("[EflScreenshot] screenshot_initialize returned null");
                    Helper.Log.Write(Helper.eLogType.Info, "[EflScreenshot] Screenshot handle created");
                }

                surface = Screenshot_TakeTbmSurface(_screenshotHandle);
                if (surface == IntPtr.Zero)
                {
                    // Handle may be stale — invalidate so next call re-inits
                    try { Screenshot_Deinitialize(_screenshotHandle); } catch { }
                    _screenshotHandle = IntPtr.Zero;
                    return CaptureResult.CreateFailure("[EflScreenshot] take_tbm_surface returned null (handle reset)");
                }

                int mapRet = Tbm_SurfaceMap(surface, TBM_SURF_OPTION_READ, out TbmSurfaceInfo info);
                if (mapRet != 0)
                {
                    Tbm_SurfaceDestroy(surface);
                    surface = IntPtr.Zero;
                    return CaptureResult.CreateFailure($"[EflScreenshot] tbm_surface_map failed: {mapRet}");
                }

                long t1 = _sw.ElapsedMilliseconds; // end of capture phase (take + map only)

                int actualW    = (int)info.width;
                int actualH    = (int)info.height;
                int stride     = (int)info.plane0.stride;
                int bpp        = (int)info.bpp;
                int bytesPerPx = bpp / 8;

                if (info.plane0.ptr == IntPtr.Zero || info.plane0.size == 0)
                {
                    Tbm_SurfaceUnmap(surface);
                    Tbm_SurfaceDestroy(surface);
                    surface = IntPtr.Zero;
                    return CaptureResult.CreateFailure("[EflScreenshot] plane0.ptr is null after map");
                }

                if (!_firstFrameLogged)
                {
                    Helper.Log.Write(Helper.eLogType.Info,
                        $"[EflScreenshot] First frame: {actualW}x{actualH} fmt=0x{info.format:X8} " +
                        $"bpp={bpp} stride={stride} planes={info.num_planes}");
                    _firstFrameLogged = true;
                }

                // ── Phase B: pixel copy ──────────────────────────────────────
                int rowBytes = actualW * bytesPerPx;
                byte[] raw   = new byte[actualW * actualH * bytesPerPx];

                for (int row = 0; row < actualH; row++)
                {
                    IntPtr rowPtr = info.plane0.ptr + row * stride;
                    Marshal.Copy(rowPtr, raw, row * rowBytes, rowBytes);
                }

                long t2 = _sw.ElapsedMilliseconds; // end of copy

                // ── Phase C: unmap + destroy surface (NOT deinitialize handle) ──
                Tbm_SurfaceUnmap(surface);
                Tbm_SurfaceDestroy(surface);
                surface = IntPtr.Zero;

                long t3 = _sw.ElapsedMilliseconds; // end of cleanup

                // ── Phase D: NV12 conversion ─────────────────────────────────
                // XR24/XRGB8888 in memory: [B, G, R, X] — treat as BGR
                ConvertToNV12(raw, actualW, actualH, bytesPerPx, isBGRInMemory: true,
                    out byte[] yData, out byte[] uvData);

                long t4 = _sw.ElapsedMilliseconds; // end of convert

                // ── Periodic perf log ────────────────────────────────────────
                _perfCaptureMs += t1;
                _perfCopyMs    += t2 - t1;
                _perfCleanupMs += t3 - t2;
                _perfConvertMs += t4 - t3;
                _perfFrameCount++;

                if (_perfFrameCount >= PerfLogEvery)
                {
                    long f = _perfFrameCount;
                    Helper.Log.Write(Helper.eLogType.Info,
                        $"[EflScreenshot] Perf avg/{f}f: " +
                        $"capture={_perfCaptureMs / f}ms  " +
                        $"copy={_perfCopyMs / f}ms  " +
                        $"cleanup={_perfCleanupMs / f}ms  " +
                        $"convert={_perfConvertMs / f}ms  " +
                        $"total={(_perfCaptureMs + _perfCopyMs + _perfCleanupMs + _perfConvertMs) / f}ms");
                    _perfFrameCount = 0;
                    _perfCaptureMs = _perfCopyMs = _perfCleanupMs = _perfConvertMs = 0;
                }

                // ── Black frame detection ────────────────────────────────────
                // On Samsung Tizen, video runs on a separate HW overlay plane that
                // the EFL compositor cannot see. YouTube/Plex/IPTV video returns a
                // fully black compositor frame. Detect this and report as failure so
                // HyperionClient can fall back to PixelSampling which reads below the
                // compositor (display controller level, sees all planes).
                if (IsBlackFrame(yData, threshold: 20))
                {
                    _blackFrameCount++;
                    if (_blackFrameCount == 1 || _blackFrameCount % 30 == 0)
                        Helper.Log.Write(Helper.eLogType.Warning,
                            $"[EflScreenshot] Black frame #{_blackFrameCount} detected — " +
                            "video may be on HW overlay plane (EFL cannot capture it)");
                    return CaptureResult.CreateFailure("[EflScreenshot] Black frame — HW video plane not accessible");
                }

                _blackFrameCount = 0;
                return CaptureResult.CreateSuccess(yData, uvData, actualW, actualH);
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"[EflScreenshot] Exception in CaptureInternal: {ex.GetType().Name}: {ex.Message}");
                return CaptureResult.CreateFailure($"[EflScreenshot] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (surface != IntPtr.Zero)
                {
                    try { Tbm_SurfaceUnmap(surface); } catch { }
                    try { Tbm_SurfaceDestroy(surface); } catch { }
                }
                // _screenshotHandle intentionally NOT released here — kept alive for next frame
            }
        }

        /// <summary>
        /// Converts packed [B,G,R,?] or [A,R,G,B] byte array to NV12 (Y + interleaved UV).
        /// isBGRInMemory=true  → src[i]=B, src[i+1]=G, src[i+2]=R
        /// isBGRInMemory=false → src[i]=A, src[i+1]=R, src[i+2]=G, src[i+3]=B
        /// </summary>
        private static void ConvertToNV12(
            byte[] src, int width, int height, int bytesPerPx,
            bool isBGRInMemory,
            out byte[] yData, out byte[] uvData)
        {
            yData  = new byte[width * height];
            uvData = new byte[width * height / 2];

            int uvIdx = 0;

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int i = (row * width + col) * bytesPerPx;

                    byte r, g, b;
                    if (isBGRInMemory)
                    {
                        b = src[i];
                        g = src[i + 1];
                        r = src[i + 2];
                    }
                    else
                    {
                        // ARGB: A R G B
                        r = src[i + 1];
                        g = src[i + 2];
                        b = src[i + 3];
                    }

                    // BT.601 full-range Y
                    int Y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                    yData[row * width + col] = (byte)Clamp(Y, 16, 235);

                    // NV12 UV — subsample 2×2
                    if ((row & 1) == 0 && (col & 1) == 0)
                    {
                        int U = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                        int V = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                        uvData[uvIdx++] = (byte)Clamp(U, 16, 240);
                        uvData[uvIdx++] = (byte)Clamp(V, 16, 240);
                    }
                }
            }
        }

        private static int Clamp(int v, int lo, int hi)
            => v < lo ? lo : v > hi ? hi : v;

        /// <summary>
        /// Returns true if the NV12 Y plane is almost entirely black.
        /// Samples every 16th pixel for speed (480×270 → ~8100 samples checked).
        /// </summary>
        private static bool IsBlackFrame(byte[] yData, int threshold)
        {
            long sum = 0;
            int step = 16;
            int count = 0;
            for (int i = 0; i < yData.Length; i += step)
            {
                sum += yData[i];
                count++;
            }
            int avg = count > 0 ? (int)(sum / count) : 0;
            return avg < threshold;
        }
    }
}
