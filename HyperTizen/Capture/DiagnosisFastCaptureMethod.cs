using System;
using System.Runtime.InteropServices;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Experimental capture method using libvideo-misc.so diagnosis fast capture API.
    /// Discovered via reverse engineering ELF symbol dumps of Samsung Tizen 9 firmware.
    /// Uses ppi_video_system_diagnosis_* symbols for screen capture.
    /// </summary>
    public class DiagnosisFastCaptureMethod : ICaptureMethod
    {
        private const string TAG = "[DiagnosisFastCapture]";
        private const int RTLD_LAZY = 1;
        private const string LIB_PATH = "/usr/lib/libvideo-misc.so";
        private const int OUTPUT_WIDTH = 64;
        private const int OUTPUT_HEIGHT = 48;

        private IntPtr _libHandle = IntPtr.Zero;
        private bool _isInitialized = false;
        private string _workingStrategy = null;

        // Resolved function pointers
        private IntPtr _ptrIsSupported = IntPtr.Zero;
        private IntPtr _ptrStartCapture = IntPtr.Zero;
        private IntPtr _ptrStopCapture = IntPtr.Zero;
        private IntPtr _ptrGetBackendDataAddress = IntPtr.Zero;
        private IntPtr _ptrGetBackendData = IntPtr.Zero;

        public string Name => "Diagnosis Fast Capture (libvideo-misc)";
        public CaptureMethodType Type => CaptureMethodType.DiagnosisFastCapture;

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

        #region Delegate Signatures - Multiple variants for unknown signatures

        // ppi_video_system_diagnosis_is_diagnosis_fast_capture_supported
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int IsSupportedDelegate();

        // ppi_video_system_diagnosis_stop_diagnosis_fast_capture
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int StopCaptureDelegate();

        // Start capture - Variant A: int func(int captureType)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int StartCaptureVariantA(int captureType);

        // Start capture - Variant B: int func()
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int StartCaptureVariantB();

        // Start capture - Variant C: int func(IntPtr outputBuffer, int bufSize)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int StartCaptureVariantC(IntPtr outputBuffer, int bufSize);

        // Start capture - Variant D: int func(int width, int height, IntPtr buffer)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int StartCaptureVariantD(int width, int height, IntPtr buffer);

        // Start capture - Variant E: int func(IntPtr bufferPtr, IntPtr widthPtr, IntPtr heightPtr)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int StartCaptureVariantE(IntPtr bufferPtr, IntPtr widthPtr, IntPtr heightPtr);

        // ppi_video_system_diagnosis_get_backend_data_address - returns IntPtr
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetBackendDataAddressDelegate();

        // ppi_video_system_diagnosis_get_backend_data - Variant A: int func(IntPtr buffer, int size)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetBackendDataVariantA(IntPtr buffer, int size);

        // ppi_video_system_diagnosis_get_backend_data - Variant B: IntPtr func()
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetBackendDataVariantB();

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
                // Try to open the library
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
                _ptrIsSupported = ResolveSymbol(_libHandle, "ppi_video_system_diagnosis_is_diagnosis_fast_capture_supported");
                _ptrStartCapture = ResolveSymbol(_libHandle, "ppi_video_system_diagnosis_start_diagnosis_fast_capture");
                _ptrStopCapture = ResolveSymbol(_libHandle, "ppi_video_system_diagnosis_stop_diagnosis_fast_capture");
                _ptrGetBackendDataAddress = ResolveSymbol(_libHandle, "ppi_video_system_diagnosis_get_backend_data_address");
                _ptrGetBackendData = ResolveSymbol(_libHandle, "ppi_video_system_diagnosis_get_backend_data");

                // Also resolve get_backend — may provide handle needed by other functions
                IntPtr ptrGetBackend = ResolveSymbol(_libHandle, "ppi_video_system_diagnosis_get_backend");

                // PROBE #1 (get_backend_data_address) DISABLED — crashes without init
                // PROBE #2 (get_backend) DISABLED — crashes without init

                // ═══════════════════════════════════════════════════════════
                // PROBE #3: start_diagnosis_fast_capture() with different arg counts
                // cdecl: extra args are safe (caller cleans stack)
                // Previous: no args → returned 4 (error). Try with int params.
                // ═══════════════════════════════════════════════════════════

                int startResult = -999;

                if (_ptrStartCapture != IntPtr.Zero)
                {
                    // Try no args (returned 4 last time)
                    {
                        var fn = Marshal.GetDelegateForFunctionPointer<StopCaptureDelegate>(_ptrStartCapture);
                        int r = fn();
                        Helper.Log.Write(Helper.eLogType.Info, $"{TAG} start() no args = {r}");
                        if (r == 0) startResult = 0;
                    }

                    // Try 1 arg: int func(int captureType) — type 0,1,2,3
                    // cdecl: extra args safe (caller cleans stack)
                    if (startResult != 0)
                    {
                        var fn1 = Marshal.GetDelegateForFunctionPointer<StartCaptureVariantA>(_ptrStartCapture);
                        for (int arg = 0; arg <= 3; arg++)
                        {
                            int r = fn1(arg);
                            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} start({arg}) = {r}");
                            if (r == 0) { startResult = 0; break; }
                        }
                    }

                    // Try 2 args: int func(int a, int b) — reuse VariantD with null 3rd arg (cdecl safe)
                    if (startResult != 0)
                    {
                        var fn2 = Marshal.GetDelegateForFunctionPointer<StartCaptureVariantD>(_ptrStartCapture);
                        int[][] pairs = new int[][] {
                            new int[] {3840, 2160},
                            new int[] {1920, 1080},
                            new int[] {0, 0},
                            new int[] {1, 0},
                            new int[] {0, 1},
                        };
                        foreach (var p in pairs)
                        {
                            int r = fn2(p[0], p[1], IntPtr.Zero);
                            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} start({p[0]}, {p[1]}, 0) = {r}");
                            if (r == 0) { startResult = 0; break; }
                        }
                    }
                }

                // If start succeeded, check backend_data_address — should be initialized now
                if (startResult == 0 && _ptrGetBackendDataAddress != IntPtr.Zero)
                {
                    Helper.Log.Write(Helper.eLogType.Info, $"{TAG} PROBE #3b: Re-checking backend_data_address after start...");
                    var getAddr2 = Marshal.GetDelegateForFunctionPointer<GetBackendDataAddressDelegate>(_ptrGetBackendDataAddress);
                    IntPtr addr2 = getAddr2();
                    Helper.Log.Write(Helper.eLogType.Info, $"{TAG} PROBE #3b RESULT: get_backend_data_address() = 0x{addr2.ToInt64():X}");

                    if (addr2 != IntPtr.Zero)
                    {
                        try
                        {
                            byte[] peek2 = new byte[128];
                            Marshal.Copy(addr2, peek2, 0, 128);
                            string hex2 = BitConverter.ToString(peek2).Replace("-", " ");
                            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} PROBE #3b MEMORY[0..127]: {hex2}");
                        }
                        catch (Exception ex)
                        {
                            Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} PROBE #3b memory read failed: {ex.Message}");
                        }
                    }
                }

                // Always stop after probing
                if (_ptrStopCapture != IntPtr.Zero)
                {
                    var stopCapture = Marshal.GetDelegateForFunctionPointer<StopCaptureDelegate>(_ptrStopCapture);
                    int stopResult = stopCapture();
                    Helper.Log.Write(Helper.eLogType.Info, $"{TAG} stop_diagnosis_fast_capture() = {stopResult}");
                }

                Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Probe complete. Returning NOT available (experimental).");
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

            if (_libHandle == IntPtr.Zero || _ptrStartCapture == IntPtr.Zero)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"{TAG} Library not loaded or start_capture symbol not resolved");
                return false;
            }

            // Try multiple start_capture signature variants
            bool success = false;

            // Variant B: int func() - simplest, no args
            if (!success) success = TestStartVariantB();

            // Variant A: int func(int captureType) - type=0 for screen
            if (!success) success = TestStartVariantA();

            // Variant D: int func(int width, int height, IntPtr buffer)
            if (!success) success = TestStartVariantD();

            // Variant E: int func(IntPtr bufferPtr, IntPtr widthPtr, IntPtr heightPtr)
            if (!success) success = TestStartVariantE();

            // Variant C: int func(IntPtr outputBuffer, int bufSize)
            if (!success) success = TestStartVariantC();

            if (success)
            {
                _isInitialized = true;
                Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Test PASSED using strategy: {_workingStrategy}");
                return true;
            }

            Helper.Log.Write(Helper.eLogType.Error, $"{TAG} Test FAILED - all start_capture variants failed");
            return false;
        }

        private bool TestStartVariantA()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Testing Variant A: start_capture(int captureType)");
            try
            {
                var startFunc = Marshal.GetDelegateForFunctionPointer<StartCaptureVariantA>(_ptrStartCapture);

                for (int captureType = 0; captureType <= 2; captureType++)
                {
                    Helper.Log.Write(Helper.eLogType.Info, $"{TAG}   Trying captureType={captureType}");
                    int result = startFunc(captureType);
                    Helper.Log.Write(Helper.eLogType.Info, $"{TAG}   start_capture({captureType}) returned: {result}");

                    if (result == 0 || result == 1)
                    {
                        // Try to read backend data
                        bool dataOk = TryReadBackendData();

                        // Stop capture
                        StopCapture();

                        if (dataOk)
                        {
                            _workingStrategy = $"VariantA_type{captureType}";
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} Variant A exception: {ex.Message}");
            }
            return false;
        }

        private bool TestStartVariantB()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Testing Variant B: start_capture()");
            try
            {
                var startFunc = Marshal.GetDelegateForFunctionPointer<StartCaptureVariantB>(_ptrStartCapture);
                int result = startFunc();
                Helper.Log.Write(Helper.eLogType.Info, $"{TAG}   start_capture() returned: {result}");

                if (result == 0 || result == 1)
                {
                    bool dataOk = TryReadBackendData();
                    StopCapture();

                    if (dataOk)
                    {
                        _workingStrategy = "VariantB_noargs";
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} Variant B exception: {ex.Message}");
            }
            return false;
        }

        private bool TestStartVariantC()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Testing Variant C: start_capture(IntPtr outputBuffer, int bufSize)");
            int bufSize = 1920 * 1080 * 3 / 2; // NV12 full HD
            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(bufSize);
                var startFunc = Marshal.GetDelegateForFunctionPointer<StartCaptureVariantC>(_ptrStartCapture);
                int result = startFunc(buffer, bufSize);
                Helper.Log.Write(Helper.eLogType.Info, $"{TAG}   start_capture(buffer, {bufSize}) returned: {result}");

                if (result == 0 || result == 1)
                {
                    StopCapture();
                    _workingStrategy = "VariantC_bufferSize";
                    return true;
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} Variant C exception: {ex.Message}");
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
            return false;
        }

        private bool TestStartVariantD()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Testing Variant D: start_capture(int width, int height, IntPtr buffer)");
            int bufSize = 1920 * 1080 * 3 / 2;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(bufSize);
                var startFunc = Marshal.GetDelegateForFunctionPointer<StartCaptureVariantD>(_ptrStartCapture);
                int result = startFunc(1920, 1080, buffer);
                Helper.Log.Write(Helper.eLogType.Info, $"{TAG}   start_capture(1920, 1080, buffer) returned: {result}");

                if (result == 0 || result == 1)
                {
                    StopCapture();
                    _workingStrategy = "VariantD_whBuffer";
                    return true;
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} Variant D exception: {ex.Message}");
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
            return false;
        }

        private bool TestStartVariantE()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Testing Variant E: start_capture(IntPtr bufferPtr, IntPtr widthPtr, IntPtr heightPtr)");
            IntPtr bufferOut = IntPtr.Zero;
            IntPtr widthOut = IntPtr.Zero;
            IntPtr heightOut = IntPtr.Zero;
            try
            {
                bufferOut = Marshal.AllocHGlobal(IntPtr.Size);
                widthOut = Marshal.AllocHGlobal(sizeof(int));
                heightOut = Marshal.AllocHGlobal(sizeof(int));

                Marshal.WriteIntPtr(bufferOut, IntPtr.Zero);
                Marshal.WriteInt32(widthOut, 0);
                Marshal.WriteInt32(heightOut, 0);

                var startFunc = Marshal.GetDelegateForFunctionPointer<StartCaptureVariantE>(_ptrStartCapture);
                int result = startFunc(bufferOut, widthOut, heightOut);

                int outW = Marshal.ReadInt32(widthOut);
                int outH = Marshal.ReadInt32(heightOut);
                IntPtr outBuf = Marshal.ReadIntPtr(bufferOut);

                Helper.Log.Write(Helper.eLogType.Info,
                    $"{TAG}   start_capture(out) returned: {result}, w={outW}, h={outH}, buf=0x{outBuf.ToInt64():X}");

                if (result == 0 || result == 1)
                {
                    StopCapture();

                    if (outW > 0 && outH > 0)
                    {
                        _workingStrategy = "VariantE_outParams";
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} Variant E exception: {ex.Message}");
            }
            finally
            {
                if (bufferOut != IntPtr.Zero) Marshal.FreeHGlobal(bufferOut);
                if (widthOut != IntPtr.Zero) Marshal.FreeHGlobal(widthOut);
                if (heightOut != IntPtr.Zero) Marshal.FreeHGlobal(heightOut);
            }
            return false;
        }

        private bool TryReadBackendData()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Attempting to read backend data...");

            // Try get_backend_data_address first
            if (_ptrGetBackendDataAddress != IntPtr.Zero)
            {
                try
                {
                    var getAddr = Marshal.GetDelegateForFunctionPointer<GetBackendDataAddressDelegate>(_ptrGetBackendDataAddress);
                    IntPtr addr = getAddr();
                    Helper.Log.Write(Helper.eLogType.Info, $"{TAG}   get_backend_data_address() returned: 0x{addr.ToInt64():X}");

                    if (addr != IntPtr.Zero)
                    {
                        // Try to peek at the first 32 bytes to see if it's valid memory
                        try
                        {
                            byte[] peek = new byte[32];
                            Marshal.Copy(addr, peek, 0, 32);
                            Helper.Log.Write(Helper.eLogType.Info,
                                $"{TAG}   Backend data peek: {BitConverter.ToString(peek, 0, 16)}...");
                            return true;
                        }
                        catch (Exception ex)
                        {
                            Helper.Log.Write(Helper.eLogType.Warning, $"{TAG}   Backend data address invalid (access violation): {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG}   get_backend_data_address failed: {ex.Message}");
                }
            }

            // Try get_backend_data (variant B - returns pointer)
            if (_ptrGetBackendData != IntPtr.Zero)
            {
                try
                {
                    var getDataB = Marshal.GetDelegateForFunctionPointer<GetBackendDataVariantB>(_ptrGetBackendData);
                    IntPtr dataPtr = getDataB();
                    Helper.Log.Write(Helper.eLogType.Info, $"{TAG}   get_backend_data() returned: 0x{dataPtr.ToInt64():X}");

                    if (dataPtr != IntPtr.Zero)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG}   get_backend_data (variant B) failed: {ex.Message}");
                }
            }

            // Try get_backend_data (variant A - buffer+size)
            if (_ptrGetBackendData != IntPtr.Zero)
            {
                IntPtr tempBuf = IntPtr.Zero;
                try
                {
                    int tempSize = 1920 * 1080 * 3 / 2;
                    tempBuf = Marshal.AllocHGlobal(tempSize);
                    var getDataA = Marshal.GetDelegateForFunctionPointer<GetBackendDataVariantA>(_ptrGetBackendData);
                    int result = getDataA(tempBuf, tempSize);
                    Helper.Log.Write(Helper.eLogType.Info, $"{TAG}   get_backend_data(buffer, {tempSize}) returned: {result}");

                    if (result == 0 || result > 0)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG}   get_backend_data (variant A) failed: {ex.Message}");
                }
                finally
                {
                    if (tempBuf != IntPtr.Zero) Marshal.FreeHGlobal(tempBuf);
                }
            }

            Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} Could not read backend data via any method");
            return false;
        }

        private void StopCapture()
        {
            if (_ptrStopCapture != IntPtr.Zero)
            {
                try
                {
                    var stopFunc = Marshal.GetDelegateForFunctionPointer<StopCaptureDelegate>(_ptrStopCapture);
                    int result = stopFunc();
                    Helper.Log.Write(Helper.eLogType.Info, $"{TAG} stop_diagnosis_fast_capture() returned: {result}");
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} stop_capture failed: {ex.Message}");
                }
            }
        }

        public CaptureResult Capture(int width, int height)
        {
            if (!_isInitialized || string.IsNullOrEmpty(_workingStrategy))
            {
                return CaptureResult.CreateFailure("DiagnosisFastCapture not initialized - call Test() first");
            }

            try
            {
                Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Capture {width}x{height} using strategy: {_workingStrategy}");

                // Start capture using the working strategy
                int startResult = -1;

                if (_workingStrategy.StartsWith("VariantA_type"))
                {
                    int captureType = int.Parse(_workingStrategy.Substring("VariantA_type".Length));
                    var startFunc = Marshal.GetDelegateForFunctionPointer<StartCaptureVariantA>(_ptrStartCapture);
                    startResult = startFunc(captureType);
                }
                else if (_workingStrategy == "VariantB_noargs")
                {
                    var startFunc = Marshal.GetDelegateForFunctionPointer<StartCaptureVariantB>(_ptrStartCapture);
                    startResult = startFunc();
                }
                else if (_workingStrategy == "VariantC_bufferSize")
                {
                    int bufSize = 1920 * 1080 * 3 / 2;
                    IntPtr buffer = Marshal.AllocHGlobal(bufSize);
                    try
                    {
                        var startFunc = Marshal.GetDelegateForFunctionPointer<StartCaptureVariantC>(_ptrStartCapture);
                        startResult = startFunc(buffer, bufSize);

                        if (startResult == 0 || startResult == 1)
                        {
                            // Data is directly in the buffer
                            return ExtractNV12FromBuffer(buffer, 1920, 1080, width, height);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                else if (_workingStrategy == "VariantD_whBuffer")
                {
                    int bufSize = 1920 * 1080 * 3 / 2;
                    IntPtr buffer = Marshal.AllocHGlobal(bufSize);
                    try
                    {
                        var startFunc = Marshal.GetDelegateForFunctionPointer<StartCaptureVariantD>(_ptrStartCapture);
                        startResult = startFunc(1920, 1080, buffer);

                        if (startResult == 0 || startResult == 1)
                        {
                            return ExtractNV12FromBuffer(buffer, 1920, 1080, width, height);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                else if (_workingStrategy == "VariantE_outParams")
                {
                    IntPtr bufferOut = Marshal.AllocHGlobal(IntPtr.Size);
                    IntPtr widthOut = Marshal.AllocHGlobal(sizeof(int));
                    IntPtr heightOut = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        Marshal.WriteIntPtr(bufferOut, IntPtr.Zero);
                        Marshal.WriteInt32(widthOut, 0);
                        Marshal.WriteInt32(heightOut, 0);

                        var startFunc = Marshal.GetDelegateForFunctionPointer<StartCaptureVariantE>(_ptrStartCapture);
                        startResult = startFunc(bufferOut, widthOut, heightOut);

                        if (startResult == 0 || startResult == 1)
                        {
                            int outW = Marshal.ReadInt32(widthOut);
                            int outH = Marshal.ReadInt32(heightOut);
                            IntPtr outBuf = Marshal.ReadIntPtr(bufferOut);

                            if (outBuf != IntPtr.Zero && outW > 0 && outH > 0)
                            {
                                var captureResult = ExtractNV12FromBuffer(outBuf, outW, outH, width, height);
                                StopCapture();
                                return captureResult;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(bufferOut);
                        Marshal.FreeHGlobal(widthOut);
                        Marshal.FreeHGlobal(heightOut);
                    }
                }

                if (startResult != 0 && startResult != 1)
                {
                    StopCapture();
                    return CaptureResult.CreateFailure($"start_capture returned error: {startResult}");
                }

                // Try to read backend data for variants A and B
                CaptureResult result = TryCaptureFromBackendData(width, height);

                StopCapture();

                return result;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"{TAG} Capture exception: {ex.Message}");
                StopCapture();
                return CaptureResult.CreateFailure($"Exception: {ex.Message}");
            }
        }

        private CaptureResult TryCaptureFromBackendData(int targetWidth, int targetHeight)
        {
            // Try get_backend_data_address
            if (_ptrGetBackendDataAddress != IntPtr.Zero)
            {
                try
                {
                    var getAddr = Marshal.GetDelegateForFunctionPointer<GetBackendDataAddressDelegate>(_ptrGetBackendDataAddress);
                    IntPtr addr = getAddr();

                    if (addr != IntPtr.Zero)
                    {
                        Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Backend data address: 0x{addr.ToInt64():X}");
                        // Assume 1920x1080 NV12 at the address
                        return ExtractNV12FromBuffer(addr, 1920, 1080, targetWidth, targetHeight);
                    }
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} Backend data address read failed: {ex.Message}");
                }
            }

            // Try get_backend_data (variant A - buffer+size)
            if (_ptrGetBackendData != IntPtr.Zero)
            {
                int bufSize = 1920 * 1080 * 3 / 2;
                IntPtr buffer = Marshal.AllocHGlobal(bufSize);
                try
                {
                    var getDataA = Marshal.GetDelegateForFunctionPointer<GetBackendDataVariantA>(_ptrGetBackendData);
                    int result = getDataA(buffer, bufSize);

                    if (result == 0 || result > 0)
                    {
                        Helper.Log.Write(Helper.eLogType.Info, $"{TAG} get_backend_data returned: {result}");
                        return ExtractNV12FromBuffer(buffer, 1920, 1080, targetWidth, targetHeight);
                    }
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"{TAG} get_backend_data (variant A) failed: {ex.Message}");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return CaptureResult.CreateFailure("Could not read capture data from any backend data method");
        }

        private CaptureResult ExtractNV12FromBuffer(IntPtr sourceBuffer, int srcWidth, int srcHeight, int targetWidth, int targetHeight)
        {
            try
            {
                int outW = targetWidth > 0 ? targetWidth : OUTPUT_WIDTH;
                int outH = targetHeight > 0 ? targetHeight : OUTPUT_HEIGHT;

                int srcYSize = srcWidth * srcHeight;
                int srcUVSize = srcYSize / 2;

                // Simple nearest-neighbor downscale from source to target NV12
                byte[] yData = new byte[outW * outH];
                byte[] uvData = new byte[outW * outH / 2];

                // Read source Y plane
                byte[] srcY = new byte[srcYSize];
                Marshal.Copy(sourceBuffer, srcY, 0, srcYSize);

                // Read source UV plane
                byte[] srcUV = new byte[srcUVSize];
                Marshal.Copy(sourceBuffer + srcYSize, srcUV, 0, srcUVSize);

                // Downscale Y plane
                for (int y = 0; y < outH; y++)
                {
                    int srcY_row = y * srcHeight / outH;
                    for (int x = 0; x < outW; x++)
                    {
                        int srcX_col = x * srcWidth / outW;
                        yData[y * outW + x] = srcY[srcY_row * srcWidth + srcX_col];
                    }
                }

                // Downscale UV plane
                int uvOutH = outH / 2;
                int uvOutW = outW;
                int uvSrcW = srcWidth;

                for (int y = 0; y < uvOutH; y++)
                {
                    int srcUVRow = y * (srcHeight / 2) / uvOutH;
                    for (int x = 0; x < uvOutW; x += 2)
                    {
                        int srcUVCol = (x * srcWidth / outW) & ~1; // align to UV pair
                        int srcIdx = srcUVRow * uvSrcW + srcUVCol;
                        int dstIdx = y * uvOutW + x;

                        if (srcIdx + 1 < srcUV.Length && dstIdx + 1 < uvData.Length)
                        {
                            uvData[dstIdx] = srcUV[srcIdx];
                            uvData[dstIdx + 1] = srcUV[srcIdx + 1];
                        }
                    }
                }

                Helper.Log.Write(Helper.eLogType.Info, $"{TAG} NV12 extracted: {outW}x{outH}");
                return CaptureResult.CreateSuccess(yData, uvData, outW, outH);
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"{TAG} ExtractNV12 exception: {ex.Message}");
                return CaptureResult.CreateFailure($"NV12 extraction failed: {ex.Message}");
            }
        }

        public void Cleanup()
        {
            Helper.Log.Write(Helper.eLogType.Info, $"{TAG} Cleanup");

            if (_libHandle != IntPtr.Zero)
            {
                try
                {
                    StopCapture();
                }
                catch { }

                dlclose(_libHandle);
                _libHandle = IntPtr.Zero;
            }

            _isInitialized = false;
            _workingStrategy = null;
            _ptrIsSupported = IntPtr.Zero;
            _ptrStartCapture = IntPtr.Zero;
            _ptrStopCapture = IntPtr.Zero;
            _ptrGetBackendDataAddress = IntPtr.Zero;
            _ptrGetBackendData = IntPtr.Zero;
        }

        #endregion
    }
}
