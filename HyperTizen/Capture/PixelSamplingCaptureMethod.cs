using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Tizen.System;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Pixel sampling capture method using libvideoenhance.so
    /// Samples individual pixels from screen edges for ambient lighting
    /// Adapted from original HyperTizen Capturer.cs to use NV12/FlatBuffers format
    /// </summary>
    public class PixelSamplingCaptureMethod : ICaptureMethod
    {
        /// <summary>
        /// Precise millisecond wait using Stopwatch spin loop.
        /// Thread.Sleep has ~20ms granularity on Tizen Mono, making it unusable
        /// for sub-20ms waits. This spins using Stopwatch for true ms precision.
        /// </summary>
        private static readonly long TicksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000;
        private static bool _waitTimingLogged = false;
        private static void PreciseWaitMs(int milliseconds)
        {
            if (milliseconds <= 0) return;
            long targetTicks = milliseconds * TicksPerMs;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedTicks < targetTicks) { }
        }
        private bool _isInitialized = false;
        private Condition _condition;

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

        private static double minPos = 0.05; // Min position in percentage from edge
        private static double maxPos = 0.95; // Max position in percentage from edge
        
        // 5 points per horizontal edge (top/bottom): step = 3840 / 5 = 768px
        // 3 iterations per vertical edge → 2 left + 3 right (after duplicate removal) = 5
        // Total: 5+5+2+3 = 15 points → 8 batches × 4ms sleep = 32ms per GetColors
        private static double _captureStepHorizontal = 768;

        private static double _captureStepVertical = 720;

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
        /// Color struct for 10-bit RGB values (0-1023)
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Color
        {
            public int R;
            public int G;
            public int B;
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
            // Reset counters — these are static and must not accumulate across multiple instances.
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

            Helper.Log.Write(Helper.eLogType.Info, $"PixelSampling: Generated {points.Count} grid capture points (horizontal: ({pixelStepHorizontal}px, vertical: {pixelStepVertical}px step), inset {minPos}–{maxPos} on 4K)");

            CapturePoint[] list = points.ToArray();
            string json = ConvertPointsToJson(list);

            Helper.Log.Write(Helper.eLogType.Info, $"PixelSampling: Generated points JSON {json}");

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

        /// <summary>
        /// Check if pixel sampling library is available
        /// </summary>
        public bool IsAvailable()
        {
            Helper.Log.Write(Helper.eLogType.Debug, "PixelSampling: Checking availability...");

            // Check if library file exists
            if (!System.IO.File.Exists("/usr/lib/libvideoenhance.so"))
            {
                Helper.Log.Write(Helper.eLogType.Debug, "PixelSampling: libvideoenhance.so not found");
                return false;
            }

            Helper.Log.Write(Helper.eLogType.Debug, "PixelSampling: Library found, available");
            return true;
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
                Helper.Log.Write(Helper.eLogType.Info, "PixelSampling: Testing capture...");

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
                "PixelSampling: Testing ALL combinations of entry points and library paths...");

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
                "PixelSampling: ALL 12 combinations failed (6 entry point variants × 2 library paths)");
            Helper.Log.Write(Helper.eLogType.Error,
                "PixelSampling: libvideoenhance.so does not support RGB pixel sampling on this Tizen version");
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
                    $"PixelSampling: Testing {variant} ({entryPrefix}) with libvideoenhance{libSuffix}");

                // Test 1: MeasureCondition
                int conditionResult = conditionFunc();
                if (conditionResult < 0)
                {
                    Helper.Log.Write(Helper.eLogType.Debug,
                        $"PixelSampling: {variant}/{libPath} condition returned error {conditionResult}");
                    return false;
                }

                // Test 2: MeasurePosition (validate entry point exists with dummy coordinates)
                int positionResult = positionFunc(0, 0, 0);
                // Position may fail if called before proper setup, but entry point should exist
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} position entry point exists (result: {positionResult})");

                // Test 3: MeasurePixel (validate entry point exists)
                Color dummyColor;
                int pixelResult = pixelFunc(0, out dummyColor);
                // Pixel may fail if no position set yet, but entry point should exist
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} pixel entry point exists (result: {pixelResult})");

                // Success - all three entry points exist and condition succeeded
                _workingVariant = variant;
                _workingLibPath = libPath;
                Helper.Log.Write(Helper.eLogType.Info,
                    $"PixelSampling: ✓ SUCCESS - All 3 entry points validated for {variant} ({entryPrefix}) with libvideoenhance{libSuffix}");
                Helper.Log.Write(Helper.eLogType.Debug, $"PixelSampling: Condition result: {conditionResult}");
                LogConditionDetails();
                return true;
            }
            catch (EntryPointNotFoundException ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} entry point not found: {ex.Message}");
                return false;
            }
            catch (DllNotFoundException ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} library file not found: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Helper method to log condition details
        /// </summary>
        private void LogConditionDetails()
        {
            Helper.Log.Write(Helper.eLogType.Info,
                $"PixelSampling: Condition - Width: {_condition.Width}, Height: {_condition.Height}, " +
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
                $"PixelSampling: Pre-calculated {_pixelCoordinates.Length} pixel coordinates");

            string json = ConvertPixelCoordinatesToJson(_pixelCoordinates);
            Helper.Log.Write(Helper.eLogType.Info, $"PixelSampling: Generated pixel coordinates JSON {json}");
        }

        /// <summary>
        /// Sample pixel colors from predefined screen positions
        /// OPTIMIZED: Sets ALL positions first, then ONE sleep, then reads ALL pixels
        /// This ensures all sampling happens at the same moment for temporal consistency
        /// </summary>
        private Color[] GetColors()
        {
            Color[] colorData = new Color[_capturedPoints.Length];

            if (_condition.ScreenCapturePoints == 0)
            {
                Helper.Log.Write(Helper.eLogType.Error, "PixelSampling: ScreenCapturePoints is 0");
                return colorData;
            }
            
            // Helper.Log.Write(Helper.eLogType.Debug, $"PixelSampling: Starting GetColors... (capturing {_capturedPoints.Length} points, screen batch size {_condition.ScreenCapturePoints})");

            // PHASE 1: Set ALL measurement positions first (no delays between batches)
            int i = 0;
            while (i < _capturedPoints.Length)
            {                
                int added = 0;
                // Set positions for this batch
                for (int j = 0; j < _condition.ScreenCapturePoints && i < _capturedPoints.Length; j++)
                {
                    // Use pre-calculated pixel coordinates
                    int x = _pixelCoordinates[i].X;
                    int y = _pixelCoordinates[i].Y;

                    // Set the measurement position
                    int res = CallMeasurePosition(j, x, y);
                    // Helper.Log.Write(Helper.eLogType.Debug, $"PixelSampling: CallMeasurePosition: j: {j}, x: {x}, y: {y}");

                    if (res < 0)
                    {
                        Helper.Log.Write(Helper.eLogType.Error,
                            $"PixelSampling: MeasurePosition failed for point {i} at ({x}, {y}) with error {res}");
                    }

                    i++;
                    added++;
                }

                // PHASE 2: Wait after positions are set so hardware can measure.
                // Thread.Sleep has ~20ms granularity on Tizen Mono (rounds up to OS tick).
                // PreciseWaitMs uses Stopwatch spin for true 8ms precision.
                // 15 points → 8 batches × 8ms = 64ms per GetColors (~15 FPS).
                if (_condition.SleepMS > 0)
                {
                    var waitSw = System.Diagnostics.Stopwatch.StartNew();
                    PreciseWaitMs(8);
                    waitSw.Stop();
                    if (!_waitTimingLogged)
                    {
                        _waitTimingLogged = true;
                        Helper.Log.Write(Helper.eLogType.Info,
                            $"PreciseWaitMs(8) actual wait: {waitSw.ElapsedMilliseconds}ms " +
                            $"(ticks: {waitSw.ElapsedTicks}, Freq: {System.Diagnostics.Stopwatch.Frequency}, " +
                            $"TicksPerMs: {TicksPerMs})");
                    }
                }

                i -= added; // Reset i to start of this batch for reading
                // Read pixels for this batch
                for (int j = 0; j < _condition.ScreenCapturePoints && i < _capturedPoints.Length; j++)
                {
                    int jClone = j;
                    Color color;
                    int res = CallMeasurePixel(jClone, out color);

                    if (res < 0)
                    {
                        Helper.Log.Write(Helper.eLogType.Error,
                            $"PixelSampling: MeasurePixel failed for point {i} with error {res}");
                        // Use black as fallback
                        color.R = 0;
                        color.G = 0;
                        color.B = 0;
                    }
                    else
                    {
                        // Validate color data (10-bit values should be 0-1023)
                        bool invalidColorData = color.R > 1023 || color.G > 1023 || color.B > 1023 ||
                                                color.R < 0 || color.G < 0 || color.B < 0;

                        if (invalidColorData)
                        {
                            Helper.Log.Write(Helper.eLogType.Warning,
                                $"PixelSampling: Invalid color data at point {i}: R={color.R}, G={color.G}, B={color.B}");
                            // Clamp to valid range
                            color.R = Math.Max(0, Math.Min(1023, color.R));
                            color.G = Math.Max(0, Math.Min(1023, color.G));
                            color.B = Math.Max(0, Math.Min(1023, color.B));
                        }
                    }

                    colorData[i] = color;
                    // Helper.Log.Write(Helper.eLogType.Debug, $"PixelSampling: CallMeasurePixel: j: {j}, r: {color.R}, g: {color.G}, b: {color.B}");
                    i++;
                }
            }

            return colorData;
        }

        /// <summary>
        /// Convert sampled pixel colors to NV12 format using BT.2020 color space
        /// Creates a virtual 64x48 image with sampled colors mapped to screen edges
        /// Uses BT.2020 coefficients for HDR10+ compatibility
        /// </summary>
        private (byte[] yData, byte[] uvData) ConvertColorsToNV12(Color[] colors)
        {
            // Output image dimensions (fixed size for NV12 encoding)
            int width = 64;
            int height = 48;
            // Helper.Log.Write(Helper.eLogType.Debug, $"PixelSampling: ConvertColorsToNV12: output {width}x{height}, input colors: {colors.Length}");

            // Allocate NV12 buffers
            byte[] yData = new byte[width * height];
            byte[] uvData = new byte[width * height / 2]; // UV plane is half the size

            // Create virtual RGB image (same logic as original ToImage method)
            byte[] rgbImage = new byte[width * height * 3]; // RGB888

            // Initialize with black
            for (int i = 0; i < rgbImage.Length; i++)
            {
                rgbImage[i] = 0;
            }

            // Dynamic edge thickness
            int topBottomThickness = Math.Max(1, height / 12);
            int leftRightThickness = Math.Max(1, width / 16);

            // Determine color distribution based on grid generation order
            // Colors are added: top row, bottom row, left column, right column
            int colorIdx = 0;
            int topRowColors = _capturePointsCountHorizontal;
            int bottomRowColors = _capturePointsCountHorizontal;
            int leftColColors = _capturePointsCountVertical;
            int rightColColors = _capturePointsCountVertical;

            // Helper.Log.Write(Helper.eLogType.Debug, $"PixelSampling: Color distribution - Top: {topRowColors}, Bottom: {bottomRowColors}, Left: {leftColColors}, Right: {rightColColors}");

            // TOP EDGE: Fill from left to right with top row colors
            for (int x = 0; x < width; x++)
            {
                float normalizedX = (width > 1) ? (x / (width - 1.0f)) : 0.0f;
                int mappedColorIdx = Math.Min((int)(normalizedX * (topRowColors - 1)), topRowColors - 1);
                
                byte r, g, b;
                if (mappedColorIdx < topRowColors - 1)
                {
                    float t = (normalizedX * (topRowColors - 1)) - mappedColorIdx;
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[colorIdx + mappedColorIdx].R) + t * ScaleTo8Bit(colors[colorIdx + mappedColorIdx + 1].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[colorIdx + mappedColorIdx].G) + t * ScaleTo8Bit(colors[colorIdx + mappedColorIdx + 1].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[colorIdx + mappedColorIdx].B) + t * ScaleTo8Bit(colors[colorIdx + mappedColorIdx + 1].B));
                }
                else
                {
                    r = ScaleTo8Bit(colors[colorIdx + mappedColorIdx].R);
                    g = ScaleTo8Bit(colors[colorIdx + mappedColorIdx].G);
                    b = ScaleTo8Bit(colors[colorIdx + mappedColorIdx].B);
                }

                for (int y = 0; y < topBottomThickness; y++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx + 0] = r;
                    rgbImage[idx + 1] = g;
                    rgbImage[idx + 2] = b;
                }
            }
            colorIdx += topRowColors;

            // BOTTOM EDGE: Fill from left to right with bottom row colors (already in reverse order from grid)
            for (int x = 0; x < width; x++)
            {
                float normalizedX = (width > 1) ? (x / (width - 1.0f)) : 0.0f;
                int mappedColorIdx = Math.Min((int)(normalizedX * (bottomRowColors - 1)), bottomRowColors - 1);
                
                byte r, g, b;
                if (mappedColorIdx < bottomRowColors - 1 && colorIdx + mappedColorIdx + 1 < colors.Length)
                {
                    float t = (normalizedX * (bottomRowColors - 1)) - mappedColorIdx;
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[colorIdx + mappedColorIdx].R) + t * ScaleTo8Bit(colors[colorIdx + mappedColorIdx + 1].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[colorIdx + mappedColorIdx].G) + t * ScaleTo8Bit(colors[colorIdx + mappedColorIdx + 1].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[colorIdx + mappedColorIdx].B) + t * ScaleTo8Bit(colors[colorIdx + mappedColorIdx + 1].B));
                }
                else if (colorIdx + mappedColorIdx < colors.Length)
                {
                    r = ScaleTo8Bit(colors[colorIdx + mappedColorIdx].R);
                    g = ScaleTo8Bit(colors[colorIdx + mappedColorIdx].G);
                    b = ScaleTo8Bit(colors[colorIdx + mappedColorIdx].B);
                }
                else
                {
                    r = g = b = 0;
                }

                for (int y = height - topBottomThickness; y < height; y++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx + 0] = r;
                    rgbImage[idx + 1] = g;
                    rgbImage[idx + 2] = b;
                }
            }
            colorIdx += bottomRowColors;

            // LEFT EDGE: middle rows only — do NOT overwrite corner pixels already set by top/bottom.
            // normalizedY is relative to the middle region so samples map correctly within it.
            {
                int midStart = topBottomThickness;
                int midEnd   = height - topBottomThickness; // exclusive
                int midH     = midEnd - midStart;
                for (int y = midStart; y < midEnd; y++)
                {
                    float normalizedY = (midH > 1) ? ((y - midStart) / (float)(midH - 1)) : 0.5f;
                    int mappedColorIdx = Math.Min((int)(normalizedY * (leftColColors - 1)), leftColColors - 1);

                    byte r, g, b;
                    if (mappedColorIdx < leftColColors - 1)
                    {
                        float t = (normalizedY * (leftColColors - 1)) - mappedColorIdx;
                        r = (byte)((1 - t) * ScaleTo8Bit(colors[colorIdx + mappedColorIdx].R) + t * ScaleTo8Bit(colors[colorIdx + mappedColorIdx + 1].R));
                        g = (byte)((1 - t) * ScaleTo8Bit(colors[colorIdx + mappedColorIdx].G) + t * ScaleTo8Bit(colors[colorIdx + mappedColorIdx + 1].G));
                        b = (byte)((1 - t) * ScaleTo8Bit(colors[colorIdx + mappedColorIdx].B) + t * ScaleTo8Bit(colors[colorIdx + mappedColorIdx + 1].B));
                    }
                    else
                    {
                        r = ScaleTo8Bit(colors[colorIdx + mappedColorIdx].R);
                        g = ScaleTo8Bit(colors[colorIdx + mappedColorIdx].G);
                        b = ScaleTo8Bit(colors[colorIdx + mappedColorIdx].B);
                    }

                    for (int x = 0; x < leftRightThickness; x++)
                    {
                        int idx = (y * width + x) * 3;
                        rgbImage[idx + 0] = r;
                        rgbImage[idx + 1] = g;
                        rgbImage[idx + 2] = b;
                    }
                }
            }
            colorIdx += leftColColors;

            // RIGHT EDGE: middle rows only — same reason as left edge.
            {
                int midStart = topBottomThickness;
                int midEnd   = height - topBottomThickness;
                int midH     = midEnd - midStart;
                for (int y = midStart; y < midEnd; y++)
                {
                    float normalizedY = (midH > 1) ? ((y - midStart) / (float)(midH - 1)) : 0.5f;
                    int mappedColorIdx = Math.Min((int)(normalizedY * (rightColColors - 1)), rightColColors - 1);

                    byte r, g, b;
                    if (mappedColorIdx < rightColColors - 1)
                    {
                        float t = (normalizedY * (rightColColors - 1)) - mappedColorIdx;
                        r = (byte)((1 - t) * ScaleTo8Bit(colors[colorIdx + mappedColorIdx].R) + t * ScaleTo8Bit(colors[colorIdx + mappedColorIdx + 1].R));
                        g = (byte)((1 - t) * ScaleTo8Bit(colors[colorIdx + mappedColorIdx].G) + t * ScaleTo8Bit(colors[colorIdx + mappedColorIdx + 1].G));
                        b = (byte)((1 - t) * ScaleTo8Bit(colors[colorIdx + mappedColorIdx].B) + t * ScaleTo8Bit(colors[colorIdx + mappedColorIdx + 1].B));
                    }
                    else
                    {
                        r = ScaleTo8Bit(colors[colorIdx + mappedColorIdx].R);
                        g = ScaleTo8Bit(colors[colorIdx + mappedColorIdx].G);
                        b = ScaleTo8Bit(colors[colorIdx + mappedColorIdx].B);
                    }

                    for (int x = width - leftRightThickness; x < width; x++)
                    {
                        int idx = (y * width + x) * 3;
                        rgbImage[idx + 0] = r;
                        rgbImage[idx + 1] = g;
                        rgbImage[idx + 2] = b;
                    }
                }
            }

            // Convert RGB to NV12 using BT.2020 color space (HDR10+ compatible)
            // Y plane
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int rgbIdx = (y * width + x) * 3;
                    byte r = rgbImage[rgbIdx + 0];
                    byte g = rgbImage[rgbIdx + 1];
                    byte b = rgbImage[rgbIdx + 2];

                    // BT.2020 Y = 0.2627R + 0.678G + 0.0593B
                    int yVal = (int)(0.2627 * r + 0.678 * g + 0.0593 * b);
                    yData[y * width + x] = (byte)Math.Max(0, Math.Min(255, yVal));
                }
            }

            // UV plane (interleaved, subsampled 2x2)
            // UV dimensions: (width/2) * (height/2), with U and V interleaved
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    // Sample 2x2 block
                    int rgbIdx = (y * width + x) * 3;
                    byte r = rgbImage[rgbIdx + 0];
                    byte g = rgbImage[rgbIdx + 1];
                    byte b = rgbImage[rgbIdx + 2];

                    // BT.2020 U = -0.1396R - 0.36037G + 0.5B + 128
                    // BT.2020 V = 0.5R - 0.4598G - 0.0402B + 128
                    int uVal = (int)(-0.1396 * r - 0.36037 * g + 0.5 * b + 128);
                    int vVal = (int)(0.5 * r - 0.4598 * g - 0.0402 * b + 128);

                    // NV12 UV indexing: ((y/2) * (width/2) + (x/2)) * 2
                    // This accounts for the halved dimensions and interleaved U,V pairs
                    int uvIdx = ((y / 2) * (width / 2) + (x / 2)) * 2;
                    if (uvIdx + 1 >= uvData.Length)
                    {
                        Helper.Log.Write(Helper.eLogType.Debug, $"PixelSampling: Index error - y: {y}, x: {x}, width: {width}, uvIdx: {uvIdx}, uvData.Length: {uvData.Length}");
                    }

                    uvData[uvIdx + 0] = (byte)Math.Max(0, Math.Min(255, uVal)); // U
                    uvData[uvIdx + 1] = (byte)Math.Max(0, Math.Min(255, vVal)); // V
                }
            }

            return (yData, uvData);
        }

        /// <summary>
        /// Convert 10-bit color value (0-1023) to 8-bit (0-255) using proper scaling
        /// Uses scaling rather than clamping to preserve color accuracy
        /// </summary>
        private byte ScaleTo8Bit(int value)
        {
            // Scale 10-bit (0-1023) to 8-bit (0-255)
            return (byte)Math.Min(255, value * 255 / 1023);
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
                        return CaptureResult.CreateFailure("PixelSampling: Failed to get condition");
                    }

                    // Pre-calculate all pixel coordinates once
                    PreCalculateCoordinates();

                    _isInitialized = true;
                }

                var sw = Stopwatch.StartNew();

                // Sample pixels from screen
                Color[] colors = GetColors();

                sw.Stop();

                if (colors == null || colors.Length == 0)
                {
                    return CaptureResult.CreateFailure("PixelSampling: No colors captured");
                }

                Helper.Log.Write(Helper.eLogType.Info, $"[V3-SPIN] GetColors {sw.ElapsedMilliseconds}ms / {colors.Length}pts / wait=8ms");
                Helper.Log.Write(Helper.eLogType.Info, $"[V3-SPIN] x23 GetColors {sw.ElapsedMilliseconds}ms / {colors.Length}pts / wait=8ms");

                // string json = ConvertColorsToJson(colors);
                // Helper.Log.Write(Helper.eLogType.Info, $"PixelSampling: Generated colors JSON {json}");

                // Convert to NV12 format
                var (yData, uvData) = ConvertColorsToNV12(colors);

                return CaptureResult.CreateSuccess(yData, uvData, 64, 48);
            }
            catch (Exception ex)
            {
                return CaptureResult.CreateFailure($"PixelSampling exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Clean up resources
        /// </summary>
        public void Cleanup()
        {
            _isInitialized = false;
            Helper.Log.Write(Helper.eLogType.Debug, "PixelSampling: Cleaned up");
        }
    }
}