using System;
using System.Runtime.InteropServices;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Tizen 9 display capture method using libdisplay-capture-api.so.0.0
    /// Provides synchronous capture with built-in YUV→RGB conversion
    /// Alternative to libvideo-capture.so
    /// </summary>
    public class T9DisplayCaptureMethod : ICaptureMethod
    {
        private bool _isInitialized = false;
        private const int RTLD_LAZY = 1;

        public string Name => "T9 Display Capture (libdisplay-capture-api.so)";
        public CaptureMethodType Type => CaptureMethodType.T9DisplayCapture;

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

        #region P/Invoke Declarations - libdisplay-capture-api.so

        // Main capture functions (C API)
        [DllImport("/usr/lib/libdisplay-capture-api.so.0.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "dc_request_capture_sync")]
        private static extern int dc_request_capture_sync_0_0(
            ref RequestData request,
            IntPtr yBuffer,
            IntPtr uvBuffer,
            int bufferSize);

        [DllImport("/usr/lib/libdisplay-capture-api.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "dc_request_capture_sync")]
        private static extern int dc_request_capture_sync_0(
            ref RequestData request,
            IntPtr yBuffer,
            IntPtr uvBuffer,
            int bufferSize);

        [DllImport("/usr/lib/libdisplay-capture-api.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "dc_request_capture_sync")]
        private static extern int dc_request_capture_sync(
            ref RequestData request,
            IntPtr yBuffer,
            IntPtr uvBuffer,
            int bufferSize);

        // Async variant (if sync doesn't work)
        [DllImport("/usr/lib/libdisplay-capture-api.so.0.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "dc_request_capture")]
        private static extern int dc_request_capture_0_0(
            ref RequestData request,
            IntPtr yBuffer,
            IntPtr uvBuffer,
            int bufferSize);

        [DllImport("/usr/lib/libdisplay-capture-api.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "dc_request_capture")]
        private static extern int dc_request_capture_0(
            ref RequestData request,
            IntPtr yBuffer,
            IntPtr uvBuffer,
            int bufferSize);

        [DllImport("/usr/lib/libdisplay-capture-api.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "dc_request_capture")]
        private static extern int dc_request_capture(
            ref RequestData request,
            IntPtr yBuffer,
            IntPtr uvBuffer,
            int bufferSize);

        #endregion

        #region Native Structs

        /// <summary>
        /// Request data for display capture
        /// Based on analysis of libdisplay-capture-api.so symbols
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct RequestData
        {
            public int width;               // Requested capture width
            public int height;              // Requested capture height
            public int format;              // Format (0 = YUV420, 1 = YUV422, etc.)
            public int mode;                // Capture mode
            public int reserved1;           // Reserved field
            public int reserved2;           // Reserved field
        }

        #endregion

        #region ICaptureMethod Implementation

        public bool IsAvailable()
        {
            Helper.Log.Write(Helper.eLogType.Info, "[T9DisplayCaptureMethod] Checking availability...");

            // Test library loading
            string[] libraryPaths = new string[]
            {
                "/usr/lib/libdisplay-capture-api.so.0.0",
                "/usr/lib/libdisplay-capture-api.so.0",
                "/usr/lib/libdisplay-capture-api.so"
            };

            foreach (var libPath in libraryPaths)
            {
                IntPtr handle = dlopen(libPath, RTLD_LAZY);
                if (handle != IntPtr.Zero)
                {
                    Helper.Log.Write(Helper.eLogType.Info, $"[T9DisplayCaptureMethod] Library loaded: {libPath}");

                    // Check for entry points
                    string[] entryPoints = new string[]
                    {
                        "dc_request_capture_sync",
                        "dc_request_capture"
                    };

                    bool foundEntryPoint = false;
                    foreach (var entryPoint in entryPoints)
                    {
                        IntPtr symbol = dlsym(handle, entryPoint);
                        if (symbol != IntPtr.Zero)
                        {
                            Helper.Log.Write(Helper.eLogType.Info, $"[T9DisplayCaptureMethod] ✓ Found entry point: {entryPoint}");
                            foundEntryPoint = true;
                        }
                    }

                    dlclose(handle);

                    if (foundEntryPoint)
                    {
                        Helper.Log.Write(Helper.eLogType.Info, "[T9DisplayCaptureMethod] Available!");
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
                    string errorMsg = $"[T9DisplayCaptureMethod] dlopen() failed for {libPath}";
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

            Helper.Log.Write(Helper.eLogType.Warning, "[T9DisplayCaptureMethod] Not available");
            return false;
        }

        public bool Test()
        {
            Helper.Log.Write(Helper.eLogType.Info, "[T9DisplayCaptureMethod] Running capture test...");

            try
            {
                // Test with small resolution
                int testWidth = 1920;
                int testHeight = 1080;
                int ySize = testWidth * testHeight;
                int uvSize = ySize / 2; // NV12 format
                int totalSize = ySize + uvSize;

                IntPtr buffer = Marshal.AllocHGlobal(totalSize);
                IntPtr yBuffer = buffer;
                IntPtr uvBuffer = IntPtr.Add(buffer, ySize);

                try
                {
                    RequestData request = new RequestData
                    {
                        width = testWidth,
                        height = testHeight,
                        format = 0,     // YUV420
                        mode = 0,       // Normal mode
                        reserved1 = 0,
                        reserved2 = 0
                    };

                    int result = CallDisplayCaptureSync(ref request, yBuffer, uvBuffer, totalSize);

                    Helper.Log.Write(Helper.eLogType.Info, $"[T9DisplayCaptureMethod] dc_request_capture_sync returned: {result}");

                    if (result == 0 || result == 4)
                    {
                        Helper.Log.Write(Helper.eLogType.Info, "[T9DisplayCaptureMethod] ✓ Test PASSED");
                        _isInitialized = true;
                        return true;
                    }
                    else if (result == -4)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, "[T9DisplayCaptureMethod] Error -4 (DRM protected content)");
                        return false;
                    }
                    else if (result == -95)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, "[T9DisplayCaptureMethod] Error -95 (Operation not supported)");
                        return false;
                    }
                    else
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, $"[T9DisplayCaptureMethod] ✗ Test FAILED with code: {result}");
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"[T9DisplayCaptureMethod] Test exception: {ex.Message}");
                return false;
            }
        }

        private int CallDisplayCaptureSync(ref RequestData request, IntPtr yBuffer, IntPtr uvBuffer, int bufferSize)
        {
            // Try all three library versions
            try
            {
                return dc_request_capture_sync_0_0(ref request, yBuffer, uvBuffer, bufferSize);
            }
            catch
            {
                try
                {
                    return dc_request_capture_sync_0(ref request, yBuffer, uvBuffer, bufferSize);
                }
                catch
                {
                    return dc_request_capture_sync(ref request, yBuffer, uvBuffer, bufferSize);
                }
            }
        }

        public CaptureResult Capture(int width, int height)
        {
            if (!_isInitialized)
            {
                return CaptureResult.CreateFailure("Not initialized - call Test() first");
            }

            try
            {
                int ySize = width * height;
                int uvSize = ySize / 2; // NV12 format
                int totalSize = ySize + uvSize;

                IntPtr buffer = Marshal.AllocHGlobal(totalSize);
                IntPtr yBuffer = buffer;
                IntPtr uvBuffer = IntPtr.Add(buffer, ySize);

                try
                {
                    RequestData request = new RequestData
                    {
                        width = width,
                        height = height,
                        format = 0,     // YUV420
                        mode = 0,       // Normal mode
                        reserved1 = 0,
                        reserved2 = 0
                    };

                    int result = CallDisplayCaptureSync(ref request, yBuffer, uvBuffer, totalSize);

                    if (result == 0 || result == 4)
                    {
                        // Copy to managed arrays
                        byte[] yData = new byte[ySize];
                        byte[] uvData = new byte[uvSize];

                        Marshal.Copy(yBuffer, yData, 0, ySize);
                        Marshal.Copy(uvBuffer, uvData, 0, uvSize);

                        return CaptureResult.CreateSuccess(yData, uvData, width, height);
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
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                return CaptureResult.CreateFailure($"Exception: {ex.Message}");
            }
        }

        public void Cleanup()
        {
            Helper.Log.Write(Helper.eLogType.Info, "[T9DisplayCaptureMethod] Cleanup");
            _isInitialized = false;
        }

        #endregion
    }
}
