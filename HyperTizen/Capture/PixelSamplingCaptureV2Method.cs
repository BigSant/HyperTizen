using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Tizen.System;
using Tizen.NUI;
using System;

using System;
using System.Runtime.InteropServices;

namespace HyperTizen.Capture
{
    public static class NativeMethods
    {
        // This library is present on almost all Tizen TV firmwares
        private const string LibEflUtil = "libcapi-ui-efl-util.so.0";
        private const string LibTbm = "libtbm.so.1";

        [DllImport(LibEflUtil, EntryPoint = "efl_util_screenshot_initialize")]
        public static extern int ScreenshotInitialize(int width, int height);

        [DllImport(LibEflUtil, EntryPoint = "efl_util_screenshot_take_tbm_surface")]
        public static extern IntPtr TakeScreenshotSurface(IntPtr screenshotHandle);

        [DllImport(LibEflUtil, EntryPoint = "efl_util_screenshot_deinitialize")]
        public static extern int ScreenshotDeinitialize(IntPtr screenshotHandle);

        // TBM Surface Mapping to read bytes
        [DllImport(LibTbm, EntryPoint = "tbm_surface_map")]
        public static extern int TbmSurfaceMap(IntPtr surface, int opt, out TbmSurfaceInfo info);

        [DllImport(LibTbm, EntryPoint = "tbm_surface_unmap")]
        public static extern int TbmSurfaceUnmap(IntPtr surface);

        [StructLayout(LayoutKind.Sequential)]
        public struct TbmSurfaceInfo
        {
            public int width;
            public int height;
            public int format;
            public int bpp;
            public int size;
            public int num_planes;
            public IntPtr planes; // Pointer to the pixel buffer
        }
    }


    /// <summary>
    /// Pixel sampling capture method using libvideoenhance.so
    /// Samples individual pixels from screen edges for ambient lighting
    /// Adapted from original HyperTizen Capturer.cs to use NV12/FlatBuffers format
    /// </summary>
    public class PixelSamplingCaptureV2Method : ICaptureMethod
    {
        /// Thread.Sleep has ~20ms granularity on Tizen Mono. Spin via Stopwatch for true ms precision.
        private static readonly long TicksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000;
        private static void PreciseWaitMs(int milliseconds)
        {
            if (milliseconds <= 0) return;
            long targetTicks = milliseconds * TicksPerMs;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedTicks < targetTicks) { }
        }

        private bool _isInitialized = false;
        private Condition _condition;

        // Temporal smoothing (EMA) to reduce color noise between frames
        private Color[] _previousColors = null;
        private const float SmoothingAlpha = 0.5f; // 0.5 = equal blend of new+old

        // ── Temporal interleaving: sample 1/N points per frame, merge with previous ──
        // groups≥2 → faster effective FPS at cost of N-frame refresh per point.
        private int _interleaveGroups = 5;
        private int _currentInterleaveGroup = 0;
        private Color[] _mergedInterleavedColors = null;

        public int InterleaveGroups
        {
            get { return _interleaveGroups; }
            set { _interleaveGroups = Math.Max(1, value); ResetInterleaveState(); }
        }

        /// <summary>
        /// When true, overlays white markers on the NV12 output at every sample point's
        /// mapped position so it's visually obvious where colors were sampled from.
        /// </summary>
        public bool DebugMode { get; set; } = false;

        private void ResetInterleaveState()
        {
            _mergedInterleavedColors = null;
            _currentInterleaveGroup = 0;
        }

        // ── Adaptive edge inset: moves sampling inward when edge is black (letterbox/pillarbox) ──
        private float _topInset = 0.02f;
        private float _bottomInset = 0.02f;
        private float _leftInset = 0.02f;
        private float _rightInset = 0.02f;
        private int _topBlackCount = 0;
        private int _bottomBlackCount = 0;
        private int _leftBlackCount = 0;
        private int _rightBlackCount = 0;
        private const int BlackThresholdFrames = 75;  // ~3s at 25 FPS before moving inward
        private const int BlackColorThreshold = 5;    // 10-bit value below this = black (very strict — only true black, not dark content)
        private static readonly float[] InsetSteps = { 0.02f, 0.08f, 0.15f, 0.25f, 0.35f };
        private bool _edgesAdapted = false;
        private int _probeResetCounter = 0;
        private const int ProbeResetInterval = 250;  // ~10s at 25 FPS — probe original position
        private int _frameCounter = 0;                // total frames since init, used to skip warmup

        // Track which API variant and library path works
        private string _workingVariant = null; // T6, T7, T9A, T9B, T9C, T9 (ppi_ve_*)
        private string _workingLibPath = null; // SO, SO0 (only .so and .so.0 exist on Tizen 9)

        // Pre-calculated pixel coordinates (calculated once during initialization)
        private struct PixelCoordinate
        {
            public int X;
            public int Y;
        }
        private PixelCoordinate[] _pixelCoordinates = null;

        // 16-point sampling grid (normalized coordinates 0.0-1.0)
        // 4 points per edge for better color representation
        // With synchronized batching, we achieve 40 FPS even with 16 points
        // private CapturePoint[] _capturedPoints = new CapturePoint[] {
        //     // Top edge (4 points) - left to right
        //     new CapturePoint(0.3, 0.05),
        //     new CapturePoint(0.6, 0.05),
        //     new CapturePoint(0.9, 0.05),
        //     // new CapturePoint(0.80, 0.05),
        //     // Right edge (4 points) - top to bottom
        //     new CapturePoint(0.95, 0.3),
        //     new CapturePoint(0.95, 0.6),
        //     new CapturePoint(0.95, 0.9),
        //     // new CapturePoint(0.95, 0.80),
        //     // Bottom edge (4 points) - right to left
        //     // new CapturePoint(0.80, 0.95),
        //     // new CapturePoint(0.60, 0.95),
        //     // new CapturePoint(0.40, 0.95),
        //     // new CapturePoint(0.20, 0.95),
        //     // // Left edge (4 points) - bottom to top
        //     // new CapturePoint(0.05, 0.80),
        //     // new CapturePoint(0.05, 0.60),
        //     // new CapturePoint(0.05, 0.40),
        //     // new CapturePoint(0.05, 0.20)
        // };

        private static double minPos = 0.02; // 2% from edge — close to actual screen border
        private static double maxPos = 0.98; // 98% from opposite edge
        
        // 5 points per horizontal edge: step = 3840*0.96/4 = 921.6 → 2%, 26%, 50%, 74%, 98%
        // 5 vertical iterations, 3 unique per side (corners shared with H edges)
        // Total: 5+5+3+3 = 16 points → 8 batches × 20ms = 160ms (~6 FPS)
        // Symmetric: 5 per edge (including shared corners)
        private static double _captureStepHorizontal = 614.4;

        private static double _captureStepVertical = 518.4;

        private static int _capturePointsCountHorizontal = 0;

        private static int _capturePointsCountVertical = 0;

        private CapturePoint[] _capturedPoints = GenerateCaptureGrid(_captureStepHorizontal, _captureStepVertical);


        public string Name => "Pixel Sampling";
        public CaptureMethodType Type => CaptureMethodType.PixelSampling;

        #region P/Invoke Declarations

        // ===== Tizen 6 API (cs_ve_* prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T6_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T6_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T6_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T6_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T6_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T6_SO0(int index, out Color color);

        // ===== Tizen 7 API (ve_* prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T7_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T7_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T7_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T7_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T7_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T7_SO0(int index, out Color color);

        // ===== Tizen 9+ API variant A (tizen_ve_* prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9A_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9A_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9A_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9A_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9A_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9A_SO0(int index, out Color color);

        // ===== Tizen 9+ API variant B (samsung_ve_* prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9B_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9B_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9B_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9B_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9B_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9B_SO0(int index, out Color color);

        // ===== Tizen 9+ API variant C (no prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9C_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "set_rgb_measure_position")]
        private static extern int MeasurePosition_T9C_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9C_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9C_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "set_rgb_measure_position")]
        private static extern int MeasurePosition_T9C_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9C_SO0(int index, out Color color);

        // ===== Tizen 9 API (ppi_ve_* prefix) - CONFIRMED via analysis =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ppi_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ppi_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ppi_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9_SO0(int index, out Color color);

        #endregion

        #region Native Structs

        /// <summary>
        /// Color struct for 10-bit RGB values (0-1023).
        /// Padded to 64 bytes because some Tizen firmwares' native get_rgb_measure_pixel
        /// writes more than 12 bytes through the out-pointer, causing stack corruption
        /// that silently mutates adjacent local variables. Extra fields absorb the overrun.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Color
        {
            public int R;
            public int G;
            public int B;
            public int Pad0, Pad1, Pad2, Pad3, Pad4, Pad5, Pad6, Pad7;
            public int Pad8, Pad9, Pad10, Pad11, Pad12;
        }

        /// <summary>
        /// Condition struct containing screen parameters and sampling configuration
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Condition
        {
            public int ScreenCapturePoints;  // Max number of points that can be sampled simultaneously
            public int PixelDensityX;         // Pixel density in X direction
            public int PixelDensityY;         // Pixel density in Y direction
            public int SleepMS;               // Milliseconds to sleep between position set and pixel read
            public int Width;                 // Screen width in pixels
            public int Height;                // Screen height in pixels
        }

        /// <summary>
        /// Capture point with normalized coordinates (0.0-1.0)
        /// </summary>
        public struct CapturePoint
        {
            public CapturePoint(double x, double y)
            {
                this.X = x;
                this.Y = y;
            }

            public double X;
            public double Y;
        }

        #endregion

        private static CapturePoint[] GenerateCaptureGrid(double pixelStepHorizontal, double pixelStepVertical)
        {
            // Reset counters — static fields must not accumulate across multiple instances.
            _capturePointsCountHorizontal = 0;
            _capturePointsCountVertical = 0;

            var points = new List<CapturePoint>();

            // Assume 3840x2160 for now; at runtime, will use actual screen dimensions from Condition
            int screenWidth = 3840;
            int screenHeight = 2160;

            double xPos, yPos;

            // Horizontal edge (top)
            for (xPos = minPos; xPos <= maxPos; xPos += pixelStepHorizontal / screenWidth)
            {
                points.Add(new CapturePoint(Math.Min(maxPos, xPos), minPos));
                _capturePointsCountHorizontal++;
            }

            // Horizontal edge (bottom)
            for (xPos = minPos; xPos <= maxPos; xPos += pixelStepHorizontal / screenWidth)
            {
                points.Add(new CapturePoint(Math.Min(maxPos, xPos), maxPos));
            }

            // Vertical edge (left)
            for (yPos = minPos; yPos <= maxPos; yPos += pixelStepVertical / screenHeight)
            {
                CapturePoint newYPoint = new CapturePoint(minPos, Math.Min(maxPos, yPos));
                if (!points.Contains(newYPoint))
                {
                    points.Add(newYPoint);
                    _capturePointsCountVertical++;
                }
            }

            // Vertical edge (right)
            for (yPos = minPos; yPos <= maxPos; yPos += pixelStepVertical / screenHeight)
            {
                CapturePoint newYPoint = new CapturePoint(maxPos, Math.Min(maxPos, yPos));
                if (!points.Contains(newYPoint))
                {
                    points.Add(newYPoint);
                }
            }

            Helper.Log.Write(Helper.eLogType.Info, $"PixelSamplingV2: Generated {points.Count} grid capture points (horizontal: ({pixelStepHorizontal}px, vertical: {pixelStepVertical}px step), inset {minPos}–{maxPos} on 4K)");

            CapturePoint[] list = points.ToArray();
            string json = ConvertPointsToJson(list);

            Helper.Log.Write(Helper.eLogType.Info, $"PixelSamplingV2: Generated points JSON {json}");

            return points.ToArray();
        }

        private static string ConvertPointsToJson(CapturePoint[] points)
        {
            if (points == null || points.Length == 0)
            {
                return "[]";
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("[");

            for (int i = 0; i < points.Length; i++)
            {
                sb.Append("{\"x\":");
                sb.Append(points[i].X.ToString("F4"));
                sb.Append(",\"y\":");
                sb.Append(points[i].Y.ToString("F4"));
                sb.Append("}");

                if (i < points.Length - 1)
                {
                    sb.Append(",");
                }
            }

            sb.Append("]");
            return sb.ToString();
        }


        private static string ConvertPixelCoordinatesToJson(PixelCoordinate[] pixelCoordinates)
        {
            if (pixelCoordinates == null || pixelCoordinates.Length == 0)
            {
                return "[]";
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("[");

            for (int i = 0; i < pixelCoordinates.Length; i++)
            {
                sb.Append("{\"x\":");
                sb.Append(pixelCoordinates[i].X.ToString("F4"));
                sb.Append(",\"y\":");
                sb.Append(pixelCoordinates[i].Y.ToString("F4"));
                sb.Append("}");

                if (i < pixelCoordinates.Length - 1)
                {
                    sb.Append(",");
                }
            }

            sb.Append("]");
            return sb.ToString();
        }

        private string ConvertColorsToJson(Color[] colors)
        {
            if (colors == null || colors.Length == 0)
            {
                return "[]";
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("[");

            for (int i = 0; i < colors.Length; i++)
            {
                sb.Append("{\"r\":");
                sb.Append(colors[i].R);
                sb.Append(",\"g\":");
                sb.Append(colors[i].G);
                sb.Append(",\"b\":");
                sb.Append(colors[i].B);
                sb.Append("}");

                if (i < colors.Length - 1)
                {
                    sb.Append(",");
                }
            }

            sb.Append("]");
            return sb.ToString();
        }

        public void CapturePixelAt(int x, int y)
        {
            try
            {
                // 1. Initialize Screenshot (Full HD usually safest)
                int ret = NativeMethods.ScreenshotInitialize(1920, 1080);
                // Note: You ideally need the handle returned by init, but some versions
                // use a void/global context. Check specific API version.

                // 2. Request Surface
                IntPtr surface = NativeMethods.TakeScreenshotSurface(IntPtr.Zero);

                if (surface != IntPtr.Zero)
                {
                    NativeMethods.TbmSurfaceInfo info;
                    // 3. Map the memory (1 = TBM_SURF_OPTION_READ)
                    if (NativeMethods.TbmSurfaceMap(surface, 1, out info) == 0)
                    {
                        // 4. Calculate byte offset
                        // Assumes ARGB8888 (4 bytes per pixel)
                        int stride = info.width * 4;
                        int offset = (y * stride) + (x * 4);

                        // Read 4 bytes from unmanaged memory
                        byte b = Marshal.ReadByte(info.planes, offset);
                        byte g = Marshal.ReadByte(info.planes, offset + 1);
                        byte r = Marshal.ReadByte(info.planes, offset + 2);

                        Helper.Log.Write(Helper.eLogType.Debug, $"CAPTURE Pixel at {x},{y} - R:{r} G:{g} B:{b}");

                        NativeMethods.TbmSurfaceUnmap(surface);
                    }
                }
                else
                {
                    Helper.Log.Write(Helper.eLogType.Debug, $"CAPTURE Permission Denied or Surface Null. (Missing http://tizen.org/privilege/screenshot?)");
                }

                NativeMethods.ScreenshotDeinitialize(IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug, $"CAPTURE Interop Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if pixel sampling library is available
        /// </summary>
        public bool IsAvailable()
        {
            // libvideoenhance.so (or .so.0) must exist for any pixel sampling variant to work
            bool soExists  = System.IO.File.Exists("/usr/lib/libvideoenhance.so");
            bool so0Exists = System.IO.File.Exists("/usr/lib/libvideoenhance.so.0");

            Helper.Log.Write(Helper.eLogType.Info,
                $"PixelSamplingV2: libvideoenhance.so={soExists}, libvideoenhance.so.0={so0Exists}");

            return soExists || so0Exists;
        }

        /// <summary>
        /// Test pixel sampling by attempting to get screen condition
        /// </summary>
        public bool Test()
        {
            if (!IsAvailable())
                return false;

            try
            {
                Helper.Log.Write(Helper.eLogType.Info, "PixelSamplingV2: Testing capture...");

                bool success = GetCondition();

                if (success)
                {
                    // Pre-calculate coordinates during test
                    PreCalculateCoordinates();

                    Helper.Log.Write(Helper.eLogType.Info,
                        $"PixelSampling Test: SUCCESS - Screen: {_condition.Width}x{_condition.Height}, " +
                        $"Points: {_condition.ScreenCapturePoints}, Sleep: {_condition.SleepMS}ms");
                    _isInitialized = true;
                    return true;
                }
                else
                {
                    Helper.Log.Write(Helper.eLogType.Warning, "PixelSampling Test: GetCondition failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"PixelSampling Test exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get screen condition parameters from VideoEnhance library
        /// Tests ALL combinations of API variants and library paths systematically
        /// </summary>
        private bool GetCondition()
        {
            Helper.Log.Write(Helper.eLogType.Info,
                "PixelSamplingV2: Testing ALL combinations of entry points and library paths...");

            // Test Tizen 6 (cs_ve_*) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T6_SO(out _condition),
                (idx, x, y) => MeasurePosition_T6_SO(idx, x, y),
                MeasurePixel_T6_SO,
                "T6", "SO", "cs_ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T6_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T6_SO0(idx, x, y),
                MeasurePixel_T6_SO0,
                "T6", "SO0", "cs_ve_*", ".so.0")) return true;

            // Test Tizen 7 (ve_*) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T7_SO(out _condition),
                (idx, x, y) => MeasurePosition_T7_SO(idx, x, y),
                MeasurePixel_T7_SO,
                "T7", "SO", "ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T7_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T7_SO0(idx, x, y),
                MeasurePixel_T7_SO0,
                "T7", "SO0", "ve_*", ".so.0")) return true;

            // Test Tizen 9+ variant A (tizen_ve_*) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T9A_SO(out _condition),
                (idx, x, y) => MeasurePosition_T9A_SO(idx, x, y),
                MeasurePixel_T9A_SO,
                "T9A", "SO", "tizen_ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T9A_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T9A_SO0(idx, x, y),
                MeasurePixel_T9A_SO0,
                "T9A", "SO0", "tizen_ve_*", ".so.0")) return true;

            // Test Tizen 9+ variant B (samsung_ve_*) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T9B_SO(out _condition),
                (idx, x, y) => MeasurePosition_T9B_SO(idx, x, y),
                MeasurePixel_T9B_SO,
                "T9B", "SO", "samsung_ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T9B_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T9B_SO0(idx, x, y),
                MeasurePixel_T9B_SO0,
                "T9B", "SO0", "samsung_ve_*", ".so.0")) return true;

            // Test Tizen 9+ variant C (no prefix) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T9C_SO(out _condition),
                (idx, x, y) => MeasurePosition_T9C_SO(idx, x, y),
                MeasurePixel_T9C_SO,
                "T9C", "SO", "no prefix", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T9C_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T9C_SO0(idx, x, y),
                MeasurePixel_T9C_SO0,
                "T9C", "SO0", "no prefix", ".so.0")) return true;

            // Test Tizen 9 actual API (ppi_ve_*) - CONFIRMED via library analysis
            if (TryVariant(
                () => MeasureCondition_T9_SO(out _condition),
                (idx, x, y) => MeasurePosition_T9_SO(idx, x, y),
                MeasurePixel_T9_SO,
                "T9", "SO", "ppi_ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T9_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T9_SO0(idx, x, y),
                MeasurePixel_T9_SO0,
                "T9", "SO0", "ppi_ve_*", ".so.0")) return true;

            // All combinations failed
            Helper.Log.Write(Helper.eLogType.Error,
                "PixelSamplingV2: ALL 12 combinations failed (6 entry point variants × 2 library paths)");
            Helper.Log.Write(Helper.eLogType.Error,
                "PixelSamplingV2: libvideoenhance.so does not support RGB pixel sampling on this Tizen version");
            return false;
        }

        /// <summary>
        /// Try a specific API variant + library path combination
        /// Tests ALL 3 entry points to ensure complete API surface exists
        /// </summary>
        private delegate int MeasurePixelDelegate(int index, out Color color);

        private bool TryVariant(
            Func<int> conditionFunc,
            Func<int, int, int, int> positionFunc,
            MeasurePixelDelegate pixelFunc,
            string variant,
            string libPath,
            string entryPrefix,
            string libSuffix)
        {
            try
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSamplingV2: Testing {variant} ({entryPrefix}) with libvideoenhance{libSuffix}");

                // Test 1: MeasureCondition
                int conditionResult = conditionFunc();
                if (conditionResult < 0)
                {
                    Helper.Log.Write(Helper.eLogType.Debug,
                        $"PixelSamplingV2: {variant}/{libPath} condition returned error {conditionResult}");
                    return false;
                }

                // Test 2: MeasurePosition (validate entry point exists with dummy coordinates)
                int positionResult = positionFunc(0, 0, 0);
                // Position may fail if called before proper setup, but entry point should exist
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSamplingV2: {variant}/{libPath} position entry point exists (result: {positionResult})");

                // Test 3: MeasurePixel (validate entry point exists)
                Color dummyColor;
                int pixelResult = pixelFunc(0, out dummyColor);
                // Pixel may fail if no position set yet, but entry point should exist
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSamplingV2: {variant}/{libPath} pixel entry point exists (result: {pixelResult})");

                // Success - all three entry points exist and condition succeeded
                _workingVariant = variant;
                _workingLibPath = libPath;
                Helper.Log.Write(Helper.eLogType.Info,
                    $"PixelSamplingV2: ✓ SUCCESS - All 3 entry points validated for {variant} ({entryPrefix}) with libvideoenhance{libSuffix}");
                Helper.Log.Write(Helper.eLogType.Debug, $"PixelSamplingV2: Condition result: {conditionResult}");
                LogConditionDetails();
                return true;
            }
            catch (EntryPointNotFoundException ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSamplingV2: {variant}/{libPath} entry point not found: {ex.Message}");
                return false;
            }
            catch (DllNotFoundException ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSamplingV2: {variant}/{libPath} library file not found: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSamplingV2: {variant}/{libPath} exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Helper method to log condition details
        /// </summary>
        private void LogConditionDetails()
        {
            Helper.Log.Write(Helper.eLogType.Info,
                $"PixelSamplingV2: Condition - Width: {_condition.Width}, Height: {_condition.Height}, " +
                $"Points: {_condition.ScreenCapturePoints}, PixelDensity: {_condition.PixelDensityX}x{_condition.PixelDensityY}, " +
                $"Sleep: {_condition.SleepMS}ms");
        }

        /// <summary>
        /// Call correct MeasurePosition variant based on working combination
        /// </summary>
        private int CallMeasurePosition(int index, int x, int y)
        {
            string key = $"{_workingVariant}_{_workingLibPath}";
            switch (key)
            {
                case "T6_SO": return MeasurePosition_T6_SO(index, x, y);
                case "T6_SO0": return MeasurePosition_T6_SO0(index, x, y);
                case "T7_SO": return MeasurePosition_T7_SO(index, x, y);
                case "T7_SO0": return MeasurePosition_T7_SO0(index, x, y);
                case "T9A_SO": return MeasurePosition_T9A_SO(index, x, y);
                case "T9A_SO0": return MeasurePosition_T9A_SO0(index, x, y);
                case "T9B_SO": return MeasurePosition_T9B_SO(index, x, y);
                case "T9B_SO0": return MeasurePosition_T9B_SO0(index, x, y);
                case "T9C_SO": return MeasurePosition_T9C_SO(index, x, y);
                case "T9C_SO0": return MeasurePosition_T9C_SO0(index, x, y);
                case "T9_SO": return MeasurePosition_T9_SO(index, x, y);
                case "T9_SO0": return MeasurePosition_T9_SO0(index, x, y);
                default:
                    throw new InvalidOperationException($"Unknown variant: {key}");
            }
        }

        /// <summary>
        /// Call correct MeasurePixel variant based on working combination
        /// </summary>
        private int CallMeasurePixel(int index, out Color color)
        {
            string key = $"{_workingVariant}_{_workingLibPath}";
            switch (key)
            {
                case "T6_SO": return MeasurePixel_T6_SO(index, out color);
                case "T6_SO0": return MeasurePixel_T6_SO0(index, out color);
                case "T7_SO": return MeasurePixel_T7_SO(index, out color);
                case "T7_SO0": return MeasurePixel_T7_SO0(index, out color);
                case "T9A_SO": return MeasurePixel_T9A_SO(index, out color);
                case "T9A_SO0": return MeasurePixel_T9A_SO0(index, out color);
                case "T9B_SO": return MeasurePixel_T9B_SO(index, out color);
                case "T9B_SO0": return MeasurePixel_T9B_SO0(index, out color);
                case "T9C_SO": return MeasurePixel_T9C_SO(index, out color);
                case "T9C_SO0": return MeasurePixel_T9C_SO0(index, out color);
                case "T9_SO": return MeasurePixel_T9_SO(index, out color);
                case "T9_SO0": return MeasurePixel_T9_SO0(index, out color);
                default:
                    throw new InvalidOperationException($"Unknown variant: {key}");
            }
        }

        /// <summary>
        /// Pre-calculate pixel coordinates from normalized positions
        /// Called once during initialization to avoid repeated calculations
        /// </summary>
        private void PreCalculateCoordinates()
        {
            _pixelCoordinates = new PixelCoordinate[_capturedPoints.Length];

            for (int i = 0; i < _capturedPoints.Length; i++)
            {
                // Convert normalized coordinates to pixel coordinates
                int x = (int)(_capturedPoints[i].X * (double)_condition.Width) - _condition.PixelDensityX / 2;
                int y = (int)(_capturedPoints[i].Y * (double)_condition.Height) - _condition.PixelDensityY / 2;

                // Clamp coordinates to valid screen bounds
                x = (x >= _condition.Width - _condition.PixelDensityX) ?
                    _condition.Width - (_condition.PixelDensityX + 1) : x;
                y = (y >= _condition.Height - _condition.PixelDensityY) ?
                    (_condition.Height - _condition.PixelDensityY + 1) : y;

                // Ensure coordinates are not negative
                x = Math.Max(0, x);
                y = Math.Max(0, y);

                _pixelCoordinates[i].X = x;
                _pixelCoordinates[i].Y = y;
            }

            Helper.Log.Write(Helper.eLogType.Info,
                $"PixelSamplingV2: Pre-calculated {_pixelCoordinates.Length} pixel coordinates");

            string json = ConvertPixelCoordinatesToJson(_pixelCoordinates);
            Helper.Log.Write(Helper.eLogType.Info, $"PixelSamplingV2: Generated pixel coordinates JSON {json}");
        }

        /// <summary>
        /// Sample pixel colors from predefined screen positions.
        /// Processes all points in batches of ScreenCapturePoints with 20ms sleep between batches.
        /// </summary>
        /// <summary>
        /// Temporal interleaving: samples only 1/N of points per call (stride-based
        /// groups i%N==group), merges with last-known values for other groups, returns
        /// a full-size array. Advances current group after each call.
        /// </summary>
        private Color[] GetColorsInterleaved()
        {
            int total = _capturedPoints.Length;
            int groups = Math.Max(1, _interleaveGroups);

            if (_mergedInterleavedColors == null || _mergedInterleavedColors.Length != total)
            {
                _mergedInterleavedColors = new Color[total];
                _currentInterleaveGroup = 0;
            }

            int group = _currentInterleaveGroup % groups;

            List<int> indices = new List<int>(total / groups + 1);
            for (int i = 0; i < total; i++)
            {
                if (i % groups == group) indices.Add(i);
            }

            for (int k = 0; k < indices.Count; k += 2)
            {
                int idx0 = indices[k];
                int idx1 = (k + 1 < indices.Count) ? indices[k + 1] : -1;

                CallMeasurePosition(0, _pixelCoordinates[idx0].X, _pixelCoordinates[idx0].Y);
                if (idx1 >= 0)
                    CallMeasurePosition(1, _pixelCoordinates[idx1].X, _pixelCoordinates[idx1].Y);

                Thread.Sleep(20);

                Color c0;
                int r0 = CallMeasurePixel(0, out c0);
                if (r0 < 0) { c0.R = 0; c0.G = 0; c0.B = 0; }
                else { c0.R = Math.Max(0, Math.Min(1023, c0.R)); c0.G = Math.Max(0, Math.Min(1023, c0.G)); c0.B = Math.Max(0, Math.Min(1023, c0.B)); }
                _mergedInterleavedColors[idx0] = c0;

                if (idx1 >= 0)
                {
                    Color c1;
                    int r1 = CallMeasurePixel(1, out c1);
                    if (r1 < 0) { c1.R = 0; c1.G = 0; c1.B = 0; }
                    else { c1.R = Math.Max(0, Math.Min(1023, c1.R)); c1.G = Math.Max(0, Math.Min(1023, c1.G)); c1.B = Math.Max(0, Math.Min(1023, c1.B)); }
                    _mergedInterleavedColors[idx1] = c1;
                }
            }

            _currentInterleaveGroup = (_currentInterleaveGroup + 1) % groups;

            Color[] result = new Color[total];
            Array.Copy(_mergedInterleavedColors, result, total);
            return result;
        }

        private Color[] GetColors()
        {
            Color[] colorData = new Color[_capturedPoints.Length];
            int total = _capturedPoints.Length;

            for (int i = 0; i < total; i += 2)
            {
                CallMeasurePosition(0, _pixelCoordinates[i].X, _pixelCoordinates[i].Y);
                bool hasSlot1 = (i + 1 < total);
                if (hasSlot1)
                    CallMeasurePosition(1, _pixelCoordinates[i + 1].X, _pixelCoordinates[i + 1].Y);

                Thread.Sleep(20);

                Color c0;
                int r0 = CallMeasurePixel(0, out c0);
                if (r0 < 0) { c0.R = 0; c0.G = 0; c0.B = 0; }
                else { c0.R = Math.Max(0, Math.Min(1023, c0.R)); c0.G = Math.Max(0, Math.Min(1023, c0.G)); c0.B = Math.Max(0, Math.Min(1023, c0.B)); }
                colorData[i] = c0;

                if (hasSlot1)
                {
                    Color c1;
                    int r1 = CallMeasurePixel(1, out c1);
                    if (r1 < 0) { c1.R = 0; c1.G = 0; c1.B = 0; }
                    else { c1.R = Math.Max(0, Math.Min(1023, c1.R)); c1.G = Math.Max(0, Math.Min(1023, c1.G)); c1.B = Math.Max(0, Math.Min(1023, c1.B)); }
                    colorData[i + 1] = c1;
                }
            }

            return colorData;
        }

        /// <summary>
        /// Convert sampled pixel colors to NV12 format using BT.2020 color space
        /// Creates a virtual 64x48 image with sampled colors mapped to screen edges
        /// Uses BT.2020 coefficients for HDR10+ compatibility
        /// </summary>
        /// <summary>
        /// Interpolate color along an edge at normalized position t (0.0–1.0)
        /// </summary>
        private void InterpolateEdge(Color[] colors, int offset, int count, float t, out byte r, out byte g, out byte b)
        {
            if (count <= 1)
            {
                r = ScaleTo8Bit(colors[offset].R);
                g = ScaleTo8Bit(colors[offset].G);
                b = ScaleTo8Bit(colors[offset].B);
                return;
            }
            float pos = t * (count - 1);
            int idx = Math.Min((int)pos, count - 2);
            float frac = pos - idx;
            r = (byte)((1 - frac) * ScaleTo8Bit(colors[offset + idx].R) + frac * ScaleTo8Bit(colors[offset + idx + 1].R));
            g = (byte)((1 - frac) * ScaleTo8Bit(colors[offset + idx].G) + frac * ScaleTo8Bit(colors[offset + idx + 1].G));
            b = (byte)((1 - frac) * ScaleTo8Bit(colors[offset + idx].B) + frac * ScaleTo8Bit(colors[offset + idx + 1].B));
        }

        private (byte[] yData, byte[] uvData) ConvertColorsToNV12(Color[] colors)
        {
            int width = 64;
            int height = 48;

            byte[] yData = new byte[width * height];
            byte[] uvData = new byte[width * height / 2];
            byte[] rgbImage = new byte[width * height * 3];

            // Color layout: top(H), bottom(H), left(V), right(V)
            int topN = _capturePointsCountHorizontal;
            int botN = _capturePointsCountHorizontal;
            int leftN = _capturePointsCountVertical;
            int rightN = _capturePointsCountVertical;
            int topOff = 0;
            int botOff = topN;
            int leftOff = topN + botN;
            int rightOff = topN + botN + leftN;

            int topBottomThickness = Math.Max(1, height / 12);
            int leftRightThickness = Math.Max(1, width / 16);

            // TOP EDGE
            for (int x = 0; x < width; x++)
            {
                float t = (width > 1) ? (x / (float)(width - 1)) : 0.5f;
                byte cr, cg, cb;
                InterpolateEdge(colors, topOff, topN, t, out cr, out cg, out cb);
                for (int y = 0; y < topBottomThickness; y++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx] = cr; rgbImage[idx + 1] = cg; rgbImage[idx + 2] = cb;
                }
            }

            // BOTTOM EDGE
            for (int x = 0; x < width; x++)
            {
                float t = (width > 1) ? (x / (float)(width - 1)) : 0.5f;
                byte cr, cg, cb;
                InterpolateEdge(colors, botOff, botN, t, out cr, out cg, out cb);
                for (int y = height - topBottomThickness; y < height; y++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx] = cr; rgbImage[idx + 1] = cg; rgbImage[idx + 2] = cb;
                }
            }

            // Build extended vertical edges that include the H corner samples as endpoints.
            // Left V edge actual sample y positions are only at mid-heights (corners excluded
            // in GenerateCaptureGrid), so interpolating across full height needs the corner
            // colors to avoid stretching mid-samples to y=0 and y=H-1.
            // V edge build strategy:
            //   - If both H corners are LIT, the real edge has content → ignore middle V
            //     samples (they may have been adapted inward into a dark area), just
            //     gradient between the two corners.
            //   - If at least one corner is BLACK (likely pillarbox), include V samples;
            //     they're either at edge X (no adaptation) or at adapted X showing the
            //     actual lit content the adaptation was meant to find.
            Color leftTopCorner = colors[topOff + 0];
            Color leftBotCorner = colors[botOff + 0];
            Color rightTopCorner = colors[topOff + topN - 1];
            Color rightBotCorner = colors[botOff + botN - 1];

            Color[] leftExtended;
            if (leftN > 0 && (IsPointBlack(leftTopCorner) || IsPointBlack(leftBotCorner)))
            {
                leftExtended = new Color[leftN + 2];
                leftExtended[0] = leftTopCorner;
                Array.Copy(colors, leftOff, leftExtended, 1, leftN);
                leftExtended[leftN + 1] = leftBotCorner;
            }
            else
            {
                leftExtended = new Color[] { leftTopCorner, leftBotCorner };
            }

            Color[] rightExtended;
            if (rightN > 0 && (IsPointBlack(rightTopCorner) || IsPointBlack(rightBotCorner)))
            {
                rightExtended = new Color[rightN + 2];
                rightExtended[0] = rightTopCorner;
                Array.Copy(colors, rightOff, rightExtended, 1, rightN);
                rightExtended[rightN + 1] = rightBotCorner;
            }
            else
            {
                rightExtended = new Color[] { rightTopCorner, rightBotCorner };
            }

            // LEFT EDGE (middle rows only)
            for (int y = topBottomThickness; y < height - topBottomThickness; y++)
            {
                float t = (height > 1) ? (y / (float)(height - 1)) : 0.5f;
                byte cr, cg, cb;
                InterpolateEdge(leftExtended, 0, leftExtended.Length, t, out cr, out cg, out cb);
                for (int x = 0; x < leftRightThickness; x++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx] = cr; rgbImage[idx + 1] = cg; rgbImage[idx + 2] = cb;
                }
            }

            // RIGHT EDGE (middle rows only)
            for (int y = topBottomThickness; y < height - topBottomThickness; y++)
            {
                float t = (height > 1) ? (y / (float)(height - 1)) : 0.5f;
                byte cr, cg, cb;
                InterpolateEdge(rightExtended, 0, rightExtended.Length, t, out cr, out cg, out cb);
                for (int x = width - leftRightThickness; x < width; x++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx] = cr; rgbImage[idx + 1] = cg; rgbImage[idx + 2] = cb;
                }
            }

            // Convert RGB to NV12 (BT.2020)
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int rgbIdx = (y * width + x) * 3;
                    int yVal = (int)(0.2627 * rgbImage[rgbIdx] + 0.678 * rgbImage[rgbIdx + 1] + 0.0593 * rgbImage[rgbIdx + 2]);
                    yData[y * width + x] = (byte)Math.Max(0, Math.Min(255, yVal));
                }
            }
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    int rgbIdx = (y * width + x) * 3;
                    byte r = rgbImage[rgbIdx], g = rgbImage[rgbIdx + 1], b = rgbImage[rgbIdx + 2];
                    int uVal = (int)(-0.1396 * r - 0.36037 * g + 0.5 * b + 128);
                    int vVal = (int)(0.5 * r - 0.4598 * g - 0.0402 * b + 128);
                    int uvIdx = ((y / 2) * (width / 2) + (x / 2)) * 2;
                    uvData[uvIdx] = (byte)Math.Max(0, Math.Min(255, uVal));
                    uvData[uvIdx + 1] = (byte)Math.Max(0, Math.Min(255, vVal));
                }
            }

            return (yData, uvData);
        }

        /// <summary>
        /// Check if edges are black and move sampling inward toward center.
        /// Handles letterbox (black top/bottom) and pillarbox (black left/right).
        /// Resets to original position when edge becomes non-black.
        /// </summary>
        private void CheckAndAdaptEdges(Color[] colors)
        {
            // Periodic probe: every ~10s, temporarily sample at 2% to check if content returned
            if (_edgesAdapted)
            {
                _probeResetCounter++;
                if (_probeResetCounter >= ProbeResetInterval)
                {
                    _probeResetCounter = 0;

                    // Save current insets
                    float savedTop = _topInset, savedBottom = _bottomInset;
                    float savedLeft = _leftInset, savedRight = _rightInset;

                    // Temporarily move to 2% and sample ONE frame
                    _topInset = InsetSteps[0]; _bottomInset = InsetSteps[0];
                    _leftInset = InsetSteps[0]; _rightInset = InsetSteps[0];
                    RecalculateCoordinatesWithInsets();

                    Color[] probeColors = GetColors();

                    int pTopN = _capturePointsCountHorizontal;
                    int pBottomN = _capturePointsCountHorizontal;
                    int pLeftN = _capturePointsCountVertical;

                    bool stillBlackTop = savedTop > InsetSteps[0] && IsEdgeBlack(probeColors, 0, pTopN);
                    bool stillBlackBottom = savedBottom > InsetSteps[0] && IsEdgeBlack(probeColors, pTopN, pBottomN);
                    bool stillBlackLeft = savedLeft > InsetSteps[0] && IsEdgeBlack(probeColors, pTopN + pBottomN, pLeftN);
                    bool stillBlackRight = savedRight > InsetSteps[0] && IsEdgeBlack(probeColors, pTopN + pBottomN + pLeftN, _capturePointsCountVertical);

                    // Restore only edges that are still black, reset edges that have content
                    _topInset = stillBlackTop ? savedTop : InsetSteps[0];
                    _bottomInset = stillBlackBottom ? savedBottom : InsetSteps[0];
                    _leftInset = stillBlackLeft ? savedLeft : InsetSteps[0];
                    _rightInset = stillBlackRight ? savedRight : InsetSteps[0];

                    _edgesAdapted = (_topInset > InsetSteps[0] || _bottomInset > InsetSteps[0] ||
                                     _leftInset > InsetSteps[0] || _rightInset > InsetSteps[0]);

                    RecalculateCoordinatesWithInsets();

                    // Probe sampled at temporary positions; merged interleaved cache is now
                    // stale w.r.t. final positions. Reset so next interleave frames re-sample
                    // cleanly into the cache and skip warmup-aware adaptive checks.
                    ResetInterleaveState();
                    _frameCounter = 0;

                    Helper.Log.Write(Helper.eLogType.Info,
                        $"[AdaptiveEdge] Probe: T={_topInset:F2}{(stillBlackTop?" (still black)":"")} " +
                        $"B={_bottomInset:F2}{(stillBlackBottom?" (still black)":"")} " +
                        $"L={_leftInset:F2}{(stillBlackLeft?" (still black)":"")} " +
                        $"R={_rightInset:F2}{(stillBlackRight?" (still black)":"")}");
                    return;
                }
            }

            int topColors = _capturePointsCountHorizontal;
            int bottomColors = _capturePointsCountHorizontal;
            int leftColors = _capturePointsCountVertical;
            int rightColors = _capturePointsCountVertical;

            int topStart = 0;
            int bottomStart = topColors;
            int leftStart = topColors + bottomColors;
            int rightStart = topColors + bottomColors + leftColors;

            bool topBlack = IsEdgeBlack(colors, topStart, topColors);
            bool bottomBlack = IsEdgeBlack(colors, bottomStart, bottomColors);
            bool leftBlack = IsEdgeBlack(colors, leftStart, leftColors);
            bool rightBlack = IsEdgeBlack(colors, rightStart, rightColors);

            bool changed = false;

            // Top edge
            if (topBlack) { _topBlackCount++; } else { _topBlackCount = 0; }
            if (_topBlackCount >= BlackThresholdFrames)
            {
                float newInset = GetNextInset(_topInset);
                if (newInset != _topInset) { _topInset = newInset; changed = true; }
                _topBlackCount = 0; // reset counter to wait again before next step
            }
            // Don't instantly reset — periodic probe handles checking if content returned

            // Bottom edge
            if (bottomBlack) { _bottomBlackCount++; } else { _bottomBlackCount = 0; }
            if (_bottomBlackCount >= BlackThresholdFrames)
            {
                float newInset = GetNextInset(_bottomInset);
                if (newInset != _bottomInset) { _bottomInset = newInset; changed = true; }
                _bottomBlackCount = 0;
            }
            // Don't instantly reset — periodic probe handles checking if content returned

            // Left edge
            if (leftBlack) { _leftBlackCount++; } else { _leftBlackCount = 0; }
            if (_leftBlackCount >= BlackThresholdFrames)
            {
                float newInset = GetNextInset(_leftInset);
                if (newInset != _leftInset) { _leftInset = newInset; changed = true; }
                _leftBlackCount = 0;
            }
            // Don't instantly reset — periodic probe handles checking if content returned

            // Right edge
            if (rightBlack) { _rightBlackCount++; } else { _rightBlackCount = 0; }
            if (_rightBlackCount >= BlackThresholdFrames)
            {
                float newInset = GetNextInset(_rightInset);
                if (newInset != _rightInset) { _rightInset = newInset; changed = true; }
                _rightBlackCount = 0;
            }
            // Don't instantly reset — periodic probe handles checking if content returned

            // Recalculate pixel coordinates if any edge moved
            if (changed)
            {
                _edgesAdapted = (_topInset > InsetSteps[0] || _bottomInset > InsetSteps[0] ||
                                 _leftInset > InsetSteps[0] || _rightInset > InsetSteps[0]);

                Helper.Log.Write(Helper.eLogType.Info,
                    $"[AdaptiveEdge] Insets changed: T={_topInset:F2} B={_bottomInset:F2} L={_leftInset:F2} R={_rightInset:F2}");

                // Recalculate pixel coordinates with new insets, then reset interleave
                // cache so it doesn't keep stale samples from the previous positions.
                RecalculateCoordinatesWithInsets();
                ResetInterleaveState();
                _frameCounter = 0;
            }
        }

        private bool IsEdgeBlack(Color[] colors, int startIdx, int count)
        {
            if (count <= 0 || startIdx + count > colors.Length) return false;
            for (int i = startIdx; i < startIdx + count; i++)
            {
                if (colors[i].R > BlackColorThreshold ||
                    colors[i].G > BlackColorThreshold ||
                    colors[i].B > BlackColorThreshold)
                    return false;
            }
            return true;
        }

        private float GetNextInset(float currentInset)
        {
            for (int i = 0; i < InsetSteps.Length - 1; i++)
            {
                if (Math.Abs(currentInset - InsetSteps[i]) < 0.001f)
                    return InsetSteps[i + 1];
            }
            return InsetSteps[InsetSteps.Length - 1]; // max inset
        }

        /// <summary>
        /// Recalculate pixel coordinates based on adaptive edge insets.
        /// Top/bottom points get Y shifted, left/right get X shifted.
        /// </summary>
        private void RecalculateCoordinatesWithInsets()
        {
            if (_pixelCoordinates == null || _condition.Width == 0) return;

            int topColors = _capturePointsCountHorizontal;
            int bottomColors = _capturePointsCountHorizontal;
            int leftColors = _capturePointsCountVertical;

            int screenW = _condition.Width;
            int screenH = _condition.Height;

            // Top edge: shift Y inward
            for (int i = 0; i < topColors && i < _pixelCoordinates.Length; i++)
            {
                _pixelCoordinates[i].Y = Math.Max(0, (int)(_topInset * screenH));
            }

            // Bottom edge: shift Y inward from bottom
            for (int i = topColors; i < topColors + bottomColors && i < _pixelCoordinates.Length; i++)
            {
                _pixelCoordinates[i].Y = Math.Min(screenH - 1, (int)((1.0f - _bottomInset) * screenH));
            }

            // Left edge: shift X inward
            for (int i = topColors + bottomColors; i < topColors + bottomColors + leftColors && i < _pixelCoordinates.Length; i++)
            {
                _pixelCoordinates[i].X = Math.Max(0, (int)(_leftInset * screenW));
            }

            // Right edge: shift X inward from right
            for (int i = topColors + bottomColors + leftColors; i < _pixelCoordinates.Length; i++)
            {
                _pixelCoordinates[i].X = Math.Min(screenW - 1, (int)((1.0f - _rightInset) * screenW));
            }
        }

        // Brightness boost: Samsung hardware often returns sub-range values (e.g. 0-800 instead of 0-1023)
        // 1.0 = no boost, 1.3 = 30% brighter, 1.5 = 50% brighter
        private const float BrightnessBoost = 1.7f;

        /// <summary>
        /// Convert 10-bit color value (0-1023) to 8-bit (0-255) with brightness boost
        /// </summary>
        private byte ScaleTo8Bit(int value)
        {
            int scaled = (int)(value * 255 * BrightnessBoost / 1023);
            return (byte)Math.Min(255, Math.Max(0, scaled));
        }

        /// <summary>
        /// Capture screen using pixel sampling
        /// </summary>
        public CaptureResult Capture(int width, int height)
        {
            try
            {
                // Initialize if not already done
                if (!_isInitialized)
                {
                    if (!GetCondition())
                    {
                        return CaptureResult.CreateFailure("PixelSamplingV2: Failed to get condition");
                    }

                    // Pre-calculate all pixel coordinates once
                    PreCalculateCoordinates();

                    _isInitialized = true;
                }

                // Temporal interleaving: sample 1/N points per frame, merge with last values.
                Color[] colors = GetColorsInterleaved();

                // Keep real sample values. Only fill true gaps (black between two lit
                // neighbors) with linear blend. No multi-point smoothing. Corners only
                // get letterbox bleed when one side is black.
                FillBlackGapsPerEdge(colors);
                BlendCorners(colors);

                // Apply temporal smoothing (EMA)
                if (_previousColors != null && _previousColors.Length == colors.Length)
                {
                    for (int ci = 0; ci < colors.Length; ci++)
                    {
                        colors[ci].R = (int)(SmoothingAlpha * colors[ci].R + (1 - SmoothingAlpha) * _previousColors[ci].R);
                        colors[ci].G = (int)(SmoothingAlpha * colors[ci].G + (1 - SmoothingAlpha) * _previousColors[ci].G);
                        colors[ci].B = (int)(SmoothingAlpha * colors[ci].B + (1 - SmoothingAlpha) * _previousColors[ci].B);
                    }
                }
                _previousColors = new Color[colors.Length];
                Array.Copy(colors, _previousColors, colors.Length);

                // Skip adaptive edge checks during interleave warmup — until every group
                // has been sampled at least twice, _mergedInterleavedColors still contains
                // (0,0,0) for some indices, which would falsely register as black edges.
                _frameCounter++;
                if (_frameCounter > _interleaveGroups * 2)
                    CheckAndAdaptEdges(colors);

                // Convert to NV12 format
                var (yData, uvData) = ConvertColorsToNV12(colors);

                if (DebugMode)
                    DrawDebugSampleMarkers(yData, uvData, 64, 48);

                return CaptureResult.CreateSuccess(yData, uvData, 64, 48);
            }
            catch (Exception ex)
            {
                return CaptureResult.CreateFailure($"PixelSampling exception: {ex.Message}");
            }
        }

        private const int GapBlackThreshold = 8; // 10-bit channel value below this = black gap

        private static bool IsPointBlack(Color c)
        {
            return c.R <= GapBlackThreshold && c.G <= GapBlackThreshold && c.B <= GapBlackThreshold;
        }

        /// <summary>
        /// Returns primary if it is not black; otherwise returns fallback.
        /// Used when choosing V-edge endpoints so a black corner (pillarbox) can be
        /// replaced by the nearest adapted V sample, but a lit corner is never
        /// replaced by a black adapted sample.
        /// </summary>
        private static Color PickLit(Color primary, Color fallback)
        {
            return IsPointBlack(primary) ? fallback : primary;
        }

        /// <summary>
        /// For each edge, replace black sample points with a blend of the nearest lit
        /// neighbors on the same edge. Two-sided gaps use distance-weighted linear blend;
        /// one-sided gaps use exponential falloff so a single lit sample doesn't smear
        /// uniformly across the whole edge. Fully black edges (real letterbox) stay black.
        /// </summary>
        private void FillBlackGapsPerEdge(Color[] colors)
        {
            int topN = _capturePointsCountHorizontal;
            int botN = _capturePointsCountHorizontal;
            int leftN = _capturePointsCountVertical;
            int rightN = _capturePointsCountVertical;

            FillBlackGapsInSegment(colors, 0, topN);
            FillBlackGapsInSegment(colors, topN, botN);
            FillBlackGapsInSegment(colors, topN + botN, leftN);
            FillBlackGapsInSegment(colors, topN + botN + leftN, rightN);
        }

        private void FillBlackGapsInSegment(Color[] colors, int offset, int count)
        {
            if (count <= 0) return;

            int[] prevNonBlack = new int[count];
            int[] nextNonBlack = new int[count];

            int last = -1;
            for (int i = 0; i < count; i++)
            {
                prevNonBlack[i] = last;
                if (!IsPointBlack(colors[offset + i])) last = i;
            }
            last = -1;
            for (int i = count - 1; i >= 0; i--)
            {
                nextNonBlack[i] = last;
                if (!IsPointBlack(colors[offset + i])) last = i;
            }

            for (int i = 0; i < count; i++)
            {
                if (!IsPointBlack(colors[offset + i])) continue;

                int p = prevNonBlack[i];
                int n = nextNonBlack[i];

                // Only fill when lit on BOTH sides — a real gap between two known colors.
                // One-sided and fully-black segments are left as-is so actual content
                // (including real black content) is preserved unchanged.
                if (p < 0 || n < 0) continue;

                float dp = i - p;
                float dn = n - i;
                float wp = dn / (dp + dn);
                float wn = dp / (dp + dn);
                Color cp = colors[offset + p];
                Color cn = colors[offset + n];
                Color blended = default;
                blended.R = (int)(wp * cp.R + wn * cn.R);
                blended.G = (int)(wp * cp.G + wn * cn.G);
                blended.B = (int)(wp * cp.B + wn * cn.B);
                colors[offset + i] = blended;
            }
        }

        /// <summary>
        /// Per-edge 1-3-1 smoothing kernel applied across multiple passes to soften
        /// transitions so adjacent LEDs share a smooth gradient.
        /// </summary>
        private void SmoothEdgesSpatial(Color[] colors)
        {
            int topN = _capturePointsCountHorizontal;
            int botN = _capturePointsCountHorizontal;
            int leftN = _capturePointsCountVertical;
            int rightN = _capturePointsCountVertical;

            for (int pass = 0; pass < 4; pass++)
            {
                SmoothSegment(colors, 0, topN);
                SmoothSegment(colors, topN, botN);
                SmoothSegment(colors, topN + botN, leftN);
                SmoothSegment(colors, topN + botN + leftN, rightN);
            }
        }

        private void SmoothSegment(Color[] colors, int offset, int count)
        {
            if (count < 3) return;
            Color[] src = new Color[count];
            Array.Copy(colors, offset, src, 0, count);

            for (int i = 0; i < count; i++)
            {
                Color prev = src[Math.Max(0, i - 1)];
                Color curr = src[i];
                Color next = src[Math.Min(count - 1, i + 1)];

                Color smoothed = default;
                smoothed.R = (prev.R + 3 * curr.R + next.R) / 5;
                smoothed.G = (prev.G + 3 * curr.G + next.G) / 5;
                smoothed.B = (prev.B + 3 * curr.B + next.B) / 5;
                colors[offset + i] = smoothed;
            }
        }

        /// <summary>
        /// Blend corner pairs between horizontal (top/bottom) and vertical (left/right)
        /// edges so corner LEDs don't show mismatched colors.
        /// </summary>
        private void BlendCorners(Color[] colors)
        {
            int topN = _capturePointsCountHorizontal;
            int botN = _capturePointsCountHorizontal;
            int leftN = _capturePointsCountVertical;
            int rightN = _capturePointsCountVertical;

            int topOff = 0;
            int botOff = topN;
            int leftOff = topN + botN;
            int rightOff = topN + botN + leftN;

            if (topN == 0 || botN == 0) return;

            if (leftN > 0) BlendCornerPair(colors, topOff + 0, leftOff + 0);
            if (rightN > 0) BlendCornerPair(colors, topOff + topN - 1, rightOff + 0);
            if (leftN > 0) BlendCornerPair(colors, botOff + 0, leftOff + leftN - 1);
            if (rightN > 0) BlendCornerPair(colors, botOff + botN - 1, rightOff + rightN - 1);
        }

        private void BlendCornerPair(Color[] colors, int idxH, int idxV)
        {
            Color h = colors[idxH];
            Color v = colors[idxV];

            bool hBlack = IsPointBlack(h);
            bool vBlack = IsPointBlack(v);

            // Letterbox helper only: bleed lit side into black side so dark corner
            // doesn't render as hard black when adjacent edge has real content.
            // Both-lit case is handled naturally by the extended V-edge interpolation
            // (which now uses H corners as endpoints), so we don't average here.
            if (hBlack && !vBlack) { colors[idxH] = v; return; }
            if (vBlack && !hBlack) { colors[idxV] = h; return; }
        }

        /// <summary>
        /// Overlay a pure-white marker on the NV12 output at every sample point's
        /// mapped position. For 64×48 NV12: Y=255 and UV=128 (neutral) = white.
        /// Marker size depends on available edge thickness.
        /// </summary>
        private void DrawDebugSampleMarkers(byte[] yData, byte[] uvData, int width, int height)
        {
            if (_capturedPoints == null) return;

            int topBottomThickness = Math.Max(1, height / 12);
            int leftRightThickness = Math.Max(1, width / 16);

            int topY = topBottomThickness / 2;
            int bottomY = height - 1 - topBottomThickness / 2;
            int leftX = leftRightThickness / 2;
            int rightX = width - 1 - leftRightThickness / 2;

            // Use the actual normalized sample positions so markers land exactly
            // where colors were captured (matters for mid-V samples at 0.26/0.50/0.74
            // which don't align with the NV12 edge endpoints).
            int topN = _capturePointsCountHorizontal;
            int botN = _capturePointsCountHorizontal;
            int leftN = _capturePointsCountVertical;
            int rightN = _capturePointsCountVertical;
            int topOff = 0;
            int botOff = topN;
            int leftOff = topN + botN;
            int rightOff = topN + botN + leftN;

            for (int j = 0; j < topN; j++)
            {
                int x = (int)(_capturedPoints[topOff + j].X * (width - 1));
                SetPixelWhite(yData, uvData, width, height, x, topY);
            }
            for (int j = 0; j < botN; j++)
            {
                int x = (int)(_capturedPoints[botOff + j].X * (width - 1));
                SetPixelWhite(yData, uvData, width, height, x, bottomY);
            }
            for (int j = 0; j < leftN; j++)
            {
                int y = (int)(_capturedPoints[leftOff + j].Y * (height - 1));
                SetPixelWhite(yData, uvData, width, height, leftX, y);
            }
            for (int j = 0; j < rightN; j++)
            {
                int y = (int)(_capturedPoints[rightOff + j].Y * (height - 1));
                SetPixelWhite(yData, uvData, width, height, rightX, y);
            }
        }

        private static void SetPixelWhite(byte[] yData, byte[] uvData, int width, int height, int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return;

            // 3x3 square — easier to spot on low-res 64×48 output after LED upscale
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int px = x + dx;
                    int py = y + dy;
                    if (px < 0 || px >= width || py < 0 || py >= height) continue;

                    yData[py * width + px] = 255;

                    int uvIdx = ((py / 2) * (width / 2) + (px / 2)) * 2;
                    if (uvIdx + 1 < uvData.Length)
                    {
                        uvData[uvIdx] = 128;     // U neutral
                        uvData[uvIdx + 1] = 128; // V neutral
                    }
                }
            }
        }

        /// <summary>
        /// Clean up resources
        /// </summary>
        public void Cleanup()
        {
            _isInitialized = false;
            ResetInterleaveState();
            _frameCounter = 0;
            Helper.Log.Write(Helper.eLogType.Debug, "PixelSamplingV2: Cleaned up");
        }
    }
}