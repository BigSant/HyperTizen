using System;
using System.Runtime.InteropServices;

namespace HyperTizen.Capture
{
    /// <summary>
    /// SecVideoCapture method ported from HyperTizen V2.
    /// Tizen 7: flat C API via libsec-video-capture.so.0 (secvideo_api_capture_screen)
    /// Tizen 8+: C++ vtable API via libvideo-capture.so.0.1.0 (getInstance → vtable[3])
    /// Produces NV12 frames at requested resolution.
    /// </summary>
    public unsafe class SecVideoCaptureMethod : ICaptureMethod
    {
        public string Name => "SecVideoCapture (V2)";
        public CaptureMethodType Type => CaptureMethodType.SecVideoCapture;

        private bool _isInitialized;
        private bool _isTizen8Plus;

        // Persistent unmanaged buffers — allocated once, reused every frame
        private int _bufferWidth;
        private int _bufferHeight;
        private IntPtr _pImageY;
        private IntPtr _pImageUV;
        private byte[] _yData;
        private byte[] _uvData;

        // Tizen 8+ vtable cache
        private IntPtr _instancePtr;
        private CaptureScreenDelegate _captureScreenFn;

        #region Native structs (matches V2 SecVideoCapture.Info_t exactly)

        [StructLayout(LayoutKind.Sequential)]
        public struct Info_t
        {
            public int iGivenBufferSize1;   // Y plane buffer size
            public int iGivenBufferSize2;   // UV plane buffer size
            public int iWidth;
            public int iHeight;
            public IntPtr pImageY;          // Y plane buffer pointer
            public IntPtr pImageUV;         // UV plane buffer pointer
            public int iRetColorFormat;     // 0=YUV420, 1=YUV422, 2=YUV444
            public int unknown2;
            public int capture3DMode;       // 0=2D, 1=FRAMEPACKING, etc.
        }

        #endregion

        #region P/Invoke — Tizen 7 flat C API

        [DllImport("/usr/lib/libsec-video-capture.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "secvideo_api_capture_screen")]
        private static extern int T7_CaptureScreen(int w, int h, ref Info_t pInfo);

        #endregion

        #region P/Invoke — Tizen 8+ C++ vtable API

        [DllImport("/usr/lib/libvideo-capture.so.0.1.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "getInstance")]
        private static extern IntPtr GetInstance();

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate int CaptureScreenDelegate(IntPtr @this, int w, int h, ref Info_t pInfo);

        #endregion

        #region ICaptureMethod

        public bool IsAvailable()
        {
            Helper.Log.Write(Helper.eLogType.Info, "[SecVideoCapture] Checking availability...");

            int tizenMajor = SDK.SystemInfo.TizenVersionMajor;
            Helper.Log.Write(Helper.eLogType.Info, $"[SecVideoCapture] Tizen version: {tizenMajor}");

            if (tizenMajor >= 8)
            {
                // Tizen 8+: need libvideo-capture.so.0.1.0 with getInstance
                bool libExists = System.IO.File.Exists("/usr/lib/libvideo-capture.so.0.1.0");
                Helper.Log.Write(Helper.eLogType.Info,
                    $"[SecVideoCapture] libvideo-capture.so.0.1.0 exists: {libExists}");

                if (!libExists)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        "[SecVideoCapture] Not available — libvideo-capture.so.0.1.0 missing");
                    return false;
                }

                _isTizen8Plus = true;
                Helper.Log.Write(Helper.eLogType.Info, "[SecVideoCapture] Available (Tizen 8+ vtable path)");
                return true;
            }
            else
            {
                // Tizen 7 and below: need libsec-video-capture.so.0
                bool libExists = System.IO.File.Exists("/usr/lib/libsec-video-capture.so.0");
                Helper.Log.Write(Helper.eLogType.Info,
                    $"[SecVideoCapture] libsec-video-capture.so.0 exists: {libExists}");

                if (!libExists)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        "[SecVideoCapture] Not available — libsec-video-capture.so.0 missing");
                    return false;
                }

                _isTizen8Plus = false;
                Helper.Log.Write(Helper.eLogType.Info, "[SecVideoCapture] Available (Tizen 7 flat C path)");
                return true;
            }
        }

        public bool Test()
        {
            if (!IsAvailable())
                return false;

            try
            {
                Helper.Log.Write(Helper.eLogType.Info, "[SecVideoCapture] Running capture test...");

                // Initialize the vtable for Tizen 8+ (only once)
                if (_isTizen8Plus)
                {
                    if (!InitT8Vtable())
                        return false;
                }

                // Try a test capture at 480x270 (same as V2)
                int testW = 480;
                int testH = 270;
                int ySize = testW * testH;
                int uvSize = ySize / 2;

                IntPtr yBuf = Marshal.AllocHGlobal(ySize);
                IntPtr uvBuf = Marshal.AllocHGlobal(uvSize);

                try
                {
                    var info = new Info_t
                    {
                        iGivenBufferSize1 = ySize,
                        iGivenBufferSize2 = uvSize,
                        pImageY = yBuf,
                        pImageUV = uvBuf
                    };

                    int result = CallCaptureScreen(testW, testH, ref info);

                    Helper.Log.Write(Helper.eLogType.Info,
                        $"[SecVideoCapture] Test capture returned: {result}");

                    if (result >= 0)
                    {
                        Helper.Log.Write(Helper.eLogType.Info,
                            "[SecVideoCapture] Test PASSED");
                        return true;
                    }

                    // Log known error codes
                    switch (result)
                    {
                        case -2:
                            Helper.Log.Write(Helper.eLogType.Warning,
                                "[SecVideoCapture] Error -2 (scaler failure) — try cold reboot if persistent");
                            break;
                        case -4:
                            Helper.Log.Write(Helper.eLogType.Warning,
                                "[SecVideoCapture] Error -4 (DRM content) — test with non-DRM source");
                            // DRM error means the API works but content is protected.
                            // Consider this a pass — capture will work on non-DRM content.
                            Helper.Log.Write(Helper.eLogType.Info,
                                "[SecVideoCapture] Treating DRM error as PASS (API is functional)");
                            return true;
                        case -95:
                            Helper.Log.Write(Helper.eLogType.Warning,
                                "[SecVideoCapture] Error -95 (not supported on this firmware)");
                            break;
                        default:
                            Helper.Log.Write(Helper.eLogType.Warning,
                                $"[SecVideoCapture] Unknown error code: {result}");
                            break;
                    }

                    return false;
                }
                finally
                {
                    Marshal.FreeHGlobal(yBuf);
                    Marshal.FreeHGlobal(uvBuf);
                }
            }
            catch (DllNotFoundException ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"[SecVideoCapture] Library not found: {ex.Message}");
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"[SecVideoCapture] Entry point not found: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"[SecVideoCapture] Test exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public CaptureResult Capture(int width, int height)
        {
            try
            {
                // Lazy init: allocate persistent buffers on first capture
                if (!_isInitialized || _bufferWidth != width || _bufferHeight != height)
                {
                    InitBuffers(width, height);
                }

                var info = new Info_t
                {
                    iGivenBufferSize1 = _yData.Length,
                    iGivenBufferSize2 = _uvData.Length,
                    pImageY = _pImageY,
                    pImageUV = _pImageUV
                };

                int result = CallCaptureScreen(width, height, ref info);

                if (result < 0)
                {
                    switch (result)
                    {
                        case -4:
                            return CaptureResult.CreateFailure("DRM protected content (-4), skipping frame");
                        case -2:
                            return CaptureResult.CreateFailure("Scaler failure (-2), try cold reboot if persistent");
                        default:
                            return CaptureResult.CreateFailure($"SecVideoCapture error: {result}");
                    }
                }

                // Copy from unmanaged to managed arrays
                Marshal.Copy(_pImageY, _yData, 0, _yData.Length);
                Marshal.Copy(_pImageUV, _uvData, 0, _uvData.Length);

                return CaptureResult.CreateSuccess(_yData, _uvData, width, height);
            }
            catch (Exception ex)
            {
                return CaptureResult.CreateFailure($"SecVideoCapture exception: {ex.Message}");
            }
        }

        public void Cleanup()
        {
            FreeBuffers();
            _isInitialized = false;
            _instancePtr = IntPtr.Zero;
            _captureScreenFn = null;
            Helper.Log.Write(Helper.eLogType.Info, "[SecVideoCapture] Cleaned up");
        }

        #endregion

        #region Private helpers

        private bool InitT8Vtable()
        {
            try
            {
                Helper.Log.Write(Helper.eLogType.Info, "[SecVideoCapture] Initializing T8+ vtable...");

                IntPtr instance = GetInstance();
                if (instance == IntPtr.Zero)
                {
                    Helper.Log.Write(Helper.eLogType.Error,
                        "[SecVideoCapture] getInstance() returned null");
                    return false;
                }

                // Read vtable pointer (first pointer-sized field of the C++ object)
                IntPtr vtablePtr = *(IntPtr*)instance;

                // CaptureScreen is at vtable index 3
                const int CaptureScreenVTableIndex = 3;
                IntPtr fnPtr = *((IntPtr*)vtablePtr + CaptureScreenVTableIndex);

                _captureScreenFn = (CaptureScreenDelegate)
                    Marshal.GetDelegateForFunctionPointer(fnPtr, typeof(CaptureScreenDelegate));
                _instancePtr = (IntPtr)instance;

                Helper.Log.Write(Helper.eLogType.Info,
                    $"[SecVideoCapture] Vtable initialized — instance=0x{instance.ToString("X")}, " +
                    $"vtable=0x{vtablePtr.ToString("X")}, captureScreen=0x{fnPtr.ToString("X")}");

                return true;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"[SecVideoCapture] Vtable init failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private int CallCaptureScreen(int w, int h, ref Info_t info)
        {
            if (_isTizen8Plus)
            {
                if (_captureScreenFn == null)
                {
                    if (!InitT8Vtable())
                        return -99;
                }
                return _captureScreenFn(_instancePtr, w, h, ref info);
            }
            else
            {
                return T7_CaptureScreen(w, h, ref info);
            }
        }

        private void InitBuffers(int width, int height)
        {
            FreeBuffers();

            int ySize = width * height;
            int uvSize = ySize / 2;

            _pImageY = Marshal.AllocHGlobal(ySize);
            _pImageUV = Marshal.AllocHGlobal(uvSize);
            _yData = new byte[ySize];
            _uvData = new byte[uvSize];
            _bufferWidth = width;
            _bufferHeight = height;
            _isInitialized = true;

            Helper.Log.Write(Helper.eLogType.Info,
                $"[SecVideoCapture] Buffers allocated ({width}x{height} NV12, " +
                $"Y={ySize} bytes, UV={uvSize} bytes)");
        }

        private void FreeBuffers()
        {
            if (_pImageY != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_pImageY);
                _pImageY = IntPtr.Zero;
            }
            if (_pImageUV != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_pImageUV);
                _pImageUV = IntPtr.Zero;
            }
            _yData = null;
            _uvData = null;
        }

        #endregion
    }
}
