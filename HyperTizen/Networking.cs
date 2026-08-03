using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Google.FlatBuffers;
using hyperhdrnet;
using Tizen.Messaging.Messages;

namespace HyperTizen
{
    public static class Networking
    {
        private static readonly object _lock = new object();
        private static TcpClient _client;
        private static NetworkStream _stream;

        // ── Dedicated sender thread ───────────────────────────────────────────
        // The sender thread is the ONLY writer to the TCP stream.
        // The capture loop calls PostImage() to hand off the latest frame;
        // if the sender is still busy, the previous pending frame is overwritten (frame drop).
        // This completely avoids thread-pool usage for networking.

        private sealed class FrameData
        {
            public readonly byte[] YData, UVData;
            public readonly int    Width, Height;
            public FrameData(byte[] y, byte[] uv, int w, int h)
            { YData = y; UVData = uv; Width = w; Height = h; }
        }

        private static volatile FrameData _pendingFrame = null;
        private static readonly System.Threading.ManualResetEventSlim _frameAvailable =
            new System.Threading.ManualResetEventSlim(false);

        public static TcpClient client
        {
            get { lock (_lock) { return _client; } }
            set { lock (_lock) { _client = value; } }
        }

        public static NetworkStream stream
        {
            get { lock (_lock) { return _stream; } }
            set { lock (_lock) { _stream = value; } }
        }

        public static void DisconnectClient()
        {
            lock (_lock)
            {
                try
                {
                    if (_stream != null)
                    {
                        _stream.Flush();
                        _stream.Close(500);
                    }
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"DisconnectClient: Stream close error: {ex.Message}");
                }

                try
                {
                    if (_client != null)
                    {
                        _client.Close();
                    }
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning, $"DisconnectClient: Client close error: {ex.Message}");
                }

                // CRITICAL FIX: Null out references to prevent race conditions
                _stream = null;
                _client = null;
                Helper.Log.Write(Helper.eLogType.Info, "DisconnectClient: Client and stream nulled");
            }
        }

        public static void SendRegister()
        {
            try
            {
                // Validate before connecting
                if (string.IsNullOrEmpty(Globals.Instance.ServerIp) || Globals.Instance.ServerPort <= 0)
                {
                    Helper.Log.Write(Helper.eLogType.Error, 
                        $"TCP FAILED: Bad config {Globals.Instance.ServerIp ?? "null"}:{Globals.Instance.ServerPort}");
                    return;
                }

                Helper.Log.Write(Helper.eLogType.Info,
                    $"TCP: Connecting to {Globals.Instance.ServerIp}:{Globals.Instance.ServerPort}");

                lock (_lock)
                {
                    _client = new TcpClient(Globals.Instance.ServerIp, Globals.Instance.ServerPort);

                    // Disable Nagle's algorithm to prevent buffering delays
                    _client.NoDelay = true;

                    Helper.Log.Write(Helper.eLogType.Info, "TCP: Socket created (NoDelay=true)");

                    if (_client == null || !_client.Connected)
                    {
                        Helper.Log.Write(Helper.eLogType.Error, "TCP FAILED: Client null/not connected");
                        return;
                    }

                    Helper.Log.Write(Helper.eLogType.Info, "TCP: Connected! Getting stream...");

                    _stream = _client.GetStream();
                    if (_stream == null)
                    {
                        Helper.Log.Write(Helper.eLogType.Error, "TCP FAILED: No stream");
                        return;
                    }

                    Helper.Log.Write(Helper.eLogType.Info, "TCP: Stream OK, creating FlatBuffer msg...");

                    byte[] registrationMessage = Networking.CreateRegistrationMessage();
                    if (registrationMessage == null)
                    {
                        Helper.Log.Write(Helper.eLogType.Error, "TCP FAILED: No FlatBuffer message");
                        return;
                    }

                    // HyperHDR expects BIG-ENDIAN 4-byte size prefix (not FlatBuffers standard little-endian)
                    var header = new byte[4];
                    header[0] = (byte)((registrationMessage.Length >> 24) & 0xFF);  // Big-endian
                    header[1] = (byte)((registrationMessage.Length >> 16) & 0xFF);
                    header[2] = (byte)((registrationMessage.Length >> 8) & 0xFF);
                    header[3] = (byte)(registrationMessage.Length & 0xFF);

                    Helper.Log.Write(Helper.eLogType.Info,
                        $"TCP: Sending {registrationMessage.Length} bytes with big-endian header...");

                    _stream.Write(header, 0, header.Length);
                    _stream.Write(registrationMessage, 0, registrationMessage.Length);

                    // CRITICAL FIX: Flush the stream to ensure data is actually sent!
                    // Without this, data stays in buffer and HyperHDR never receives it
                    _stream.Flush();

                    Helper.Log.Write(Helper.eLogType.Info, "TCP: Data sent and flushed, waiting for reply...");
                }

                ReadRegisterReply();
                
                Helper.Log.Write(Helper.eLogType.Info, "TCP OK: Fully registered!");
            }
            catch (SocketException ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, 
                    $"SOCKET ERROR: {ex.Message} (Code:{ex.ErrorCode})");
                DisconnectClient();
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, 
                    $"ERROR: {ex.GetType().Name}: {ex.Message}");
                DisconnectClient();
            }
        }

        /// <summary>
        /// Post a captured frame for sending. Returns immediately — the sender thread picks it up.
        /// If the sender is still busy with the previous frame, that frame is overwritten (drop oldest).
        /// Never blocks, never touches the thread pool.
        /// </summary>
        public static void PostImage(byte[] yData, byte[] uvData, int width, int height)
        {
            if (yData == null || uvData == null || width <= 0 || height <= 0)
                return;
            // Overwrite any pending frame (drop old, keep latest)
            _pendingFrame = new FrameData(yData, uvData, width, height);
            _frameAvailable.Set();
        }

        /// <summary>
        /// Start the dedicated sender thread. Call once after TCP registration succeeds.
        /// </summary>
        public static void StartSenderThread()
        {
            var t = new System.Threading.Thread(SenderThreadLoop)
            {
                IsBackground = true,
                Name = "HyperTizen-Sender"
            };
            t.Start();
            Helper.Log.Write(Helper.eLogType.Info, "Networking: Sender thread started");
        }

        private static void SenderThreadLoop()
        {
            var replyBuf = new byte[1024];

            while (true)
            {
                // Block until a frame is posted
                _frameAvailable.Wait();
                _frameAvailable.Reset();

                // Take the latest frame (may be null if Reset raced with Set)
                FrameData frame = System.Threading.Interlocked.Exchange(ref _pendingFrame, null);
                if (frame == null)
                    continue;

                try
                {
                    // ── Validate connection ────────────────────────────────────
                    NetworkStream localStream;
                    lock (_lock)
                    {
                        if (_client == null || !_client.Connected || _stream == null)
                            continue;
                        localStream = _stream;
                    }

                    // ── Build FlatBuffer ───────────────────────────────────────
                    byte[] message = CreateFlatBufferMessage(frame.YData, frame.UVData, frame.Width, frame.Height);
                    if (message == null)
                        continue;

                    // ── Write header + message (sync) ──────────────────────────
                    var header = new byte[4];
                    header[0] = (byte)((message.Length >> 24) & 0xFF);
                    header[1] = (byte)((message.Length >> 16) & 0xFF);
                    header[2] = (byte)((message.Length >> 8) & 0xFF);
                    header[3] = (byte)(message.Length & 0xFF);

                    localStream.Write(header, 0, header.Length);
                    localStream.Write(message, 0, message.Length);
                    localStream.Flush();

                    // ── Read reply (sync, 500ms timeout) ──────────────────────
                    // ReadTimeout is set on the stream; IOException on timeout.
                    try
                    {
                        localStream.ReadTimeout = 500;
                        int bytesRead = localStream.Read(replyBuf, 0, replyBuf.Length);
                        if (bytesRead > 0)
                        {
                            var replyData = new byte[bytesRead];
                            Array.Copy(replyBuf, replyData, bytesRead);
                            Reply reply = ParseReply(replyData);
                            if (!string.IsNullOrEmpty(reply.Error))
                            {
                                Helper.Log.Write(Helper.eLogType.Error,
                                    "SenderThread: Reply_Error: " + reply.Error);
                                DisconnectClient();
                            }
                        }
                        else
                        {
                            Helper.Log.Write(Helper.eLogType.Error,
                                "SenderThread: No answer from server — disconnecting");
                            DisconnectClient();
                        }
                    }
                    catch (System.IO.IOException)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning,
                            "SenderThread: Reply timeout (500ms) — disconnecting");
                        DisconnectClient();
                    }
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Error,
                        $"SenderThread: Exception: {ex.GetType().Name}: {ex.Message}");
                    DisconnectClient();
                }
            }
        }
        static byte[] CreateFlatBufferMessage(byte[] yData, byte[] uvData, int width, int height)
        {
            // ENHANCED NULL SAFETY: Detailed checks with logging
            try
            {
                lock (_lock)
                {
                    if (_client == null)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, "CreateFlatBufferMessage: client is null");
                        return null;
                    }

                    if (_client.Client == null)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, "CreateFlatBufferMessage: client.Client is null");
                        return null;
                    }

                    if (!_client.Connected)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, "CreateFlatBufferMessage: client not connected");
                        return null;
                    }

                    if (_stream == null)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, "CreateFlatBufferMessage: stream is null");
                        return null;
                    }
                }
            }
            catch (NullReferenceException ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"CreateFlatBufferMessage: NullRef during validation: {ex.Message}");
                return null;
            }
            catch (ObjectDisposedException ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"CreateFlatBufferMessage: Object disposed: {ex.Message}");
                return null;
            }

            // CRITICAL: Validate buffer parameters before using them
            if (yData == null)
            {
                Helper.Log.Write(Helper.eLogType.Error, "CreateFlatBufferMessage: yData is null");
                return null;
            }

            if (uvData == null)
            {
                Helper.Log.Write(Helper.eLogType.Error, "CreateFlatBufferMessage: uvData is null");
                return null;
            }

            if (width <= 0 || height <= 0)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"CreateFlatBufferMessage: Invalid dimensions ({width}x{height})");
                return null;
            }

            // CRITICAL: Validate buffer sizes match NV12 format
            int expectedYSize = width * height;
            int expectedUVSize = (width * height) / 2;

            if (yData.Length != expectedYSize)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"CreateFlatBufferMessage: Invalid Y buffer size. Expected {expectedYSize}, got {yData.Length}");
                return null;
            }

            if (uvData.Length != expectedUVSize)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"CreateFlatBufferMessage: Invalid UV buffer size. Expected {expectedUVSize}, got {uvData.Length}");
                return null;
            }

            var builder = new FlatBufferBuilder(yData.Length + uvData.Length + 100);

            var yVector = NV12Image.CreateDataYVector(builder, yData);
            var uvVector = NV12Image.CreateDataUvVector(builder, uvData);

            NV12Image.StartNV12Image(builder);
            NV12Image.AddDataY(builder, yVector);
            NV12Image.AddDataUv(builder, uvVector);
            NV12Image.AddWidth(builder, width);
            NV12Image.AddHeight(builder, height);
            NV12Image.AddStrideY(builder, width);  //TODO: Check if this is correct
            NV12Image.AddStrideUv(builder, width);
            var nv12Image = NV12Image.EndNV12Image(builder);

            Image.StartImage(builder);
            Image.AddDataType(builder, ImageType.NV12Image);
            Image.AddData(builder, nv12Image.Value);
            Image.AddDuration(builder, -1);
            var imageOffset = Image.EndImage(builder);

            Request.StartRequest(builder);
            Request.AddCommandType(builder, Command.Image);
            Request.AddCommand(builder, imageOffset.Value);
            var requestOffset = Request.EndRequest(builder);

            // Use regular Finish (NOT FinishSizePrefixed)
            // HyperHDR expects big-endian size prefix which we'll add manually in SendMessageAndReceiveReplyAsync()
            builder.Finish(requestOffset.Value);
            return builder.SizedByteArray();
        }

        static Reply ParseReply(byte[] receivedData)
        {
            var byteBuffer = new ByteBuffer(receivedData, 4); //shift for header
            return Reply.GetRootAsReply(byteBuffer);
        }

        public static byte[] CreateRegistrationMessage()
        {
            // ENHANCED NULL SAFETY: Detailed checks with logging
            try
            {
                lock (_lock)
                {
                    if (_client == null)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, "CreateRegistrationMessage: client is null");
                        return null;
                    }

                    if (_client.Client == null)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, "CreateRegistrationMessage: client.Client is null");
                        return null;
                    }

                    if (!_client.Connected)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, "CreateRegistrationMessage: client not connected");
                        return null;
                    }

                    if (_stream == null)
                    {
                        Helper.Log.Write(Helper.eLogType.Warning, "CreateRegistrationMessage: stream is null");
                        return null;
                    }
                }
            }
            catch (NullReferenceException ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"CreateRegistrationMessage: NullRef during validation: {ex.Message}");
                return null;
            }
            catch (ObjectDisposedException ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"CreateRegistrationMessage: Object disposed: {ex.Message}");
                return null;
            }

            var builder = new FlatBufferBuilder(256); //TODO:Check how to calculate correctly

            var originOffset = builder.CreateString("HyperTizen");

            Register.StartRegister(builder);
            Register.AddPriority(builder, 123);
            Register.AddOrigin(builder, originOffset);
            var registerOffset = Register.EndRegister(builder);

            Request.StartRequest(builder);
            Request.AddCommandType(builder, Command.Register);
            Request.AddCommand(builder, registerOffset.Value);
            var requestOffset = Request.EndRequest(builder);

            // Use regular Finish (NOT FinishSizePrefixed)
            // HyperHDR expects big-endian size prefix which we'll add manually in SendRegister()
            builder.Finish(requestOffset.Value);
            byte[] message = builder.SizedByteArray();

            return message;
        }

        public static void ReadRegisterReply()
        {
            try
            {
                lock (_lock)
                {
                    if (_client == null || !_client.Connected || _stream == null)
                    {
                        Helper.Log.Write(Helper.eLogType.Error, "ReadRegisterReply: No client/stream");
                        return;
                    }

                    Helper.Log.Write(Helper.eLogType.Info, "ReadRegisterReply: Waiting for server reply...");

                    // Set read timeout to prevent infinite blocking
                    _stream.ReadTimeout = 5000; // 5 second timeout

                    byte[] buffer = new byte[1024];
                    int bytesRead = _stream.Read(buffer, 0, buffer.Length);

                    if (bytesRead > 0)
                    {
                        Helper.Log.Write(Helper.eLogType.Info, $"ReadRegisterReply: Got {bytesRead} bytes");

                        byte[] replyData = new byte[bytesRead];
                        Array.Copy(buffer, replyData, bytesRead);

                        // Log raw reply bytes for debugging
                        Reply reply = ParseReply(replyData);

                        if (reply.Registered > 0)
                        {
                            Helper.Log.Write(Helper.eLogType.Info, "ReadRegisterReply: REGISTERED OK!");
                        }
                        else
                        {
                            Helper.Log.Write(Helper.eLogType.Error, $"ReadRegisterReply: NOT registered (code: {reply.Registered})");
                        }
                    }
                    else
                    {
                        Helper.Log.Write(Helper.eLogType.Error, "ReadRegisterReply: No data received");
                    }
                }
            }
            catch (System.IO.IOException ex)
            {
                // Log stream state at timeout
                string streamState;
                lock (_lock)
                {
                    streamState = _stream != null ?
                        $"CanRead={_stream.CanRead}, CanWrite={_stream.CanWrite}, DataAvail={_stream.DataAvailable}" :
                        "stream is null";
                }

                Helper.Log.Write(Helper.eLogType.Error,
                    $"ReadRegisterReply TIMEOUT: {ex.Message}");
                DisconnectClient();
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error,
                    $"ReadRegisterReply ERROR: {ex.GetType().Name}: {ex.Message}");
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"ReadRegisterReply ERROR: Stack trace: {ex.StackTrace}");
                DisconnectClient();
            }
        }

    }
}
