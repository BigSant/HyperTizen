using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            new Thread(() => _report = Probe.BuildReport()) { IsBackground = true }.Start();
            new Thread(ServeReport) { IsBackground = true }.Start();
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
                    byte[] request = ReadHttpRequest(stream);
                    int headerEnd = FindHeaderEnd(request);
                    int bodyOffset = headerEnd < 0 ? request.Length : headerEnd + 4;
                    string requestText = Encoding.ASCII.GetString(
                        request, 0, headerEnd < 0 ? request.Length : headerEnd);
                    byte[] requestBody = new byte[request.Length - bodyOffset];
                    if (requestBody.Length > 0)
                        Buffer.BlockCopy(request, bodyOffset, requestBody, 0, requestBody.Length);
                    bool wantsFrame = requestText.StartsWith("GET /frame.ppm ");
                    bool wantsMirrorFrame = requestText.StartsWith("GET /mirror.ppm ");
                    bool wantsDisplayStream = requestText.StartsWith("GET /display.h264 ");
                    bool wantsTzFrame = requestText.StartsWith("GET /tz.ppm ");
                    bool wantsHeaderDecrypt = requestText.StartsWith("POST /decrypt-header");
                    bool wantsStreamBegin = requestText.StartsWith("POST /stream-begin");
                    bool wantsStreamUpdate = requestText.StartsWith("POST /stream-update");
                    bool wantsStreamFinish = requestText.StartsWith("POST /stream-finish");
                    bool wantsStreamAbort = requestText.StartsWith("POST /stream-abort");
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
                    byte[] body = wantsStreamBegin
                        ? Probe.BeginFirmwareStream(
                            requestBody,
                            GetQueryInt(requestText, "derivation", 1),
                            GetQueryInt(requestText, "keysize", 2),
                            GetQueryInt(requestText, "mode", 1))
                        : wantsStreamUpdate
                        ? Probe.UpdateFirmwareStream(requestBody)
                        : wantsStreamFinish
                        ? Probe.FinishFirmwareStream()
                        : wantsStreamAbort
                        ? Probe.AbortFirmwareStream()
                        : wantsHeaderDecrypt
                        ? Probe.DecryptFirmwareHeader(
                            requestBody,
                            GetQueryInt(requestText, "derivation", 1),
                            GetQueryInt(requestText, "keysize", 2),
                            GetQueryInt(requestText, "mode", 0))
                        : wantsMirrorFrame && Probe.CapturedMirrorFramePpm != null
                        ? Probe.CapturedMirrorFramePpm
                        : wantsFrame && Probe.CapturedFramePpm != null
                        ? Probe.CapturedFramePpm
                        : wantsDisplayStream && Probe.CapturedDisplayH264 != null
                        ? Probe.CapturedDisplayH264
                        : wantsTzFrame && Probe.CapturedTzFramePpm != null
                        ? Probe.CapturedTzFramePpm
                        : downloadPath != null
                            ? File.ReadAllBytes(downloadPath)
                            : Encoding.UTF8.GetBytes(_report);
                    string contentType = wantsHeaderDecrypt || wantsStreamBegin ||
                        wantsStreamUpdate || wantsStreamFinish || wantsStreamAbort
                        ? "application/octet-stream"
                        : wantsFrame || wantsMirrorFrame || wantsTzFrame
                        ? "image/x-portable-pixmap"
                        : wantsDisplayStream ? "video/h264"
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

        private static byte[] ReadHttpRequest(NetworkStream stream)
        {
            stream.ReadTimeout = 5000;
            using (var data = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                int expected = -1;
                while (data.Length < 131072)
                {
                    int read;
                    try { read = stream.Read(chunk, 0, chunk.Length); }
                    catch { break; }
                    if (read <= 0) break;
                    data.Write(chunk, 0, read);
                    byte[] current = data.ToArray();
                    int end = FindHeaderEnd(current);
                    if (end >= 0 && expected < 0)
                    {
                        string headers = Encoding.ASCII.GetString(current, 0, end);
                        int contentLength = 0;
                        foreach (string line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                int.TryParse(line.Substring(line.IndexOf(':') + 1).Trim(), out contentLength);
                        }
                        expected = end + 4 + contentLength;
                    }
                    if (expected >= 0 && data.Length >= expected) break;
                }
                return data.ToArray();
            }
        }

        private static int FindHeaderEnd(byte[] data)
        {
            for (int i = 0; i + 3 < data.Length; i++)
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            return -1;
        }

        private static int GetQueryInt(string request, string name, int fallback)
        {
            string marker = name + "=";
            int start = request.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return fallback;
            start += marker.Length;
            int end = request.IndexOfAny(new[] { '&', ' ' }, start);
            string value = end < 0 ? request.Substring(start) : request.Substring(start, end - start);
            return int.TryParse(value, out int parsed) ? parsed : fallback;
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
        private static IntPtr _teecHandle;
        private static IntPtr _nativeHandle;
        private static readonly byte[] RsmHeaderSalt =
            { 0xa8, 0x45, 0x90, 0x1f, 0xb0, 0xa0, 0x3c, 0x47 };

        internal static byte[] CapturedFramePpm { get; private set; }
        internal static byte[] CapturedMirrorFramePpm { get; private set; }
        internal static byte[] CapturedDisplayH264 { get; private set; }
        internal static byte[] CapturedTzFramePpm { get; private set; }
        internal static byte[] CapturedSecvideoFramePpm { get; private set; }

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string file, int flags);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string name);

        [DllImport("libdl.so.2")]
        private static extern int dlclose(IntPtr handle);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int ioctl(int fd, uint request, byte[] argument);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint SwuOpen(out uint origin);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint SwuUnwrap(
            byte[] salt, uint saltSize, byte[] output, uint outputCapacity,
            out uint outputSize, out uint origin, out int stage);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint SwuDecrypt(
            byte[] salt, uint saltSize, byte[] ciphertext, uint ciphertextSize,
            byte[] output, uint outputCapacity, out uint outputSize,
            out uint origin, out int stage, uint derivation, uint keySize, uint mode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint SwuStreamBegin(
            byte[] salt, uint saltSize, out uint origin, out int stage,
            uint derivation, uint keySize, uint mode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint SwuStreamUpdate(
            byte[] ciphertext, uint ciphertextSize, byte[] output,
            uint outputCapacity, out uint outputSize, out uint origin, out int stage);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint SwuStreamFinish(
            byte[] output, uint outputCapacity, out uint outputSize,
            out uint origin, out int stage);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SwuStreamAbort();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint TzCapture(
            byte[] y, uint yCapacity, byte[] c, uint cCapacity,
            out uint width, out uint height, out uint chromaFull,
            out uint metadata, out uint origin, out int stage);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SecvideoCapture(
            byte[] y, uint yCapacity, byte[] c, uint cCapacity,
            uint requestedWidth, uint requestedHeight,
            uint[] resultWords, uint resultWordCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int RmEncoderProbe(uint[] resultWords, uint resultWordCount);

        private static bool EnsureNative(out string error)
        {
            error = null;
            if (_nativeHandle != IntPtr.Zero) return true;
            _teecHandle = dlopen("/usr/lib/libteec.so", RtldLazy | RtldGlobal);
            string nativePath = Path.Combine(AppContext.BaseDirectory, "libhypertizenprobe.so");
            _nativeHandle = dlopen(nativePath, RtldLazy);
            if (_nativeHandle != IntPtr.Zero) return true;
            error = "native helper load failed: " + DlError();
            return false;
        }

        private static byte[] NativeError(uint result, uint origin, int stage)
        {
            return Encoding.ASCII.GetBytes(
                $"ERROR result=0x{result:x8} origin={origin} stage={stage}");
        }

        internal static byte[] BeginFirmwareStream(
            byte[] salt, int derivation, int keySize, int mode)
        {
            if (salt == null || salt.Length == 0)
                return Encoding.ASCII.GetBytes("ERROR missing salt");
            if (!EnsureNative(out string error))
                return Encoding.ASCII.GetBytes("ERROR " + error);
            SwuStreamBegin begin = GetDelegate<SwuStreamBegin>(
                _nativeHandle, "hypertizen_probe_swu_stream_begin");
            if (begin == null) return Encoding.ASCII.GetBytes("ERROR begin symbol missing");
            uint origin;
            int stage;
            uint result = begin(salt, (uint)salt.Length, out origin, out stage,
                (uint)derivation, (uint)keySize, (uint)mode);
            return result == 0 ? Encoding.ASCII.GetBytes("OK") : NativeError(result, origin, stage);
        }

        internal static byte[] UpdateFirmwareStream(byte[] ciphertext)
        {
            if (ciphertext == null || ciphertext.Length == 0 || ciphertext.Length > 65536)
                return Encoding.ASCII.GetBytes("ERROR invalid ciphertext chunk");
            if (!EnsureNative(out string error))
                return Encoding.ASCII.GetBytes("ERROR " + error);
            SwuStreamUpdate update = GetDelegate<SwuStreamUpdate>(
                _nativeHandle, "hypertizen_probe_swu_stream_update");
            if (update == null) return Encoding.ASCII.GetBytes("ERROR update symbol missing");
            byte[] output = new byte[65536];
            uint outputSize, origin;
            int stage;
            uint result = update(ciphertext, (uint)ciphertext.Length, output,
                (uint)output.Length, out outputSize, out origin, out stage);
            if (result != 0) return NativeError(result, origin, stage);
            Array.Resize(ref output, (int)outputSize);
            return output;
        }

        internal static byte[] FinishFirmwareStream()
        {
            if (!EnsureNative(out string error))
                return Encoding.ASCII.GetBytes("ERROR " + error);
            SwuStreamFinish finish = GetDelegate<SwuStreamFinish>(
                _nativeHandle, "hypertizen_probe_swu_stream_finish");
            if (finish == null) return Encoding.ASCII.GetBytes("ERROR finish symbol missing");
            byte[] output = new byte[65536];
            uint outputSize, origin;
            int stage;
            uint result = finish(output, (uint)output.Length,
                out outputSize, out origin, out stage);
            if (result != 0) return NativeError(result, origin, stage);
            Array.Resize(ref output, (int)outputSize);
            return output;
        }

        internal static byte[] AbortFirmwareStream()
        {
            if (!EnsureNative(out string error))
                return Encoding.ASCII.GetBytes("ERROR " + error);
            SwuStreamAbort abort = GetDelegate<SwuStreamAbort>(
                _nativeHandle, "hypertizen_probe_swu_stream_abort");
            if (abort == null) return Encoding.ASCII.GetBytes("ERROR abort symbol missing");
            abort();
            return Encoding.ASCII.GetBytes("OK");
        }

        internal static byte[] DecryptFirmwareHeader(
            byte[] requestBody, int derivation, int keySize, int mode)
        {
            if (requestBody == null || requestBody.Length <= 8)
                return Encoding.ASCII.GetBytes("ERROR malformed request body");
            byte[] salt = new byte[8];
            byte[] ciphertext = new byte[requestBody.Length - 8];
            Buffer.BlockCopy(requestBody, 0, salt, 0, salt.Length);
            Buffer.BlockCopy(requestBody, 8, ciphertext, 0, ciphertext.Length);

            IntPtr teec = dlopen("/usr/lib/libteec.so", RtldLazy | RtldGlobal);
            string nativePath = Path.Combine(AppContext.BaseDirectory, "libhypertizenprobe.so");
            IntPtr native = dlopen(nativePath, RtldLazy);
            if (native == IntPtr.Zero)
                return Encoding.ASCII.GetBytes("ERROR native helper load failed: " + DlError());
            try
            {
                SwuDecrypt decrypt = GetDelegate<SwuDecrypt>(
                    native, "hypertizen_probe_swu_decrypt");
                if (decrypt == null)
                    return Encoding.ASCII.GetBytes("ERROR decrypt symbol missing");
                byte[] output = new byte[ciphertext.Length + 32];
                uint outputSize, origin;
                int stage;
                uint result = decrypt(
                    salt, (uint)salt.Length, ciphertext, (uint)ciphertext.Length,
                    output, (uint)output.Length, out outputSize, out origin, out stage,
                    (uint)derivation, (uint)keySize, (uint)mode);
                if (result != 0)
                    return Encoding.ASCII.GetBytes(
                        $"ERROR result=0x{result:x8} origin={origin} stage={stage}");
                Array.Resize(ref output, (int)outputSize);
                return output;
            }
            finally
            {
                dlclose(native);
                if (teec != IntPtr.Zero) dlclose(teec);
            }
        }

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
            ProbeRemoteManagement(report);
            ProbeSecvideoCapture(report);
            ProbeScreenshot(report);
            ProbeSwu(report);
            report.AppendLine("DONE");
            return report.ToString();
        }

        private static void ProbeSecvideoCapture(StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("[SECVIDEO FULL-FRAME CAPTURE]");
            try
            {
                if (!EnsureNative(out string error))
                {
                    report.AppendLine("native unavailable: " + error);
                    return;
                }
                IntPtr symbol = dlsym(_nativeHandle, "hypertizen_probe_secvideo_capture");
                if (symbol == IntPtr.Zero)
                {
                    report.AppendLine("helper symbol unavailable");
                    return;
                }
                var capture = Marshal.GetDelegateForFunctionPointer<SecvideoCapture>(symbol);
                const uint width = 320;
                const uint height = 180;
                byte[] y = new byte[width * height];
                byte[] c = new byte[width * height];
                uint[] words = new uint[23];
                int result = capture(y, (uint)y.Length, c, (uint)c.Length,
                    width, height, words, (uint)words.Length);
                report.AppendLine($"result={result} output={words[2]}x{words[3]} " +
                    $"format={words[7]} yBytes={words[0]} cBytes={words[1]}");
                if (result >= 0 && words[2] > 0 && words[3] > 0 &&
                    words[2] * words[3] <= y.Length)
                {
                    bool chromaFull = words[1] >= words[0];
                    CapturedSecvideoFramePpm = ToPpmYuv(
                        y, c, words[2], words[3], chromaFull);
                    using (SHA256 sha = SHA256.Create())
                        report.AppendLine("frame sha256=" +
                            BitConverter.ToString(sha.ComputeHash(CapturedSecvideoFramePpm))
                                .Replace("-", "").ToLowerInvariant());
                }
            }
            catch (Exception ex)
            {
                report.AppendLine(ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ProbeRmEncoder(StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("[RM LOW-LEVEL H264 ENCODER]");
            try
            {
                if (!EnsureNative(out string error))
                {
                    report.AppendLine("native unavailable: " + error);
                    return;
                }
                IntPtr symbol = dlsym(_nativeHandle, "hypertizen_probe_rm_encoder");
                if (symbol == IntPtr.Zero)
                {
                    report.AppendLine("helper symbol unavailable");
                    return;
                }
                var probe = Marshal.GetDelegateForFunctionPointer<RmEncoderProbe>(symbol);
                uint[] values = new uint[16];
                int result = probe(values, (uint)values.Length);
                report.AppendLine($"final={result} init={unchecked((int)values[0])} " +
                    $"open={unchecked((int)values[1])} fd={unchecked((int)values[2])}");
                report.AppendLine($"resolution={unchecked((int)values[3])} " +
                    $"fps={unchecked((int)values[4])} bitrate={unchecked((int)values[5])} " +
                    $"subscribe={unchecked((int)values[6])} streamOn={unchecked((int)values[7])}");
                report.AppendLine($"event={unchecked((int)values[8])} " +
                    $"offset={values[9]} bytes={values[10]} attempts={values[11]} " +
                    $"streamOff={unchecked((int)values[12])} " +
                    $"unsubscribe={unchecked((int)values[13])} close={unchecked((int)values[14])}");
            }
            catch (Exception ex)
            {
                report.AppendLine(ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ProbeMediatedAlternatives(StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("[MEDIATED / SOURCE-SPECIFIC ALTERNATIVES]");
            foreach (var command in new[]
            {
                new[] { "/usr/bin/gdbus", "introspect --system --dest com.uifw.colorpick --object-path /com/uifw/colorpick" },
                new[] { "/usr/bin/gst-inspect-1.0", "tvmultihdmisrc" },
                new[] { "/usr/bin/gst-inspect-1.0", "displayencodesrc" }
            })
            {
                if (!File.Exists(command[0]))
                {
                    report.AppendLine(command[0] + " missing");
                    continue;
                }
                try
                {
                    var start = new ProcessStartInfo(command[0], command[1])
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using (Process process = Process.Start(start))
                    {
                        if (process == null)
                        {
                            report.AppendLine(Path.GetFileName(command[0]) + " failed to start");
                            continue;
                        }
                        if (!process.WaitForExit(3000))
                        {
                            try { process.Kill(); } catch { }
                            report.AppendLine(Path.GetFileName(command[0]) + " timed out");
                            continue;
                        }
                        string stdout = process.StandardOutput.ReadToEnd();
                        string stderr = process.StandardError.ReadToEnd();
                        string compact = (stdout + " " + stderr).Replace('\r', ' ').Replace('\n', ' ').Trim();
                        if (compact.Length > 500) compact = compact.Substring(0, 500);
                        report.AppendLine($"{Path.GetFileName(command[0])} exit={process.ExitCode}: {compact}");
                    }
                }
                catch (Exception ex)
                {
                    report.AppendLine(Path.GetFileName(command[0]) + " " +
                        ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int VconfGetInt(string key, out int value);

        private static void ProbeRemoteManagement(StringBuilder report)
        {
            report.AppendLine("[REMOTE MANAGEMENT]");
            foreach (string marker in new[]
            {
                "/opt/usr/apps/org.tizen.remote-management/tmp/RMStateFile",
                "/tmp/rmDaemonRun"
            })
                report.AppendLine($"marker {marker} exists={File.Exists(marker)}");

            int matches = 0;
            try
            {
                foreach (string directory in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(directory), out int pid)) continue;
                    try
                    {
                        string command = Encoding.UTF8.GetString(
                            File.ReadAllBytes(Path.Combine(directory, "cmdline"))).Replace('\0', ' ').Trim();
                        if (command.IndexOf("rmdemon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            command.IndexOf("remote-management", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            report.AppendLine($"process pid={pid} cmd={command}");
                            matches++;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { report.AppendLine("proc scan: " + ex.GetType().Name); }
            report.AppendLine("matching_processes=" + matches);
            report.AppendLine();
        }

        private static void ProbeDirectTzCapture(StringBuilder report)
        {
            report.AppendLine("[DIRECT TRUSTZONE CAPTURE]");
            try
            {
                if (!EnsureNative(out string error))
                {
                    report.AppendLine(error);
                    return;
                }
                TzCapture capture = GetDelegate<TzCapture>(
                    _nativeHandle, "hypertizen_probe_tzcapture");
                if (capture == null)
                {
                    report.AppendLine("native capture symbol missing");
                    return;
                }
                byte[] y = new byte[0x80000];
                byte[] c = new byte[0x80000];
                uint width, height, chromaFull, metadata, origin;
                int stage;
                uint result = capture(y, (uint)y.Length, c, (uint)c.Length,
                    out width, out height, out chromaFull, out metadata,
                    out origin, out stage);
                report.AppendLine($"result=0x{result:x8} origin={origin} stage={stage} " +
                    $"size={width}x{height} chroma_full={chromaFull} metadata=0x{metadata:x8}");
                if (result == 0 && width > 0 && height > 0)
                {
                    CapturedTzFramePpm = ToPpmYuv(y, c, width, height, chromaFull != 0);
                    int nonZeroY = y.Take(checked((int)(width * height))).Count(value => value != 0);
                    using (SHA256 sha = SHA256.Create())
                    {
                        string digest = BitConverter.ToString(sha.ComputeHash(CapturedTzFramePpm))
                            .Replace("-", "").ToLowerInvariant();
                        report.AppendLine($"frame bytes={CapturedTzFramePpm.Length} " +
                            $"nonzero_y={nonZeroY}/{width * height} sha256={digest} download=/tz.ppm");
                    }
                }
            }
            catch (Exception ex)
            {
                report.AppendLine(ex.GetType().Name + ": " + ex.Message);
            }
            report.AppendLine();
        }

        private static byte[] ToPpmYuv(
            byte[] yPlane, byte[] cPlane, uint width, uint height, bool chromaFull)
        {
            byte[] header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
            byte[] ppm = new byte[header.Length + checked((int)(width * height * 3))];
            Buffer.BlockCopy(header, 0, ppm, 0, header.Length);
            int output = header.Length;
            for (uint row = 0; row < height; row++)
            {
                for (uint column = 0; column < width; column++)
                {
                    int yIndex = checked((int)(row * width + column));
                    int cIndex = chromaFull
                        ? checked((int)(row * width + (column & ~1u)))
                        : checked((int)((row / 2) * width + (column & ~1u)));
                    int yy = yPlane[yIndex] - 16;
                    int u = cPlane[cIndex] - 128;
                    int v = cPlane[cIndex + 1] - 128;
                    int red = (298 * yy + 409 * v + 128) >> 8;
                    int green = (298 * yy - 100 * u - 208 * v + 128) >> 8;
                    int blue = (298 * yy + 516 * u + 128) >> 8;
                    ppm[output++] = (byte)Math.Max(0, Math.Min(255, red));
                    ppm[output++] = (byte)Math.Max(0, Math.Min(255, green));
                    ppm[output++] = (byte)Math.Max(0, Math.Min(255, blue));
                }
            }
            return ppm;
        }

        private static void ProbeV4L2(StringBuilder report)
        {
            report.AppendLine("[V4L2 VIDEO30]");
            try
            {
                using (FileStream stream = File.Open(
                    "/dev/video30", FileMode.Open, FileAccess.ReadWrite))
                {
                    int fd = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
                    byte[] capability = new byte[104];
                    int result = ioctl(fd, 0x80685600, capability);
                    report.AppendLine($"VIDIOC_QUERYCAP={result} errno={Marshal.GetLastWin32Error()}");
                    if (result == 0)
                    {
                        report.AppendLine("driver=" + ReadCString(capability, 0, 16));
                        report.AppendLine("card=" + ReadCString(capability, 16, 32));
                        report.AppendLine("bus=" + ReadCString(capability, 48, 32));
                        report.AppendLine($"version=0x{BitConverter.ToUInt32(capability, 80):x8} " +
                            $"caps=0x{BitConverter.ToUInt32(capability, 84):x8} " +
                            $"device_caps=0x{BitConverter.ToUInt32(capability, 88):x8}");

                        for (uint index = 0; index < 16; index++)
                        {
                            byte[] format = new byte[64];
                            Buffer.BlockCopy(BitConverter.GetBytes(index), 0, format, 0, 4);
                            Buffer.BlockCopy(BitConverter.GetBytes(1u), 0, format, 4, 4);
                            int enumResult = ioctl(fd, 0xc0405602, format);
                            if (enumResult != 0)
                                break;
                            uint fourcc = BitConverter.ToUInt32(format, 44);
                            report.AppendLine($"format[{index}]={ReadCString(format, 12, 32)} " +
                                $"fourcc={FourCc(fourcc)} flags=0x{BitConverter.ToUInt32(format, 8):x8}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report.AppendLine(ex.GetType().Name + ": " + ex.Message);
            }
            report.AppendLine();
        }

        private static string ReadCString(byte[] data, int offset, int length)
        {
            int end = offset;
            while (end < offset + length && data[end] != 0) end++;
            return Encoding.ASCII.GetString(data, offset, end - offset);
        }

        private static string FourCc(uint value)
        {
            return new string(new[]
            {
                (char)(value & 0xff), (char)((value >> 8) & 0xff),
                (char)((value >> 16) & 0xff), (char)((value >> 24) & 0xff)
            });
        }

        private static void ProbeDisplayEncode(StringBuilder report)
        {
            report.AppendLine("[DISPLAY ENCODE GSTREAMER]");
            string outputPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "data", "hypertizen-display.h264"));
            try
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/gst-launch-1.0",
                    Arguments = "-q displayencodesrc dst-w=320 dst-h=180 " +
                        "frame-rate=24 ! h264parse ! filesink location=" + outputPath,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false,
                    CreateNoWindow = true
                };
                using (Process process = Process.Start(startInfo))
                {
                    var errors = new StringBuilder();
                    process.ErrorDataReceived += (sender, args) =>
                    {
                        if (!string.IsNullOrEmpty(args.Data))
                            errors.AppendLine(args.Data);
                    };
                    process.BeginErrorReadLine();
                    bool exited = process.WaitForExit(8000);
                    if (!exited)
                        process.Kill();
                    process.WaitForExit(1000);
                    report.AppendLine("started=true exited=" + exited +
                        (exited ? " code=" + process.ExitCode : " killed-after=8s"));
                    if (errors.Length > 0)
                        report.AppendLine("stderr=" + errors.ToString().Trim().Replace('\n', ' '));
                }

                if (File.Exists(outputPath))
                {
                    CapturedDisplayH264 = File.ReadAllBytes(outputPath);
                    using (SHA256 sha = SHA256.Create())
                    {
                        string digest = BitConverter.ToString(
                            sha.ComputeHash(CapturedDisplayH264)).Replace("-", "").ToLowerInvariant();
                        report.AppendLine($"stream bytes={CapturedDisplayH264.Length} " +
                            $"sha256={digest} download=/display.h264");
                    }
                }
                else
                {
                    report.AppendLine("stream file was not created");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine(ex.GetType().Name + ": " + ex.Message);
            }
            report.AppendLine();
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
