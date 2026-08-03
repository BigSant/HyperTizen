using System;
using System.Runtime.InteropServices;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Experimental capture method using libvideo-dp-control.so framebuffer API.
    /// Discovered via reverse engineering ELF symbol dumps of Samsung Tizen 9 firmware.
    /// Uses ppi_video_dp_control_get_frame_buffer_* symbols for direct framebuffer access.
    /// </summary>
    public class FrameBufferCaptureMethod : ICaptureMethod
    {
        private const string TAG = "[FrameBufferCapture]";
        private const int RTLD_LAZY = 1;
        private const string LIB_PATH = "/usr/lib/libvideo-dp-control.so";
        private const int OUTPUT_WIDTH = 64;
        private const int OUTPUT_HEIGHT = 48;

        private IntPtr _libHandle = IntPtr.Zero;
        private bool _isInitialized = false;
        private int _workingPath = -1;
        private string _workingPropFunc = null;
        private int _fbWidth = 0;
        private int _fbHeight = 0;

        // Resolved function pointers
        private IntPtr _ptrGetFrameBufferProp = IntPtr.Zero;
        private IntPtr _ptrGetFrameBufferProperty = IntPtr.Zero;
        private IntPtr _ptrGetFrameBufferSize = IntPtr.Zero;
        private IntPtr _ptrGetFramerate = IntPtr.Zero;

        public string Name => "FrameBuffer Capture (libvideo-dp-control)";
        public CaptureMethodType Type => CaptureMethodType.FrameBufferCapture;

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
        /// Framebuffer property structure - guessed layout based on PPI conventions.
        /// Padded to 128 bytes to accommodate unknown fields.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct FrameBufferPropStruct
        {
            public int width;
            public int height;
            public int stride;
            public int format;          // pixel format (NV12=0x15, ARGB=0x00, etc)
            public IntPtr bufferAddr;   // physical or virtual address of framebuffer
            public int bufferSize;
            public int field6;
            public int field7;
            public int field8;
            public int field9;
            public int field10;
            public int field11;
            public int field12;
            public int field13;
            public int field14;
            public int field15;
            // 64 bytes total (16 ints on 32-bit)
        }

        /// <summary>
        /// Framebuffer size output structure
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct FrameBufferSizeStruct
        {
            public int width;
            public int height;
            public int bufferSize;
            public int format;
        }

        #endregion

        #region Delegate Signatures

        // ppi_video_dp_control_get_frame_buffer_prop(int path, IntPtr propStruct)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetFrameBufferPropDelegate(int path, IntPtr propStruct);

        // ppi_video_dp_control_get_frame_buffer_property(int path, IntPtr propStruct)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetFrameBufferPropertyDelegate(int path, IntPtr propStruct);

        // ppi_video_dp_control_get_frame_buffer_size - Variant A: int func(int path, out int width, out int height)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetFrameBufferSizeVariantA(int path, out int width, out int height);

        // ppi_video_dp_control_get_frame_buffer_size - Variant B: int func(int path, IntPtr sizeStruct)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetFrameBufferSizeVariantB(int path, IntPtr sizeStruct);

        // ppi_video_dp_control_get_framerate(int path, out int fps)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetFramerateDelegate(int path, out int fps);

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

        #endregion

        #region ICaptureMethod Implementation

        public bool IsAvailable()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Checking availability...");

            try
            {
                _libHandle = dlopen(LIB_PATH, RTLD_LAZY);
                if (_libHandle == IntPtr.Zero)
                {
                    string error = GetDlError();
                    bool fileExists = System.IO.File.Exists(LIB_PATH);
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"{TAG} dlopen failed for {LIB_PATH} - exists={fileExists} error={error}");
                    return false;
                }

                Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Library loaded: {LIB_PATH} handle=0x{_libHandle.ToInt64():X}");

                // Resolve all symbols
                _ptrGetFrameBufferProp = ResolveSymbol(_libHandle, "ppi_video_dp_control_get_frame_buffer_prop");
                _ptrGetFrameBufferProperty = ResolveSymbol(_libHandle, "ppi_video_dp_control_get_frame_buffer_property");
                _ptrGetFrameBufferSize = ResolveSymbol(_libHandle, "ppi_video_dp_control_get_frame_buffer_size");
                _ptrGetFramerate = ResolveSymbol(_libHandle, "ppi_video_dp_control_get_framerate");

                // We need at least one property/size function
                bool hasAnyUseful = _ptrGetFrameBufferProp != IntPtr.Zero ||
                                    _ptrGetFrameBufferProperty != IntPtr.Zero ||
                                    _ptrGetFrameBufferSize != IntPtr.Zero;

                if (!hasAnyUseful)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} No useful symbols found");
                    dlclose(_libHandle);
                    _libHandle = IntPtr.Zero;
                    return false;
                }

                // DO NOT call any functions — unknown signatures/struct layouts cause segfault.
                Helper.Log.Write(Helper.eLogType.Info, $"{TAG} All symbols resolved (dlopen+dlsym OK). Function calls DISABLED to prevent crash.");
                Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Returning NOT available — need correct function signatures before calling.");
                return false;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"{TAG} IsAvailable exception: {ex.Message}");
                return false;
            }
        }

        public bool Test()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Running capture test...");

            if (_libHandle == IntPtr.Zero)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"{TAG} Library not loaded");
                return false;
            }

            // Try framerate first to see if API is responsive
            if (_ptrGetFramerate != IntPtr.Zero)
            {
                try
                {
                    var getFramerate = Marshal.GetDelegateForFunctionPointer<GetFramerateDelegate>(_ptrGetFramerate);
                    for (int path = 0; path <= 2; path++)
                    {
                        int fps = 0;
                        int result = getFramerate(path, out fps);
                        Helper.Log.Write(Helper.eLogType.Info, $"{TAG} get_framerate(path={path}) returned: {result}, fps={fps}");
                    }
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} get_framerate failed: {ex.Message}");
                }
            }

            // Try get_frame_buffer_size to get dimensions
            bool gotDimensions = false;

            if (_ptrGetFrameBufferSize != IntPtr.Zero)
            {
                gotDimensions = TestGetFrameBufferSize();
            }

            // get_frame_buffer_prop/property with struct pointer DISABLED — unknown struct layout causes segfault.
            // These will only be tried if we find the correct struct layout via further RE.
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Struct-based prop calls skipped (crash risk)");

            if (gotDimensions)
            {
                _isInitialized = true;
                Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Test PASSED - fb dimensions: {_fbWidth}x{_fbHeight}, path={_workingPath}");
                return true;
            }

            Helper.Log.Write(Helper.eLogType.Error, $"{TAG} Test FAILED - could not determine framebuffer dimensions");
            return false;
        }

        private bool TestGetFrameBufferSize()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Testing get_frame_buffer_size...");

            // Try Variant A: int func(int path, out int width, out int height)
            for (int path = 0; path <= 2; path++)
            {
                try
                {
                    var getSizeA = Marshal.GetDelegateForFunctionPointer<GetFrameBufferSizeVariantA>(_ptrGetFrameBufferSize);
                    int w = 0, h = 0;
                    int result = getSizeA(path, out w, out h);
                    Helper.Log.Write(Helper.eLogType.Info,
                        $"{TAG}   get_frame_buffer_size(path={path}) [VariantA] returned: {result}, w={w}, h={h}");

                    if (result == 0 && w > 0 && h > 0 && w <= 7680 && h <= 4320)
                    {
                        _fbWidth = w;
                        _fbHeight = h;
                        _workingPath = path;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG}   VariantA path={path} exception: {ex.Message}");
                }
            }

            // VariantB (struct-based) DISABLED — caused segfault crash on Tizen 9.
            // The function likely expects (int, int*, int*) not (int, struct*).
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG}   VariantB skipped (known crash risk)");

            return false;
        }

        private bool TestGetFrameBufferProp(string funcName, IntPtr funcPtr)
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Testing {funcName}...");

            for (int path = 0; path <= 2; path++)
            {
                IntPtr structPtr = IntPtr.Zero;
                try
                {
                    int structSize = Marshal.SizeOf<FrameBufferPropStruct>();
                    structPtr = Marshal.AllocHGlobal(structSize);

                    // Zero-initialize
                    for (int i = 0; i < structSize; i++)
                        Marshal.WriteByte(structPtr, i, 0);

                    var getProp = Marshal.GetDelegateForFunctionPointer<GetFrameBufferPropDelegate>(funcPtr);
                    int result = getProp(path, structPtr);

                    var propData = Marshal.PtrToStructure<FrameBufferPropStruct>(structPtr);

                    Helper.Log.Write(Helper.eLogType.Info,
                        $"{TAG}   {funcName}(path={path}) returned: {result}");
                    Helper.Log.Write(Helper.eLogType.Info,
                        $"{TAG}     width={propData.width}, height={propData.height}, stride={propData.stride}, format={propData.format}");
                    Helper.Log.Write(Helper.eLogType.Info,
                        $"{TAG}     bufferAddr=0x{propData.bufferAddr.ToInt64():X}, bufferSize={propData.bufferSize}");
                    Helper.Log.Write(Helper.eLogType.Info,
                        $"{TAG}     fields: {propData.field6},{propData.field7},{propData.field8},{propData.field9},{propData.field10},{propData.field11},{propData.field12},{propData.field13},{propData.field14},{propData.field15}");

                    if (result == 0 && propData.width > 0 && propData.height > 0 && propData.width <= 7680 && propData.height <= 4320)
                    {
                        _fbWidth = propData.width;
                        _fbHeight = propData.height;
                        _workingPath = path;
                        _workingPropFunc = funcName;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG}   {funcName} path={path} exception: {ex.Message}");
                }
                finally
                {
                    if (structPtr != IntPtr.Zero) Marshal.FreeHGlobal(structPtr);
                }
            }

            return false;
        }

        public CaptureResult Capture(int width, int height)
        {
            if (!_isInitialized || _workingPath < 0)
            {
                return CaptureResult.CreateFailure("FrameBufferCapture not initialized - call Test() first");
            }

            try
            {
                int outW = width > 0 ? width : OUTPUT_WIDTH;
                int outH = height > 0 ? height : OUTPUT_HEIGHT;

                Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Capture {outW}x{outH} from path={_workingPath}");

                // Get framebuffer property to find buffer address
                IntPtr funcPtr = _ptrGetFrameBufferProp != IntPtr.Zero ? _ptrGetFrameBufferProp : _ptrGetFrameBufferProperty;

                if (funcPtr == IntPtr.Zero)
                {
                    return CaptureResult.CreateFailure("No framebuffer property function available");
                }

                int structSize = Marshal.SizeOf<FrameBufferPropStruct>();
                IntPtr structPtr = Marshal.AllocHGlobal(structSize);
                try
                {
                    for (int i = 0; i < structSize; i++)
                        Marshal.WriteByte(structPtr, i, 0);

                    var getProp = Marshal.GetDelegateForFunctionPointer<GetFrameBufferPropDelegate>(funcPtr);
                    int result = getProp(_workingPath, structPtr);

                    if (result != 0)
                    {
                        return CaptureResult.CreateFailure($"get_frame_buffer_prop returned error: {result}");
                    }

                    var propData = Marshal.PtrToStructure<FrameBufferPropStruct>(structPtr);

                    if (propData.bufferAddr == IntPtr.Zero)
                    {
                        return CaptureResult.CreateFailure("Framebuffer address is null");
                    }

                    if (propData.width <= 0 || propData.height <= 0)
                    {
                        return CaptureResult.CreateFailure($"Invalid framebuffer dimensions: {propData.width}x{propData.height}");
                    }

                    int srcWidth = propData.width;
                    int srcHeight = propData.height;
                    int srcYSize = srcWidth * srcHeight;
                    int srcUVSize = srcYSize / 2;

                    Helper.Log.Write(Helper.eLogType.Info,
                        $"{TAG} Reading framebuffer: {srcWidth}x{srcHeight} at 0x{propData.bufferAddr.ToInt64():X}");

                    // Read source Y plane
                    byte[] srcY = new byte[srcYSize];
                    Marshal.Copy(propData.bufferAddr, srcY, 0, srcYSize);

                    // Read source UV plane (immediately after Y in NV12)
                    byte[] srcUV = new byte[srcUVSize];
                    Marshal.Copy(propData.bufferAddr + srcYSize, srcUV, 0, srcUVSize);

                    // Nearest-neighbor downscale to target resolution
                    byte[] yData = new byte[outW * outH];
                    byte[] uvData = new byte[outW * outH / 2];

                    // Downscale Y plane
                    for (int y = 0; y < outH; y++)
                    {
                        int srcRow = y * srcHeight / outH;
                        for (int x = 0; x < outW; x++)
                        {
                            int srcCol = x * srcWidth / outW;
                            yData[y * outW + x] = srcY[srcRow * srcWidth + srcCol];
                        }
                    }

                    // Downscale UV plane
                    int uvOutH = outH / 2;
                    for (int y = 0; y < uvOutH; y++)
                    {
                        int srcUVRow = y * (srcHeight / 2) / uvOutH;
                        for (int x = 0; x < outW; x += 2)
                        {
                            int srcUVCol = (x * srcWidth / outW) & ~1;
                            int srcIdx = srcUVRow * srcWidth + srcUVCol;
                            int dstIdx = y * outW + x;

                            if (srcIdx + 1 < srcUV.Length && dstIdx + 1 < uvData.Length)
                            {
                                uvData[dstIdx] = srcUV[srcIdx];
                                uvData[dstIdx + 1] = srcUV[srcIdx + 1];
                            }
                        }
                    }

                    Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Capture success: {outW}x{outH}");
                    return CaptureResult.CreateSuccess(yData, uvData, outW, outH);
                }
                finally
                {
                    Marshal.FreeHGlobal(structPtr);
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"{TAG} Capture exception: {ex.Message}");
                return CaptureResult.CreateFailure($"Exception: {ex.Message}");
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
            _workingPath = -1;
            _workingPropFunc = null;
            _fbWidth = 0;
            _fbHeight = 0;
            _ptrGetFrameBufferProp = IntPtr.Zero;
            _ptrGetFrameBufferProperty = IntPtr.Zero;
            _ptrGetFrameBufferSize = IntPtr.Zero;
            _ptrGetFramerate = IntPtr.Zero;
        }

        #endregion
    }
}
