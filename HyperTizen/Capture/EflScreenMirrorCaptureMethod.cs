using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Continuous EFL compositor capture. Hardware-tested on QE77S95FATXXH:
    /// callbacks arrive at roughly 25-30 FPS and carry an XRGB8888 TBM surface.
    /// Like EflScreenshot, protected/hardware-overlay video can be black.
    /// </summary>
    public sealed class EflScreenMirrorCaptureMethod : ICaptureMethod
    {
        public string Name => "EflScreenMirror (continuous TBM)";
        public CaptureMethodType Type => CaptureMethodType.EflScreenMirror;

        private const string LibEfl = "libcapi-ui-efl-util.so.0";
        private const string LibTbm = "libtbm.so.1";
        private const int TbmRead = 1;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FrameHandler(IntPtr mirror, IntPtr surface, IntPtr userData);

        [DllImport(LibEfl, EntryPoint = "efl_util_screenmirror_initialize", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr Initialize(int width, int height);
        [DllImport(LibEfl, EntryPoint = "efl_util_screenmirror_set_handler", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetHandler(IntPtr mirror, FrameHandler handler, IntPtr userData);
        [DllImport(LibEfl, EntryPoint = "efl_util_screenmirror_start", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Start(IntPtr mirror);
        [DllImport(LibEfl, EntryPoint = "efl_util_screenmirror_stop", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Stop(IntPtr mirror);
        [DllImport(LibEfl, EntryPoint = "efl_util_screenmirror_deinitialize", CallingConvention = CallingConvention.Cdecl)]
        private static extern int Deinitialize(IntPtr mirror);
        [DllImport(LibTbm, EntryPoint = "tbm_surface_map", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SurfaceMap(IntPtr surface, int option, out TbmSurfaceInfo info);
        [DllImport(LibTbm, EntryPoint = "tbm_surface_unmap", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SurfaceUnmap(IntPtr surface);

        [StructLayout(LayoutKind.Sequential)]
        private struct TbmPlane
        {
            public IntPtr Pointer;
            public uint Size;
            public uint Offset;
            public uint Stride;
            public IntPtr Reserved1;
            public IntPtr Reserved2;
            public IntPtr Reserved3;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TbmSurfaceInfo
        {
            public uint Width, Height, Format, Bpp, Size, PlaneCount;
            public TbmPlane Plane0, Plane1, Plane2, Plane3;
        }

        private readonly object _frameLock = new object();
        private readonly object _callbackLock = new object();
        private readonly AutoResetEvent _frameReady = new AutoResetEvent(false);
        private readonly FrameHandler _handler;
        private IntPtr _mirror;
        private byte[] _latest;
        private int _latestWidth;
        private int _latestHeight;
        private int _latestBytesPerPixel;
        private int _configuredWidth;
        private int _configuredHeight;
        private bool _running;
        private volatile bool _acceptFrames;

        public EflScreenMirrorCaptureMethod()
        {
            _handler = OnFrame;
        }

        public bool IsAvailable()
        {
            return File.Exists("/usr/lib/" + LibEfl) && File.Exists("/usr/lib/" + LibTbm);
        }

        public bool Test()
        {
            CaptureResult result = Capture(480, 270);
            return result != null && result.Success;
        }

        public CaptureResult Capture(int width, int height)
        {
            try
            {
                if (!EnsureStarted(width, height))
                    return CaptureResult.CreateFailure("[EflScreenMirror] initialization failed");

                byte[] raw = null;
                int frameWidth = 0, frameHeight = 0, bytesPerPixel = 0;
                lock (_frameLock)
                {
                    if (_latest != null)
                    {
                        raw = (byte[])_latest.Clone();
                        frameWidth = _latestWidth;
                        frameHeight = _latestHeight;
                        bytesPerPixel = _latestBytesPerPixel;
                    }
                }

                if (raw == null)
                {
                    _frameReady.WaitOne(750);
                    lock (_frameLock)
                    {
                        if (_latest != null)
                        {
                            raw = (byte[])_latest.Clone();
                            frameWidth = _latestWidth;
                            frameHeight = _latestHeight;
                            bytesPerPixel = _latestBytesPerPixel;
                        }
                    }
                }

                if (raw == null)
                    return CaptureResult.CreateFailure("[EflScreenMirror] no callback frame received");

                ConvertToNv12(raw, frameWidth, frameHeight, bytesPerPixel,
                    out byte[] yData, out byte[] uvData);
                if (IsBlack(yData))
                    return CaptureResult.CreateFailure("[EflScreenMirror] Black frame - HW video plane not accessible");
                return CaptureResult.CreateSuccess(yData, uvData, frameWidth, frameHeight);
            }
            catch (Exception ex)
            {
                return CaptureResult.CreateFailure("[EflScreenMirror] " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private bool EnsureStarted(int width, int height)
        {
            if (_running && width == _configuredWidth && height == _configuredHeight)
                return true;
            Cleanup();
            _mirror = Initialize(width, height);
            if (_mirror == IntPtr.Zero)
                return false;
            _acceptFrames = true;
            if (SetHandler(_mirror, _handler, IntPtr.Zero) != 0 || Start(_mirror) != 0)
            {
                Cleanup();
                return false;
            }
            _configuredWidth = width;
            _configuredHeight = height;
            _running = true;
            Helper.Log.Write(Helper.eLogType.Info,
                $"[EflScreenMirror] Started continuous capture at {width}x{height}");
            return true;
        }

        private void OnFrame(IntPtr mirror, IntPtr surface, IntPtr userData)
        {
            if (!_acceptFrames || surface == IntPtr.Zero)
                return;
            lock (_callbackLock)
            {
                if (!_acceptFrames || SurfaceMap(surface, TbmRead, out TbmSurfaceInfo info) != 0)
                    return;
                try
                {
                    int width = (int)info.Width;
                    int height = (int)info.Height;
                    int bytesPerPixel = (int)info.Bpp / 8;
                    int rowBytes = width * bytesPerPixel;
                    if (info.Plane0.Pointer == IntPtr.Zero || width <= 0 || height <= 0 || bytesPerPixel < 3)
                        return;
                    byte[] raw = new byte[rowBytes * height];
                    for (int row = 0; row < height; row++)
                        Marshal.Copy(info.Plane0.Pointer + row * (int)info.Plane0.Stride,
                            raw, row * rowBytes, rowBytes);
                    lock (_frameLock)
                    {
                        _latest = raw;
                        _latestWidth = width;
                        _latestHeight = height;
                        _latestBytesPerPixel = bytesPerPixel;
                    }
                    _frameReady.Set();
                }
                finally
                {
                    SurfaceUnmap(surface);
                }
            }
        }

        public void Cleanup()
        {
            _acceptFrames = false;
            IntPtr mirror = _mirror;
            bool wasRunning = _running;
            _mirror = IntPtr.Zero;
            _running = false;
            if (mirror != IntPtr.Zero)
            {
                bool stopped = !wasRunning;
                if (wasRunning)
                {
                    try
                    {
                        Task<int> stopTask = Task.Run(() => Stop(mirror));
                        stopped = stopTask.Wait(500);
                    }
                    catch { stopped = false; }
                }
                if (stopped)
                {
                    lock (_callbackLock)
                    {
                        try { Deinitialize(mirror); } catch { }
                    }
                }
                else
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        "[EflScreenMirror] native stop timed out; abandoning handle until process exit");
                }
            }
            lock (_frameLock) { _latest = null; }
        }

        private static void ConvertToNv12(byte[] src, int width, int height, int bpp,
            out byte[] yData, out byte[] uvData)
        {
            yData = new byte[width * height];
            uvData = new byte[width * height / 2];
            int uv = 0;
            for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
            {
                int i = (row * width + col) * bpp;
                int b = src[i], g = src[i + 1], r = src[i + 2];
                yData[row * width + col] = (byte)Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 16, 235);
                if ((row & 1) == 0 && (col & 1) == 0)
                {
                    uvData[uv++] = (byte)Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 16, 240);
                    uvData[uv++] = (byte)Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 16, 240);
                }
            }
        }

        private static bool IsBlack(byte[] yData)
        {
            long sum = 0;
            int count = 0;
            for (int i = 0; i < yData.Length; i += 16) { sum += yData[i]; count++; }
            return count == 0 || sum / count < 20;
        }

        private static int Clamp(int value, int min, int max)
            => value < min ? min : value > max ? max : value;
    }
}
