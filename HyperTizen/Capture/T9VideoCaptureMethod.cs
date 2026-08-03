using System;
using System.Runtime.InteropServices;
using Tizen.System;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Tizen 9 video capture method using libvideo-capture.so.0.1.0
    /// Tests multiple entry points: secvideo_api_*, ppi_video_capture_*, and IVideoCapture C++ API
    /// Based on analysis of Tizen 9 library exports
    /// </summary>
    public class T9VideoCaptureMethod : ICaptureMethod
    {
        private bool _isInitialized = false;
        private string _workingEntryPoint = null; // Which API variant works
        private const int RTLD_LAZY = 1;

        public string Name => "T9 Video Capture (libvideo-capture.so.0.1.0)";
        public CaptureMethodType Type => CaptureMethodType.T9VideoCapture;

        #region P/Invoke Declarations - Dynamic Library Loading

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlopen(string filename, int flags);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dlclose(IntPtr handle);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlerror();

        #endregion

        #region P/Invoke Declarations - secvideo_api_* (Plain C API - Priority 1)

        // === Entry Point 1: secvideo_api_capture_screen_video_only ===
        [DllImport("/usr/lib/libvideo-capture.so.0.1.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "secvideo_api_capture_screen_video_only")]
        private static extern int secvideo_api_capture_screen_video_only_0_1_0(
            ref InputParams inputParams,
            ref OutputParams outputParams);

        [DllImport("/usr/lib/libvideo-capture.so.0.1", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "secvideo_api_capture_screen_video_only")]
        private static extern int secvideo_api_capture_screen_video_only_0_1(
            ref InputParams inputParams,
            ref OutputParams outputParams);

        [DllImport("/usr/lib/libvideo-capture.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "secvideo_api_capture_screen_video_only")]
        private static extern int secvideo_api_capture_screen_video_only(
            ref InputParams inputParams,
            ref OutputParams outputParams);

        // === Entry Point 2: secvideo_api_capture_screen ===
        [DllImport("/usr/lib/libvideo-capture.so.0.1.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "secvideo_api_capture_screen")]
        private static extern int secvideo_api_capture_screen_0_1_0(
            ref InputParams inputParams,
            ref OutputParams outputParams);

        [DllImport("/usr/lib/libvideo-capture.so.0.1", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "secvideo_api_capture_screen")]
        private static extern int secvideo_api_capture_screen_0_1(
            ref InputParams inputParams,
            ref OutputParams outputParams);

        [DllImport("/usr/lib/libvideo-capture.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "secvideo_api_capture_screen")]
        private static extern int secvideo_api_capture_screen(
            ref InputParams inputParams,
            ref OutputParams outputParams);

        #endregion

        #region P/Invoke Declarations - ppi_video_capture_* (Plain C API - Priority 2)

        // Lock/Unlock functions
        [DllImport("/usr/lib/libvideo-capture.so.0.1.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_video_capture_lock_global")]
        private static extern int ppi_video_capture_lock_global();

        [DllImport("/usr/lib/libvideo-capture.so.0.1.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_video_capture_unlock_global")]
        private static extern int ppi_video_capture_unlock_global();

        // Main video YUV capture
        [DllImport("/usr/lib/libvideo-capture.so.0.1.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_video_capture_get_video_main_yuv")]
        private static extern int ppi_video_capture_get_video_main_yuv(
            ref InputParams inputParams,
            ref OutputParams outputParams);

        // Screen post-processing YUV capture
        [DllImport("/usr/lib/libvideo-capture.so.0.1.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_video_capture_get_screen_post_yuv")]
        private static extern int ppi_video_capture_get_screen_post_yuv(
            ref InputParams inputParams,
            ref OutputParams outputParams);

        // Protection check
        [DllImport("/usr/lib/libvideo-capture.so.0.1.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_video_capture_is_protect_capture")]
        private static extern int ppi_video_capture_is_protect_capture(
            out int isProtected);

        #endregion

        #region Native Structs

        /// <summary>
        /// Input parameters for video capture
        /// Based on analysis of libvideo-capture.so and reference implementation
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct InputParams
        {
            public int field0;          // Usually 0
            public int field1;          // Usually 0
            public int cropX;           // Crop X (use 0xffff for full screen)
            public int cropY;           // Crop Y (use 0xffff for full screen)
            public int field4;          // Usually 1
            public int yBufferSize;     // Y buffer size (e.g., 0x7e900 for 1920x1080)
            public int uvBufferSize;    // UV buffer size (e.g., 0x7e900 for 1920x1080)
            public IntPtr pYBuffer;     // Pointer to Y buffer
            public IntPtr pUVBuffer;    // Pointer to UV buffer
        }

        /// <summary>
        /// Output parameters from video capture
        /// Contains actual captured dimensions and buffer info
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct OutputParams
        {
            public int width;           // Captured width
            public int height;          // Captured height
            public int field2;          // Unknown field
            public int field3;          // Unknown field
            public int ySize;           // Actual Y buffer size used
            public int uvSize;          // Actual UV buffer size used
            public IntPtr pYData;       // Pointer to Y data
            public IntPtr pUVData;      // Pointer to UV data
        }

        #endregion

        #region ICaptureMethod Implementation

        public bool IsAvailable()
        {
            Helper.Log.Write(Helper.eLogType.Info, "[T9VideoCaptureMethod] Checking availability...");

            // Test library loading using dlopen
            string[] libraryPaths = new string[]
            {
                "/usr/lib/libvideo-capture.so.0.1.0",
                "/usr/lib/libvideo-capture.so.0.1",
                "/usr/lib/libvideo-capture.so"
            };

            foreach (var libPath in libraryPaths)
            {
                IntPtr handle = dlopen(libPath, RTLD_LAZY);
                if (handle != IntPtr.Zero)
                {
                    Helper.Log.Write(Helper.eLogType.Info, $"[T9VideoCaptureMethod] Library loaded: {libPath}");

                    // Test for entry points (highest to lowest priority)
                    string[] entryPoints = new string[]
                    {
                        "secvideo_api_capture_screen_video_only",
                        "secvideo_api_capture_screen",
                        "ppi_video_capture_get_video_main_yuv",
                        "ppi_video_capture_get_screen_post_yuv"
                    };

                    bool foundEntryPoint = false;
                    foreach (var entryPoint in entryPoints)
                    {
                        IntPtr symbol = dlsym(handle, entryPoint);
                        if (symbol != IntPtr.Zero)
                        {
                            Helper.Log.Write(Helper.eLogType.Info, $"[T9VideoCaptureMethod] ✓ Found entry point: {entryPoint}");
                            foundEntryPoint = true;
                        }
                    }

                    dlclose(handle);

                    if (foundEntryPoint)
                    {
                        Helper.Log.Write(Helper.eLogType.Info, "[T9VideoCaptureMethod] Available!");
                        return true;
                    }
                }
                else
                {
                    // Get detailed error information
                    IntPtr errorPtr = dlerror();
                    string dlError = errorPtr != IntPtr.Zero
                        ? Marshal.PtrToStringAnsi(errorPtr)
                        : null;

                    // Check if file exists
                    bool fileExists = System.IO.File.Exists(libPath);

                    // Build detailed error message
                    string errorMsg = $"[T9VideoCaptureMethod] dlopen() failed for {libPath}";
                    if (!fileExists)
                    {
                        errorMsg += " - File does not exist";
                    }
                    else if (dlError != null)
                    {
                        errorMsg += $" - {dlError}";
                    }
                    else
                    {
                        errorMsg += " - dlopen returned NULL but dlerror() also returned NULL (possible permission issue or invalid ELF format)";
                    }

                    Helper.Log.Write(Helper.eLogType.Warning, errorMsg);
                }
            }

            Helper.Log.Write(Helper.eLogType.Warning, "[T9VideoCaptureMethod] Not available - no library or entry points found");
            return false;
        }

        public bool Test()
        {
            Helper.Log.Write(Helper.eLogType.Info, "[T9VideoCaptureMethod] Running capture test...");

            try
            {
                // Test all API variants systematically
                bool success = false;

                // Priority 1: secvideo_api_capture_screen_video_only (most direct for our use case)
                if (!success) success = TestEntryPoint("secvideo_api_capture_screen_video_only", 1);

                // Priority 2: secvideo_api_capture_screen
                if (!success) success = TestEntryPoint("secvideo_api_capture_screen", 2);

                // Priority 3: ppi_video_capture_get_video_main_yuv (with lock/unlock)
                if (!success) success = TestEntryPoint("ppi_video_capture_get_video_main_yuv", 3);

                // Priority 4: ppi_video_capture_get_screen_post_yuv (with lock/unlock)
                if (!success) success = TestEntryPoint("ppi_video_capture_get_screen_post_yuv", 4);

                if (success)
                {
                    _isInitialized = true;
                    Helper.Log.Write(Helper.eLogType.Info, $"[T9VideoCaptureMethod] ✓ Test PASSED using: {_workingEntryPoint}");
                    return true;
                }
                else
                {
                    Helper.Log.Write(Helper.eLogType.Error, "[T9VideoCaptureMethod] ✗ Test FAILED - all entry points failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"[T9VideoCaptureMethod] Test exception: {ex.Message}");
                return false;
            }
        }

        private bool TestEntryPoint(string entryPointName, int priority)
        {
            Helper.Log.Write(Helper.eLogType.Info, $"[T9VideoCaptureMethod] Testing entry point #{priority}: {entryPointName}");

            try
            {
                // Allocate test buffers (small resolution for testing)
                int testWidth = 1920;
                int testHeight = 1080;
                int ySize = testWidth * testHeight;
                int uvSize = ySize / 2; // NV12 format

                IntPtr yBuffer = Marshal.AllocHGlobal(ySize);
                IntPtr uvBuffer = Marshal.AllocHGlobal(uvSize);

                try
                {
                    // Initialize input parameters
                    InputParams input = new InputParams
                    {
                        field0 = 0,
                        field1 = 0,
                        cropX = 0xffff,      // Full screen
                        cropY = 0xffff,      // Full screen
                        field4 = 1,
                        yBufferSize = ySize,
                        uvBufferSize = uvSize,
                        pYBuffer = yBuffer,
                        pUVBuffer = uvBuffer
                    };

                    OutputParams output = new OutputParams();

                    int result = -999; // Default failure

                    // Call appropriate entry point based on library version and name
                    switch (entryPointName)
                    {
                        case "secvideo_api_capture_screen_video_only":
                            result = CallSecvideoApiScreenVideoOnly(ref input, ref output);
                            break;

                        case "secvideo_api_capture_screen":
                            result = CallSecvideoApiScreen(ref input, ref output);
                            break;

                        case "ppi_video_capture_get_video_main_yuv":
                            ppi_video_capture_lock_global();
                            result = ppi_video_capture_get_video_main_yuv(ref input, ref output);
                            ppi_video_capture_unlock_global();
                            break;

                        case "ppi_video_capture_get_screen_post_yuv":
                            ppi_video_capture_lock_global();
                            result = ppi_video_capture_get_screen_post_yuv(ref input, ref output);
                            ppi_video_capture_unlock_global();
                            break;
                    }

                    Helper.Log.Write(Helper.eLogType.Info, $"[T9VideoCaptureMethod] {entryPointName} returned: {result}");

                    // Check success (0 or 4 are success codes)
                    bool isSuccess = (result == 0 || result == 4);

                    if (isSuccess && output.width > 0 && output.height > 0)
                    {
                        Helper.Log.Write(Helper.eLogType.Info, $"[T9VideoCaptureMethod] ✓ SUCCESS: {output.width}x{output.height}, Y size: {output.ySize}, UV size: {output.uvSize}");
                        _workingEntryPoint = entryPointName;
                        return true;
                    }
                    else if (result == -4)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, $"[T9VideoCaptureMethod] Error -4 (DRM protected content) - Try non-DRM source");
                        return false; // DRM content - not a successful test
                    }
                    else if (result == -95)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, $"[T9VideoCaptureMethod] Error -95 (Operation not supported) - API blocked on this firmware");
                        return false;
                    }
                    else
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, $"[T9VideoCaptureMethod] {entryPointName} failed or returned invalid data");
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(yBuffer);
                    Marshal.FreeHGlobal(uvBuffer);
                }
            }
            catch (DllNotFoundException)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"[T9VideoCaptureMethod] {entryPointName} - Library not found");
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"[T9VideoCaptureMethod] {entryPointName} - Entry point not found");
                return false;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"[T9VideoCaptureMethod] {entryPointName} exception: {ex.Message}");
                return false;
            }
        }

        private int CallSecvideoApiScreenVideoOnly(ref InputParams input, ref OutputParams output)
        {
            // Try all three library versions
            try
            {
                return secvideo_api_capture_screen_video_only_0_1_0(ref input, ref output);
            }
            catch
            {
                try
                {
                    return secvideo_api_capture_screen_video_only_0_1(ref input, ref output);
                }
                catch
                {
                    return secvideo_api_capture_screen_video_only(ref input, ref output);
                }
            }
        }

        private int CallSecvideoApiScreen(ref InputParams input, ref OutputParams output)
        {
            // Try all three library versions
            try
            {
                return secvideo_api_capture_screen_0_1_0(ref input, ref output);
            }
            catch
            {
                try
                {
                    return secvideo_api_capture_screen_0_1(ref input, ref output);
                }
                catch
                {
                    return secvideo_api_capture_screen(ref input, ref output);
                }
            }
        }

        public CaptureResult Capture(int width, int height)
        {
            if (!_isInitialized || string.IsNullOrEmpty(_workingEntryPoint))
            {
                return CaptureResult.CreateFailure("Not initialized - call Test() first");
            }

            try
            {
                // Allocate buffers
                int ySize = width * height;
                int uvSize = ySize / 2; // NV12 format

                IntPtr yBuffer = Marshal.AllocHGlobal(ySize);
                IntPtr uvBuffer = Marshal.AllocHGlobal(uvSize);

                try
                {
                    // Setup input parameters
                    InputParams input = new InputParams
                    {
                        field0 = 0,
                        field1 = 0,
                        cropX = 0xffff,      // Full screen
                        cropY = 0xffff,      // Full screen
                        field4 = 1,
                        yBufferSize = ySize,
                        uvBufferSize = uvSize,
                        pYBuffer = yBuffer,
                        pUVBuffer = uvBuffer
                    };

                    OutputParams output = new OutputParams();

                    int result = -999;

                    // Call the working entry point
                    switch (_workingEntryPoint)
                    {
                        case "secvideo_api_capture_screen_video_only":
                            result = CallSecvideoApiScreenVideoOnly(ref input, ref output);
                            break;

                        case "secvideo_api_capture_screen":
                            result = CallSecvideoApiScreen(ref input, ref output);
                            break;

                        case "ppi_video_capture_get_video_main_yuv":
                            ppi_video_capture_lock_global();
                            result = ppi_video_capture_get_video_main_yuv(ref input, ref output);
                            ppi_video_capture_unlock_global();
                            break;

                        case "ppi_video_capture_get_screen_post_yuv":
                            ppi_video_capture_lock_global();
                            result = ppi_video_capture_get_screen_post_yuv(ref input, ref output);
                            ppi_video_capture_unlock_global();
                            break;
                    }

                    // Check success
                    if ((result == 0 || result == 4) && output.width > 0 && output.height > 0)
                    {
                        // Copy data to managed arrays
                        byte[] yData = new byte[output.ySize];
                        byte[] uvData = new byte[output.uvSize];

                        Marshal.Copy(output.pYData, yData, 0, output.ySize);
                        Marshal.Copy(output.pUVData, uvData, 0, output.uvSize);

                        return CaptureResult.CreateSuccess(yData, uvData, output.width, output.height);
                    }
                    else if (result == -4)
                    {
                        return CaptureResult.CreateFailure("DRM protected content - cannot capture");
                    }
                    else if (result == -95)
                    {
                        return CaptureResult.CreateFailure("Operation not supported (firmware block)");
                    }
                    else
                    {
                        return CaptureResult.CreateFailure($"Capture failed with code: {result}");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(yBuffer);
                    Marshal.FreeHGlobal(uvBuffer);
                }
            }
            catch (Exception ex)
            {
                return CaptureResult.CreateFailure($"Exception: {ex.Message}");
            }
        }

        public void Cleanup()
        {
            Helper.Log.Write(Helper.eLogType.Info, "[T9VideoCaptureMethod] Cleanup");
            _isInitialized = false;
            _workingEntryPoint = null;
        }

        #endregion
    }
}
