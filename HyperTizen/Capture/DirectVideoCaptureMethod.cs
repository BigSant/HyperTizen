using System;
using System.Runtime.InteropServices;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Experimental capture method attempting direct access to libvideo-capture.so.
    /// This library is known to be BLOCKED by libtzcapturec.so on most Tizen 9 firmware,
    /// but we attempt to load it anyway with various strategies for diagnostic purposes.
    /// Uses ppi_video_capture_* and secvideo_api_* symbols.
    /// </summary>
    public class DirectVideoCaptureMethod : ICaptureMethod
    {
        private const string TAG = "[DirectVideoCapture]";
        private const int RTLD_LAZY = 1;
        private const int RTLD_NODELETE = 0x1000;
        private const int OUTPUT_WIDTH = 64;
        private const int OUTPUT_HEIGHT = 48;

        private IntPtr _libHandle = IntPtr.Zero;
        private bool _isInitialized = false;
        private string _loadedLibPath = null;

        // Resolved function pointers
        private IntPtr _ptrLockGlobal = IntPtr.Zero;
        private IntPtr _ptrUnlockGlobal = IntPtr.Zero;
        private IntPtr _ptrGetScreenPostYuv = IntPtr.Zero;
        private IntPtr _ptrGetVideoMainYuv = IntPtr.Zero;
        private IntPtr _ptrIsProtectCapture = IntPtr.Zero;
        private IntPtr _ptrSecvideoApiCaptureScreen = IntPtr.Zero;

        private string _workingEntryPoint = null;

        public string Name => "Direct Video Capture (libvideo-capture.so - bypass attempt)";
        public CaptureMethodType Type => CaptureMethodType.DirectVideoCapture;

        #region P/Invoke - Dynamic Library Loading

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlopen(string filename, int flags);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int dlclose(IntPtr handle);

        [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr dlerror();

        #endregion

        #region Native Structs

        /// <summary>
        /// Input parameters for video capture (matches T9VideoCaptureMethod.InputParams)
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct InputParams
        {
            public int field0;          // Usually 0
            public int field1;          // Usually 0
            public int cropX;           // Crop X (use 0xffff for full screen)
            public int cropY;           // Crop Y (use 0xffff for full screen)
            public int field4;          // Usually 1
            public int yBufferSize;     // Y buffer size
            public int uvBufferSize;    // UV buffer size
            public IntPtr pYBuffer;     // Pointer to Y buffer
            public IntPtr pUVBuffer;    // Pointer to UV buffer
        }

        /// <summary>
        /// Output parameters from video capture (matches T9VideoCaptureMethod.OutputParams)
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct OutputParams
        {
            public int width;
            public int height;
            public int field2;
            public int field3;
            public int ySize;
            public int uvSize;
            public IntPtr pYData;
            public IntPtr pUVData;
        }

        #endregion

        #region Delegate Signatures

        // Lock/Unlock: int func()
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int LockUnlockDelegate();

        // Capture: int func(ref InputParams, ref OutputParams)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CaptureDelegate(ref InputParams input, ref OutputParams output);

        // Is protect: int func(out int isProtected)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int IsProtectCaptureDelegate(out int isProtected);

        // secvideo_api_capture_screen - Variant A: int func(ref InputParams, ref OutputParams)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SecvideoApiCaptureScreenA(ref InputParams input, ref OutputParams output);

        // secvideo_api_capture_screen - Variant B: int func(IntPtr params)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SecvideoApiCaptureScreenB(IntPtr paramsPtr);

        // secvideo_api_capture_screen - Variant C: int func(int width, int height, IntPtr buffer, int bufSize)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SecvideoApiCaptureScreenC(int width, int height, IntPtr buffer, int bufSize);

        #endregion

        #region Helper Methods

        private string GetDlError()
        {
            IntPtr errorPtr = dlerror();
            return errorPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(errorPtr) : "unknown error";
        }

        private IntPtr ResolveSymbol(IntPtr handle, string symbolName)
        {
            IntPtr ptr = dlsym(handle, symbolName);
            if (ptr != IntPtr.Zero)
            {
                Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Resolved symbol: {symbolName} -> 0x{ptr.ToInt64():X}");
            }
            else
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} Failed to resolve symbol: {symbolName} - {GetDlError()}");
            }
            return ptr;
        }

        private IntPtr TryDlOpen(string path, int flags, string description)
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Attempting dlopen: {path} flags=0x{flags:X} ({description})");

            // Clear any previous error
            dlerror();

            IntPtr handle = dlopen(path, flags);
            if (handle != IntPtr.Zero)
            {
                Helper.Log.Write(Helper.eLogType.Info, $"{TAG} SUCCESS: dlopen({path}) = 0x{handle.ToInt64():X}");
            }
            else
            {
                string error = GetDlError();
                bool fileExists = System.IO.File.Exists(path);
                Helper.Log.Write(Helper.eLogType.Warning,
                    $"{TAG} FAILED: dlopen({path}) - exists={fileExists} error={error}");
            }

            return handle;
        }

        #endregion

        #region ICaptureMethod Implementation

        public bool IsAvailable()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Checking availability...");
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} NOTE: This library is known to be blocked by libtzcapturec.so on most firmware.");
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Attempting multiple loading strategies for diagnostic purposes...");

            // Strategy 1: Standard RTLD_LAZY with versioned path
            string[] libPaths = new string[]
            {
                "/usr/lib/libvideo-capture.so.0.1.0",
                "/usr/lib/libvideo-capture.so.0.1",
                "/usr/lib/libvideo-capture.so.0",
                "/usr/lib/libvideo-capture.so",
                "libvideo-capture.so.0.1.0",
                "libvideo-capture.so"
            };

            foreach (var libPath in libPaths)
            {
                // Strategy 1: RTLD_LAZY
                _libHandle = TryDlOpen(libPath, RTLD_LAZY, "RTLD_LAZY");
                if (_libHandle != IntPtr.Zero)
                {
                    _loadedLibPath = libPath;
                    break;
                }

                // Strategy 2: RTLD_LAZY | RTLD_NODELETE
                _libHandle = TryDlOpen(libPath, RTLD_LAZY | RTLD_NODELETE, "RTLD_LAZY | RTLD_NODELETE");
                if (_libHandle != IntPtr.Zero)
                {
                    _loadedLibPath = libPath;
                    break;
                }
            }

            if (_libHandle == IntPtr.Zero)
            {
                Helper.Log.Write(Helper.eLogType.Warning,
                    $"{TAG} All dlopen attempts failed. Library is likely blocked by libtzcapturec.so security policy.");
                Helper.Log.Write(Helper.eLogType.Warning,
                    $"{TAG} This is expected on most Tizen 9 firmware. The T9VideoCaptureMethod uses DllImport which may bypass this.");
                return false;
            }

            // Resolve symbols
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Library loaded! Resolving symbols...");

            _ptrLockGlobal = ResolveSymbol(_libHandle, "ppi_video_capture_lock_global");
            _ptrUnlockGlobal = ResolveSymbol(_libHandle, "ppi_video_capture_unlock_global");
            _ptrGetScreenPostYuv = ResolveSymbol(_libHandle, "ppi_video_capture_get_screen_post_yuv");
            _ptrGetVideoMainYuv = ResolveSymbol(_libHandle, "ppi_video_capture_get_video_main_yuv");
            _ptrIsProtectCapture = ResolveSymbol(_libHandle, "ppi_video_capture_is_protect_capture");
            _ptrSecvideoApiCaptureScreen = ResolveSymbol(_libHandle, "secvideo_api_capture_screen");

            bool hasCapture = _ptrGetScreenPostYuv != IntPtr.Zero ||
                              _ptrGetVideoMainYuv != IntPtr.Zero ||
                              _ptrSecvideoApiCaptureScreen != IntPtr.Zero;

            if (!hasCapture)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} No capture entry points found despite library loading");
                dlclose(_libHandle);
                _libHandle = IntPtr.Zero;
                return false;
            }

            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Available - library loaded via {_loadedLibPath}");
            return true;
        }

        public bool Test()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Running capture test...");

            if (_libHandle == IntPtr.Zero)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"{TAG} Library not loaded");
                return false;
            }

            // Check DRM protection first
            if (_ptrIsProtectCapture != IntPtr.Zero)
            {
                try
                {
                    var isProtect = Marshal.GetDelegateForFunctionPointer<IsProtectCaptureDelegate>(_ptrIsProtectCapture);
                    int isProtected = 0;
                    int protResult = isProtect(out isProtected);
                    Helper.Log.Write(Helper.eLogType.Info,
                        $"{TAG} is_protect_capture() returned: {protResult}, isProtected={isProtected}");

                    if (isProtected == 1)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning,
                            $"{TAG} Content is DRM protected - capture may fail with error -4");
                    }
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} is_protect_capture failed: {ex.Message}");
                }
            }

            // Lock global if available
            bool locked = false;
            if (_ptrLockGlobal != IntPtr.Zero)
            {
                try
                {
                    var lockFunc = Marshal.GetDelegateForFunctionPointer<LockUnlockDelegate>(_ptrLockGlobal);
                    int lockResult = lockFunc();
                    Helper.Log.Write(Helper.eLogType.Info, $"{TAG} lock_global() returned: {lockResult}");
                    locked = (lockResult == 0);
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} lock_global failed: {ex.Message}");
                }
            }

            bool success = false;

            try
            {
                // Try ppi_video_capture_get_screen_post_yuv first (the "holy grail")
                if (!success && _ptrGetScreenPostYuv != IntPtr.Zero)
                {
                    success = TestCaptureEntry("ppi_video_capture_get_screen_post_yuv", _ptrGetScreenPostYuv);
                }

                // Try ppi_video_capture_get_video_main_yuv
                if (!success && _ptrGetVideoMainYuv != IntPtr.Zero)
                {
                    success = TestCaptureEntry("ppi_video_capture_get_video_main_yuv", _ptrGetVideoMainYuv);
                }

                // Try secvideo_api_capture_screen with multiple signatures
                if (!success && _ptrSecvideoApiCaptureScreen != IntPtr.Zero)
                {
                    success = TestSecvideoApiCaptureScreen();
                }
            }
            finally
            {
                // Unlock global if we locked
                if (locked && _ptrUnlockGlobal != IntPtr.Zero)
                {
                    try
                    {
                        var unlockFunc = Marshal.GetDelegateForFunctionPointer<LockUnlockDelegate>(_ptrUnlockGlobal);
                        int unlockResult = unlockFunc();
                        Helper.Log.Write(Helper.eLogType.Info, $"{TAG} unlock_global() returned: {unlockResult}");
                    }
                    catch (Exception ex)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} unlock_global failed: {ex.Message}");
                    }
                }
            }

            if (success)
            {
                _isInitialized = true;
                Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Test PASSED using: {_workingEntryPoint}");
                return true;
            }

            Helper.Log.Write(Helper.eLogType.Error, $"{TAG} Test FAILED - all capture attempts failed");
            return false;
        }

        private bool TestCaptureEntry(string name, IntPtr funcPtr)
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Testing {name}...");

            int testWidth = 1920;
            int testHeight = 1080;
            int ySize = testWidth * testHeight;
            int uvSize = ySize / 2;

            IntPtr yBuffer = IntPtr.Zero;
            IntPtr uvBuffer = IntPtr.Zero;

            try
            {
                yBuffer = Marshal.AllocHGlobal(ySize);
                uvBuffer = Marshal.AllocHGlobal(uvSize);

                InputParams input = new InputParams
                {
                    field0 = 0,
                    field1 = 0,
                    cropX = 0xffff,
                    cropY = 0xffff,
                    field4 = 1,
                    yBufferSize = ySize,
                    uvBufferSize = uvSize,
                    pYBuffer = yBuffer,
                    pUVBuffer = uvBuffer
                };

                OutputParams output = new OutputParams();

                var captureFunc = Marshal.GetDelegateForFunctionPointer<CaptureDelegate>(funcPtr);
                int result = captureFunc(ref input, ref output);

                Helper.Log.Write(Helper.eLogType.Info,
                    $"{TAG}   {name} returned: {result}");
                Helper.Log.Write(Helper.eLogType.Info,
                    $"{TAG}   output: w={output.width}, h={output.height}, ySize={output.ySize}, uvSize={output.uvSize}");
                Helper.Log.Write(Helper.eLogType.Info,
                    $"{TAG}   output ptrs: pYData=0x{output.pYData.ToInt64():X}, pUVData=0x{output.pUVData.ToInt64():X}");

                if ((result == 0 || result == 4) && output.width > 0 && output.height > 0)
                {
                    _workingEntryPoint = name;
                    return true;
                }
                else if (result == -4)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG}   Error -4: DRM protected content");
                }
                else if (result == -95)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG}   Error -95: Operation not supported (firmware block)");
                }
                else if (result == -1)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG}   Error -1: General failure (likely permission denied)");
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"{TAG}   {name} exception: {ex.Message}");
            }
            finally
            {
                if (yBuffer != IntPtr.Zero) Marshal.FreeHGlobal(yBuffer);
                if (uvBuffer != IntPtr.Zero) Marshal.FreeHGlobal(uvBuffer);
            }

            return false;
        }

        private bool TestSecvideoApiCaptureScreen()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Testing secvideo_api_capture_screen with multiple signatures...");

            // Variant A: Same signature as ppi capture functions
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG}   Trying Variant A: int func(ref InputParams, ref OutputParams)");
            if (TestCaptureEntry("secvideo_api_capture_screen", _ptrSecvideoApiCaptureScreen))
            {
                return true;
            }

            // Variant C: int func(int width, int height, IntPtr buffer, int bufSize)
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG}   Trying Variant C: int func(int w, int h, IntPtr buf, int size)");
            int bufSize = 1920 * 1080 * 3 / 2;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(bufSize);

                var captureC = Marshal.GetDelegateForFunctionPointer<SecvideoApiCaptureScreenC>(_ptrSecvideoApiCaptureScreen);
                int result = captureC(1920, 1080, buffer, bufSize);
                Helper.Log.Write(Helper.eLogType.Info, $"{TAG}     Variant C returned: {result}");

                if (result == 0 || result == 4)
                {
                    _workingEntryPoint = "secvideo_api_capture_screen_varC";
                    return true;
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"{TAG}     Variant C exception: {ex.Message}");
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }

            return false;
        }

        public CaptureResult Capture(int width, int height)
        {
            if (!_isInitialized || string.IsNullOrEmpty(_workingEntryPoint))
            {
                return CaptureResult.CreateFailure("DirectVideoCapture not initialized - call Test() first");
            }

            try
            {
                int outW = width > 0 ? width : OUTPUT_WIDTH;
                int outH = height > 0 ? height : OUTPUT_HEIGHT;

                Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Capture {outW}x{outH} using: {_workingEntryPoint}");

                // Lock global
                bool locked = false;
                if (_ptrLockGlobal != IntPtr.Zero)
                {
                    try
                    {
                        var lockFunc = Marshal.GetDelegateForFunctionPointer<LockUnlockDelegate>(_ptrLockGlobal);
                        int lockResult = lockFunc();
                        locked = (lockResult == 0);
                    }
                    catch { }
                }

                try
                {
                    if (_workingEntryPoint == "secvideo_api_capture_screen_varC")
                    {
                        return CaptureWithVariantC(outW, outH);
                    }
                    else
                    {
                        return CaptureWithStandardParams(outW, outH);
                    }
                }
                finally
                {
                    // Unlock global
                    if (locked && _ptrUnlockGlobal != IntPtr.Zero)
                    {
                        try
                        {
                            var unlockFunc = Marshal.GetDelegateForFunctionPointer<LockUnlockDelegate>(_ptrUnlockGlobal);
                            unlockFunc();
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"{TAG} Capture exception: {ex.Message}");
                return CaptureResult.CreateFailure($"Exception: {ex.Message}");
            }
        }

        private CaptureResult CaptureWithStandardParams(int outW, int outH)
        {
            int captureW = 1920;
            int captureH = 1080;
            int ySize = captureW * captureH;
            int uvSize = ySize / 2;

            IntPtr yBuffer = IntPtr.Zero;
            IntPtr uvBuffer = IntPtr.Zero;

            try
            {
                yBuffer = Marshal.AllocHGlobal(ySize);
                uvBuffer = Marshal.AllocHGlobal(uvSize);

                InputParams input = new InputParams
                {
                    field0 = 0,
                    field1 = 0,
                    cropX = 0xffff,
                    cropY = 0xffff,
                    field4 = 1,
                    yBufferSize = ySize,
                    uvBufferSize = uvSize,
                    pYBuffer = yBuffer,
                    pUVBuffer = uvBuffer
                };

                OutputParams output = new OutputParams();

                IntPtr funcPtr = IntPtr.Zero;
                if (_workingEntryPoint == "ppi_video_capture_get_screen_post_yuv")
                    funcPtr = _ptrGetScreenPostYuv;
                else if (_workingEntryPoint == "ppi_video_capture_get_video_main_yuv")
                    funcPtr = _ptrGetVideoMainYuv;
                else if (_workingEntryPoint == "secvideo_api_capture_screen")
                    funcPtr = _ptrSecvideoApiCaptureScreen;
                else
                    return CaptureResult.CreateFailure($"Unknown entry point: {_workingEntryPoint}");

                var captureFunc = Marshal.GetDelegateForFunctionPointer<CaptureDelegate>(funcPtr);
                int result = captureFunc(ref input, ref output);

                if ((result == 0 || result == 4) && output.width > 0 && output.height > 0)
                {
                    // Copy and downscale
                    int srcYSize = output.ySize > 0 ? output.ySize : output.width * output.height;
                    int srcUVSize = output.uvSize > 0 ? output.uvSize : srcYSize / 2;

                    IntPtr srcYPtr = output.pYData != IntPtr.Zero ? output.pYData : yBuffer;
                    IntPtr srcUVPtr = output.pUVData != IntPtr.Zero ? output.pUVData : uvBuffer;

                    byte[] srcY = new byte[srcYSize];
                    byte[] srcUV = new byte[srcUVSize];
                    Marshal.Copy(srcYPtr, srcY, 0, srcYSize);
                    Marshal.Copy(srcUVPtr, srcUV, 0, srcUVSize);

                    // Downscale
                    byte[] yData = new byte[outW * outH];
                    byte[] uvData = new byte[outW * outH / 2];

                    int srcWidth = output.width;
                    int srcHeight = output.height;

                    for (int y = 0; y < outH; y++)
                    {
                        int srcRow = y * srcHeight / outH;
                        for (int x = 0; x < outW; x++)
                        {
                            int srcCol = x * srcWidth / outW;
                            int srcIdx = srcRow * srcWidth + srcCol;
                            if (srcIdx < srcY.Length)
                                yData[y * outW + x] = srcY[srcIdx];
                        }
                    }

                    int uvOutH = outH / 2;
                    for (int y = 0; y < uvOutH; y++)
                    {
                        int srcUVRow = y * (srcHeight / 2) / uvOutH;
                        for (int x = 0; x < outW; x += 2)
                        {
                            int srcUVCol = (x * srcWidth / outW) & ~1;
                            int sIdx = srcUVRow * srcWidth + srcUVCol;
                            int dIdx = y * outW + x;

                            if (sIdx + 1 < srcUV.Length && dIdx + 1 < uvData.Length)
                            {
                                uvData[dIdx] = srcUV[sIdx];
                                uvData[dIdx + 1] = srcUV[sIdx + 1];
                            }
                        }
                    }

                    return CaptureResult.CreateSuccess(yData, uvData, outW, outH);
                }
                else if (result == -4)
                {
                    return CaptureResult.CreateFailure("DRM protected content - cannot capture");
                }
                else
                {
                    return CaptureResult.CreateFailure($"Capture failed with code: {result}");
                }
            }
            finally
            {
                if (yBuffer != IntPtr.Zero) Marshal.FreeHGlobal(yBuffer);
                if (uvBuffer != IntPtr.Zero) Marshal.FreeHGlobal(uvBuffer);
            }
        }

        private CaptureResult CaptureWithVariantC(int outW, int outH)
        {
            int captureW = 1920;
            int captureH = 1080;
            int bufSize = captureW * captureH * 3 / 2;

            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(bufSize);

                var captureC = Marshal.GetDelegateForFunctionPointer<SecvideoApiCaptureScreenC>(_ptrSecvideoApiCaptureScreen);
                int result = captureC(captureW, captureH, buffer, bufSize);

                if (result == 0 || result == 4)
                {
                    int srcYSize = captureW * captureH;

                    byte[] srcY = new byte[srcYSize];
                    byte[] srcUV = new byte[srcYSize / 2];
                    Marshal.Copy(buffer, srcY, 0, srcYSize);
                    Marshal.Copy(buffer + srcYSize, srcUV, 0, srcYSize / 2);

                    // Downscale
                    byte[] yData = new byte[outW * outH];
                    byte[] uvData = new byte[outW * outH / 2];

                    for (int y = 0; y < outH; y++)
                    {
                        int srcRow = y * captureH / outH;
                        for (int x = 0; x < outW; x++)
                        {
                            int srcCol = x * captureW / outW;
                            yData[y * outW + x] = srcY[srcRow * captureW + srcCol];
                        }
                    }

                    int uvOutH = outH / 2;
                    for (int y = 0; y < uvOutH; y++)
                    {
                        int srcUVRow = y * (captureH / 2) / uvOutH;
                        for (int x = 0; x < outW; x += 2)
                        {
                            int srcUVCol = (x * captureW / outW) & ~1;
                            int sIdx = srcUVRow * captureW + srcUVCol;
                            int dIdx = y * outW + x;

                            if (sIdx + 1 < srcUV.Length && dIdx + 1 < uvData.Length)
                            {
                                uvData[dIdx] = srcUV[sIdx];
                                uvData[dIdx + 1] = srcUV[sIdx + 1];
                            }
                        }
                    }

                    return CaptureResult.CreateSuccess(yData, uvData, outW, outH);
                }
                else
                {
                    return CaptureResult.CreateFailure($"secvideo_api_capture_screen (varC) failed with code: {result}");
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        public void Cleanup()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Cleanup");

            if (_libHandle != IntPtr.Zero)
            {
                dlclose(_libHandle);
                _libHandle = IntPtr.Zero;
            }

            _isInitialized = false;
            _loadedLibPath = null;
            _workingEntryPoint = null;
            _ptrLockGlobal = IntPtr.Zero;
            _ptrUnlockGlobal = IntPtr.Zero;
            _ptrGetScreenPostYuv = IntPtr.Zero;
            _ptrGetVideoMainYuv = IntPtr.Zero;
            _ptrIsProtectCapture = IntPtr.Zero;
            _ptrSecvideoApiCaptureScreen = IntPtr.Zero;
        }

        #endregion
    }
}
