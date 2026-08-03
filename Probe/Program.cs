using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tizen.Applications;

namespace HyperTizenProbe
{
    internal sealed class App : ServiceApplication
    {
        private static string _report = "Probe is starting...\n";
        private static TcpListener _listener;

        protected override void OnCreate()
        {
            base.OnCreate();
            Task.Run(() => _report = Probe.BuildReport());
            Task.Run((Action)ServeReport);
        }

        private static void ServeReport()
        {
            _listener = new TcpListener(IPAddress.Any, 45679);
            _listener.Start();
            while (true)
            {
                using (TcpClient client = _listener.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                {
                    byte[] request = new byte[2048];
                    int requestSize = 0;
                    try { requestSize = stream.Read(request, 0, request.Length); } catch { }
                    string requestText = Encoding.ASCII.GetString(request, 0, requestSize);
                    bool wantsFrame = requestText.StartsWith("GET /frame.ppm ");
                    bool wantsMirrorFrame = requestText.StartsWith("GET /mirror.ppm ");
                    string downloadPath = requestText.StartsWith("GET /libefl.so ")
                        ? "/usr/lib/libcapi-ui-efl-util.so.0"
                        : requestText.StartsWith("GET /swu-api.so ")
                            ? "/usr/lib/libSoftwareUpgradeAPI.so"
                            : requestText.StartsWith("GET /swu-config.so ")
                                ? "/usr/lib/libSWUProductionConfig.so"
                                : requestText.StartsWith("GET /swu-key.enc ")
                                    ? "/usr/share/org.tizen.tv.swu/itemsAESPassphraseEncrypted.txt"
                                    : requestText.StartsWith("GET /swu-key.pub ")
                                        ? "/usr/share/org.tizen.tv.swu/itemsPublicRSAKey.txt"
                                        : requestText.StartsWith("GET /display-capture.so ")
                                            ? "/usr/lib/libdisplay-capture-api.so.0"
                                            : requestText.StartsWith("GET /ep-screencapture.so ")
                                                ? "/usr/lib/libep-common-screencapture.so"
                                                : requestText.StartsWith("GET /rm-video-capture.so ")
                                                    ? "/usr/lib/librm-video-capture.so.0"
                                                    : requestText.StartsWith("GET /capi-rm-video-capture.so ")
                                                        ? "/usr/lib/libcapi-rm-video-capture.so.0"
                                                        : requestText.StartsWith("GET /screen-analysis.so ")
                                                            ? "/usr/lib/libscreen-analysis-api.so.1"
                                                            : requestText.StartsWith("GET /tzcapture.manifest ")
                                                                ? "/usr/lib/tzcapture.manifest"
                                                                : requestText.StartsWith("GET /rm-video-capture-impl.so ")
                                                                    ? "/prd/usr/lib/librm-video-capture-impl.so"
                                                                    : requestText.StartsWith("GET /swu-core ")
                                                                        ? "/usr/bin/SWUCoreTV"
                                                                        : requestText.StartsWith("GET /swu-verifier ")
                                                                            ? "/usr/bin/SWUVerifierTV"
                                                                            : requestText.StartsWith("GET /swu-service ")
                                                                                ? "/usr/bin/SWUService"
                                                                                : requestText.StartsWith("GET /swu-standalone ")
                                                                                    ? "/usr/bin/SWUStandalone"
                                                                                    : null;
                    byte[] body = wantsMirrorFrame && Probe.CapturedMirrorFramePpm != null
                        ? Probe.CapturedMirrorFramePpm
                        : wantsFrame && Probe.CapturedFramePpm != null
                        ? Probe.CapturedFramePpm
                        : downloadPath != null
                            ? File.ReadAllBytes(downloadPath)
                            : Encoding.UTF8.GetBytes(_report);
                    string contentType = wantsFrame || wantsMirrorFrame
                        ? "image/x-portable-pixmap"
                        : downloadPath != null ? "application/octet-stream" : "text/plain; charset=utf-8";
                    byte[] header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: " + contentType + "\r\n" +
                        "Cache-Control: no-store\r\nContent-Length: " + body.Length +
                        "\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length);
                    stream.Write(body, 0, body.Length);
                }
            }
        }

        private static void Main(string[] args)
        {
            new App().Run(args);
        }
    }

    internal static class Probe
    {
        private const int RtldLazy = 1;
        private const int RtldGlobal = 0x100;
        private static readonly byte[] RsmHeaderSalt =
            { 0xa8, 0x45, 0x90, 0x1f, 0xb0, 0xa0, 0x3c, 0x47 };

        internal static byte[] CapturedFramePpm { get; private set; }
        internal static byte[] CapturedMirrorFramePpm { get; private set; }

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string file, int flags);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string name);

        [DllImport("libdl.so.2")]
        private static extern int dlclose(IntPtr handle);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint SwuOpen(out uint origin);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint SwuUnwrap(
            byte[] salt, uint saltSize, byte[] output, uint outputCapacity,
            out uint outputSize, out uint origin, out int stage);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ScreenshotInitialize(int width, int height);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ScreenshotTake(IntPtr screenshot);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ScreenshotDeinitialize(IntPtr screenshot);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetLastResult();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NoArgInt();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int TbmSurfaceMap(IntPtr surface, int options, out TbmSurfaceInfo info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int TbmSurfaceUnmap(IntPtr surface);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int TbmSurfaceDestroy(IntPtr surface);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ScreenMirrorInitialize(int width, int height);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ScreenMirrorSetHandler(
            IntPtr screenMirror, ScreenMirrorFrameHandler handler, IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ScreenMirrorControl(IntPtr screenMirror);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ScreenMirrorFrameHandler(
            IntPtr screenMirror, IntPtr surface, IntPtr userData);

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
            public uint Width;
            public uint Height;
            public uint Format;
            public uint Bpp;
            public uint Size;
            public uint PlaneCount;
            public TbmPlane Plane0;
            public TbmPlane Plane1;
            public TbmPlane Plane2;
            public TbmPlane Plane3;
            public IntPtr Reserved4;
            public IntPtr Reserved5;
            public IntPtr Reserved6;
        }

        internal static string BuildReport()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("HyperTizen Tizen 9 probe");
            report.AppendLine("UTC: " + DateTime.UtcNow.ToString("O"));
            report.AppendLine("Architecture: " + RuntimeInformation.ProcessArchitecture);
            report.AppendLine();

            ProbeFiles(report);
            ProbeCandidatePaths(report);
            ProbeDeviceAccess(report);
            ProbeLibraries(report);
            ProbeRmVideoCapture(report);
            ProbeScreenshot(report);
            ProbeSwu(report);
            report.AppendLine("DONE");
            return report.ToString();
        }

        private static void ProbeDeviceAccess(StringBuilder report)
        {
            report.AppendLine("[DEVICE ACCESS]");
            foreach (string path in new[] { "/dev/video30", "/dev/dri/card0" })
            {
                try
                {
                    using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
                        report.AppendLine($"OPEN {path} handle={stream.SafeFileHandle.DangerousGetHandle()}");
                }
                catch (Exception ex)
                {
                    report.AppendLine($"DENY {path} {ex.GetType().Name}: {ex.Message}");
                }
            }
            report.AppendLine();
        }

        private static void ProbeRmVideoCapture(StringBuilder report)
        {
            report.AppendLine("[RM VIDEO CAPTURE]");
            IntPtr handle = dlopen("/usr/lib/libcapi-rm-video-capture.so.0", RtldLazy);
            if (handle == IntPtr.Zero)
            {
                report.AppendLine("library did not load: " + DlError());
            }
            else
            {
                try
                {
                    NoArgInt isSupported = GetDelegate<NoArgInt>(
                        handle, "_Z29rm_video_capture_is_supportedv");
                    report.AppendLine("is_supported=" +
                        (isSupported == null ? "symbol-missing" : isSupported().ToString()));
                }
                finally
                {
                    dlclose(handle);
                }
            }

            report.AppendLine();
        }

        private static void ProbeCandidatePaths(StringBuilder report)
        {
            report.AppendLine("[CANDIDATE PATHS]");
            string[] roots = { "/usr/lib", "/usr/bin", "/usr/share" };
            string[] needles =
            {
                "capture", "screen", "mirror", "swu", "upgrade", "decrypt", "crypto", "tee"
            };
            foreach (string root in roots)
            {
                try
                {
                    foreach (string path in Directory.EnumerateFileSystemEntries(root)
                        .Where(path => needles.Any(needle =>
                            Path.GetFileName(path).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
                        .OrderBy(path => path))
                        report.AppendLine(path);
                }
                catch (Exception ex)
                {
                    report.AppendLine($"DENY {root}: {ex.GetType().Name}");
                }
            }
            report.AppendLine();
        }

        private static void ProbeFiles(StringBuilder report)
        {
            report.AppendLine("[FILES]");
            string[] paths =
            {
                "/usr/share/org.tizen.tv.swu/itemsAESPassphraseEncrypted.txt",
                "/usr/share/org.tizen.tv.swu/itemsPublicRSAKey.txt",
                "/usr/share/org.tizen.tv.swu/OpenAPIAESPassphraseEncrypted.txt",
                "/usr/lib/libSWUProductionConfig.so",
                "/usr/lib/libSWUProductionConfigRelease.so",
                "/usr/lib/libSoftwareUpgradeAPI.so",
                "/usr/lib/libteec.so",
                "/usr/lib/libds-tizen-screenshooter.so.0.1.2",
                "/usr/lib/libcapi-ui-efl-util.so.0",
                "/usr/lib/libtzcapturec.so",
                "/usr/lib/libvideo-capture.so.0.1.0"
                , "/usr/lib/libdisplay-capture-api.so.0"
                , "/usr/lib/libep-common-screencapture.so"
                , "/usr/lib/librm-video-capture.so.0"
                , "/usr/lib/libcapi-rm-video-capture.so.0"
                , "/usr/lib/libscreen-analysis-api.so.1"
                , "/usr/lib/libscreenmirroring-api-tv.so"
                , "/usr/lib/tzcapture.manifest"
                , "/prd/usr/lib/librm-video-capture-impl.so"
                , "/usr/bin/SWUCoreTV"
                , "/usr/bin/SWUVerifierTV"
                , "/usr/bin/SWUService"
                , "/usr/bin/SWUStandalone"
            };

            foreach (string path in paths)
            {
                try
                {
                    byte[] data = File.ReadAllBytes(path);
                    using (SHA256 sha = SHA256.Create())
                    {
                        string digest = BitConverter.ToString(sha.ComputeHash(data))
                            .Replace("-", "").ToLowerInvariant();
                        report.AppendLine($"READ {path} size={data.Length} sha256={digest}");
                    }
                }
                catch (Exception ex)
                {
                    report.AppendLine($"DENY {path} {ex.GetType().Name}: {ex.Message}");
                }
            }
            report.AppendLine();
        }

        private static void ProbeLibraries(StringBuilder report)
        {
            report.AppendLine("[DLOPEN]");
            string[] libraries =
            {
                "/usr/lib/libSWUProductionConfig.so",
                "/usr/lib/libSWUProductionConfigRelease.so",
                "/usr/lib/libSoftwareUpgradeAPI.so",
                "/usr/lib/libteec.so",
                "/usr/lib/libds-tizen-screenshooter.so.0.1.2",
                "/usr/lib/libcapi-ui-efl-util.so.0",
                "/usr/lib/libtzcapturec.so",
                "/usr/lib/libvideo-capture.so.0.1.0"
                , "/usr/lib/libdisplay-capture-api.so.0"
                , "/usr/lib/libep-common-screencapture.so"
                , "/usr/lib/librm-video-capture.so.0"
                , "/usr/lib/libcapi-rm-video-capture.so.0"
                , "/usr/lib/libscreen-analysis-api.so.1"
                , "/usr/lib/libscreenmirroring-api-tv.so"
            };
            foreach (string library in libraries)
            {
                dlerror();
                IntPtr handle = dlopen(library, RtldLazy);
                if (handle == IntPtr.Zero)
                {
                    IntPtr error = dlerror();
                    report.AppendLine($"FAIL {library}: " +
                        (error == IntPtr.Zero ? "unknown" : Marshal.PtrToStringAnsi(error)));
                }
                else
                {
                    report.AppendLine($"OK   {library}");
                    dlclose(handle);
                }
            }
            report.AppendLine();
        }

        private static void ProbeScreenshot(StringBuilder report)
        {
            report.AppendLine("[EFL SCREENSHOT]");
            IntPtr handle = dlopen("/usr/lib/libcapi-ui-efl-util.so.0", RtldLazy);
            if (handle == IntPtr.Zero)
            {
                report.AppendLine("libcapi-ui-efl-util.so.0 did not load");
                report.AppendLine();
                return;
            }

            try
            {
                string[] mirrorSymbols =
                {
                    "efl_util_screenmirror_initialize",
                    "efl_util_screenmirror_set_handler",
                    "efl_util_screenmirror_start",
                    "efl_util_screenmirror_stop",
                    "efl_util_screenmirror_deinitialize"
                };
                foreach (string symbol in mirrorSymbols)
                    report.AppendLine($"symbol {symbol}=" +
                        (dlsym(handle, symbol) == IntPtr.Zero ? "missing" : "present"));

                ScreenshotInitialize initialize = GetDelegate<ScreenshotInitialize>(
                    handle, "efl_util_screenshot_initialize");
                ScreenshotTake take = GetDelegate<ScreenshotTake>(
                    handle, "efl_util_screenshot_take_tbm_surface");
                ScreenshotDeinitialize deinitialize = GetDelegate<ScreenshotDeinitialize>(
                    handle, "efl_util_screenshot_deinitialize");
                GetLastResult getLast = GetDelegate<GetLastResult>(
                    IntPtr.Zero, "get_last_result");

                if (initialize == null || take == null || deinitialize == null)
                {
                    report.AppendLine("required screenshot symbols are missing");
                    return;
                }

                IntPtr screenshot = initialize(320, 180);
                int initializeResult = getLast == null ? int.MinValue : getLast();
                report.AppendLine($"initialize=0x{screenshot.ToInt64():x} last={initializeResult}");
                if (screenshot != IntPtr.Zero)
                {
                    IntPtr surface = take(screenshot);
                    int takeResult = getLast == null ? int.MinValue : getLast();
                    report.AppendLine($"surface=0x{surface.ToInt64():x} last={takeResult}");
                    if (surface != IntPtr.Zero)
                        ReadSurface(report, surface);
                    report.AppendLine("deinitialize=" + deinitialize(screenshot));
                }

                ProbeScreenMirror(report, handle, getLast);
            }
            catch (Exception ex)
            {
                report.AppendLine(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                dlclose(handle);
                report.AppendLine();
            }
        }

        private static void ProbeScreenMirror(StringBuilder report, IntPtr handle, GetLastResult getLast)
        {
            ScreenMirrorInitialize initialize = GetDelegate<ScreenMirrorInitialize>(
                handle, "efl_util_screenmirror_initialize");
            ScreenMirrorSetHandler setHandler = GetDelegate<ScreenMirrorSetHandler>(
                handle, "efl_util_screenmirror_set_handler");
            ScreenMirrorControl start = GetDelegate<ScreenMirrorControl>(
                handle, "efl_util_screenmirror_start");
            ScreenMirrorControl stop = GetDelegate<ScreenMirrorControl>(
                handle, "efl_util_screenmirror_stop");
            ScreenMirrorControl deinitialize = GetDelegate<ScreenMirrorControl>(
                handle, "efl_util_screenmirror_deinitialize");
            if (initialize == null || setHandler == null || start == null ||
                stop == null || deinitialize == null)
                return;

            int frameCount = 0;
            int sampleStarted = 0;
            long callbackMirror = 0;
            long firstSurface = 0;
            ScreenMirrorFrameHandler handler = (callbackHandle, surface, userData) =>
            {
                Interlocked.CompareExchange(ref callbackMirror, callbackHandle.ToInt64(), 0);
                Interlocked.CompareExchange(ref firstSurface, surface.ToInt64(), 0);
                if (Interlocked.CompareExchange(ref sampleStarted, 1, 0) == 0)
                {
                    try { CapturedMirrorFramePpm = CopyMirrorSurface(surface); }
                    catch { }
                }
                Interlocked.Increment(ref frameCount);
            };

            IntPtr mirror = initialize(320, 180);
            report.AppendLine($"mirror.initialize=0x{mirror.ToInt64():x} last=" +
                (getLast == null ? int.MinValue : getLast()));
            if (mirror == IntPtr.Zero)
                return;

            int handlerResult = setHandler(mirror, handler, IntPtr.Zero);
            int startResult = handlerResult == 0 ? start(mirror) : int.MinValue;
            if (startResult == 0)
                Thread.Sleep(2000);
            int stopResult = startResult == 0 ? stop(mirror) : int.MinValue;
            int observedFrames = Volatile.Read(ref frameCount);
            report.AppendLine(
                $"mirror.handler={handlerResult} start={startResult} stop={stopResult} " +
                $"frames={observedFrames}/2s callback=0x{Interlocked.Read(ref callbackMirror):x} " +
                $"surface=0x{Interlocked.Read(ref firstSurface):x} " +
                $"sample={CapturedMirrorFramePpm?.Length ?? 0} download=/mirror.ppm");
            report.AppendLine("mirror.deinitialize=" + deinitialize(mirror));
            GC.KeepAlive(handler);
        }

        private static byte[] CopyMirrorSurface(IntPtr surface)
        {
            IntPtr tbm = dlopen("/usr/lib/libtbm.so.1", RtldLazy);
            if (tbm == IntPtr.Zero)
                return null;
            try
            {
                TbmSurfaceMap map = GetDelegate<TbmSurfaceMap>(tbm, "tbm_surface_map");
                TbmSurfaceUnmap unmap = GetDelegate<TbmSurfaceUnmap>(tbm, "tbm_surface_unmap");
                if (map == null || unmap == null)
                    return null;
                TbmSurfaceInfo info;
                if (map(surface, 1, out info) != 0 || info.Plane0.Pointer == IntPtr.Zero)
                    return null;
                try
                {
                    byte[] raw = new byte[info.Plane0.Size];
                    Marshal.Copy(info.Plane0.Pointer, raw, 0, raw.Length);
                    return ToPpm(raw, info.Width, info.Height, info.Plane0.Stride);
                }
                finally
                {
                    unmap(surface);
                }
            }
            finally
            {
                dlclose(tbm);
            }
        }

        private static void ReadSurface(StringBuilder report, IntPtr surface)
        {
            IntPtr tbm = dlopen("/usr/lib/libtbm.so.1", RtldLazy);
            if (tbm == IntPtr.Zero)
            {
                report.AppendLine("TBM library did not load");
                return;
            }

            try
            {
                TbmSurfaceMap map = GetDelegate<TbmSurfaceMap>(tbm, "tbm_surface_map");
                TbmSurfaceUnmap unmap = GetDelegate<TbmSurfaceUnmap>(tbm, "tbm_surface_unmap");
                TbmSurfaceDestroy destroy = GetDelegate<TbmSurfaceDestroy>(tbm, "tbm_surface_destroy");
                if (map == null || unmap == null || destroy == null)
                {
                    report.AppendLine("TBM symbols are missing");
                    return;
                }

                TbmSurfaceInfo info;
                int mapResult = map(surface, 1, out info);
                report.AppendLine(
                    $"tbm_map={mapResult} {info.Width}x{info.Height} " +
                    $"format=0x{info.Format:x8} bpp={info.Bpp} size={info.Size} " +
                    $"planes={info.PlaneCount} stride={info.Plane0.Stride}");
                if (mapResult == 0 && info.Plane0.Pointer != IntPtr.Zero && info.Plane0.Size > 0)
                {
                    byte[] raw = new byte[info.Plane0.Size];
                    Marshal.Copy(info.Plane0.Pointer, raw, 0, raw.Length);
                    CapturedFramePpm = ToPpm(raw, info.Width, info.Height, info.Plane0.Stride);
                    using (SHA256 sha = SHA256.Create())
                    {
                        string digest = BitConverter.ToString(sha.ComputeHash(raw))
                            .Replace("-", "").ToLowerInvariant();
                        int nonZero = raw.Count(value => value != 0);
                        report.AppendLine(
                            $"frame sha256={digest} nonzero={nonZero}/{raw.Length} " +
                            "download=/frame.ppm");
                    }
                    report.AppendLine("tbm_unmap=" + unmap(surface));
                }
                report.AppendLine("tbm_destroy=" + destroy(surface));
            }
            finally
            {
                dlclose(tbm);
            }
        }

        private static byte[] ToPpm(byte[] raw, uint width, uint height, uint stride)
        {
            byte[] header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
            byte[] ppm = new byte[header.Length + checked((int)(width * height * 3))];
            Buffer.BlockCopy(header, 0, ppm, 0, header.Length);
            int output = header.Length;
            for (uint y = 0; y < height; y++)
            {
                for (uint x = 0; x < width; x++)
                {
                    int input = checked((int)(y * stride + x * 4));
                    ppm[output++] = raw[input + 2];
                    ppm[output++] = raw[input + 1];
                    ppm[output++] = raw[input];
                }
            }
            return ppm;
        }

        private static T GetDelegate<T>(IntPtr handle, string name) where T : class
        {
            IntPtr address = dlsym(handle, name);
            return address == IntPtr.Zero
                ? null
                : Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        private static void ProbeSwu(StringBuilder report)
        {
            report.AppendLine("[SWU TRUSTZONE]");
            IntPtr teec = dlopen("/usr/lib/libteec.so", RtldLazy | RtldGlobal);
            if (teec == IntPtr.Zero)
                report.AppendLine("libteec preload failed: " + DlError());
            string nativePath = Path.Combine(AppContext.BaseDirectory, "libhypertizenprobe.so");
            IntPtr native = dlopen(nativePath, RtldLazy);
            if (native == IntPtr.Zero)
            {
                report.AppendLine("native helper did not load: " + DlError());
                if (teec != IntPtr.Zero)
                    dlclose(teec);
                report.AppendLine();
                return;
            }

            try
            {
                SwuOpen open = GetDelegate<SwuOpen>(native, "hypertizen_probe_swu_open");
                SwuUnwrap unwrap = GetDelegate<SwuUnwrap>(native, "hypertizen_probe_swu_unwrap");
                if (open == null || unwrap == null)
                {
                    report.AppendLine("native helper symbols are missing");
                    return;
                }

                uint origin;
                uint openResult = open(out origin);
                report.AppendLine($"open result=0x{openResult:x8} origin={origin}");

                for (byte mode = 0; mode < 4; mode++)
                {
                    byte[] output = new byte[512];
                    uint outputSize;
                    int stage;
                    uint unwrapResult = unwrap(
                        new[] { mode }, 1,
                        output, (uint)output.Length,
                        out outputSize, out origin, out stage);
                    report.AppendLine(
                        $"unwrap mode={mode} result=0x{unwrapResult:x8} " +
                        $"origin={origin} stage={stage} size={outputSize}");
                    if (unwrapResult == 0 && outputSize > 0)
                    {
                        int hashed = Math.Min((int)outputSize, output.Length);
                        using (var sha = System.Security.Cryptography.SHA256.Create())
                        {
                            byte[] digest = sha.ComputeHash(output, 0, hashed);
                            report.AppendLine("unwrap.sha256=" + BitConverter.ToString(digest)
                                .Replace("-", "").ToLowerInvariant());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report.AppendLine(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                dlclose(native);
                if (teec != IntPtr.Zero)
                    dlclose(teec);
            }
            report.AppendLine();
        }

        private static string DlError()
        {
            IntPtr error = dlerror();
            return error == IntPtr.Zero ? "unknown" : Marshal.PtrToStringAnsi(error);
        }
    }
}
