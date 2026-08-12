#!/usr/bin/env python3
"""Full-frame media-source bridge for HyperHDR.

The first adapter mirrors a local Plex playback session.  It reads the source
file through Plex HTTP range requests, follows the TV's reported playback
position, decodes low-resolution NV12 frames with ffmpeg, and submits them to
HyperHDR's FlatBuffers API. Future application-specific adapters can implement
MediaSourceAdapter without changing the decoder or HyperHDR sink.
"""

from __future__ import annotations

import argparse
import select
import os
import socket
import struct
import subprocess
import sys
import time
import urllib.request
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from typing import BinaryIO, Optional

import flatbuffers


@dataclass(frozen=True)
class SourceSession:
    identity: str
    title: str
    state: str
    position_seconds: float
    stream_url: str
    video_codec: str = ""
    video_profile: str = ""
    timeline_calibrated: bool = False


class MediaSourceAdapter:
    name = "unknown"

    def current_session(self) -> Optional[SourceSession]:
        raise NotImplementedError


class PlexSessionAdapter(MediaSourceAdapter):
    name = "Plex session"

    def __init__(self, base_url: str, tv_model: str, timeout: float = 3.0,
                 plex_path_prefix: str = "", local_media_root: str = ""):
        self.base_url = base_url.rstrip("/")
        self.tv_model = tv_model
        self.timeout = timeout
        self._timeline_identity: Optional[str] = None
        self._timeline_state: Optional[str] = None
        self._reported_position = 0.0
        self._position_anchor = 0.0
        self._anchor_time = 0.0
        self._timeline_calibrated = False
        self.plex_path_prefix = plex_path_prefix.rstrip("/")
        self.local_media_root = local_media_root.rstrip("/")
        self._stream_details: dict[str, tuple[str, str, str]] = {}

    def _source_for_part(self, part: ET.Element) -> Optional[str]:
        plex_path = part.get("file", "")
        if (self.plex_path_prefix and self.local_media_root
                and (plex_path == self.plex_path_prefix
                     or plex_path.startswith(self.plex_path_prefix + "/"))):
            suffix = plex_path[len(self.plex_path_prefix):].lstrip("/")
            local_path = os.path.join(self.local_media_root, suffix)
            if os.path.isfile(local_path):
                return local_path
            print(f"Mapped media file is missing: {local_path}",
                  file=sys.stderr, flush=True)
        part_key = part.get("key")
        return self.base_url + part_key if part_key else None

    def _xml(self, path: str) -> ET.Element:
        request = urllib.request.Request(
            self.base_url + path,
            headers={"X-Plex-Client-Identifier": "HyperTizenSourceBridge"},
        )
        with urllib.request.urlopen(request, timeout=self.timeout) as response:
            return ET.fromstring(response.read())

    def current_session(self) -> Optional[SourceSession]:
        root = self._xml("/status/sessions")
        for video in root.findall("Video"):
            player = video.find("Player")
            if player is None:
                continue
            if self.tv_model and player.get("model") != self.tv_model:
                continue

            metadata_key = video.get("key")
            if not metadata_key:
                continue
            stream_details = self._stream_details.get(metadata_key)
            if stream_details is None:
                metadata = self._xml(metadata_key)
                media = metadata.find("./Video/Media")
                part = metadata.find("./Video/Media/Part")
                if part is None:
                    continue
                stream_url = self._source_for_part(part)
                if not stream_url:
                    continue
                video_codec = media.get("videoCodec", "") if media is not None else ""
                video_profile = media.get("videoProfile", "") if media is not None else ""
                stream_details = (stream_url, video_codec, video_profile)
                self._stream_details[metadata_key] = stream_details
            stream_url, video_codec, video_profile = stream_details

            identity = (video.get("playbackSessionId")
                        or player.get("playbackSessionId")
                        or video.get("sessionKey", metadata_key))
            state = player.get("state", "unknown")
            reported_position = float(video.get("viewOffset", "0")) / 1000.0
            observed_at = time.monotonic()
            report_changed = abs(reported_position - self._reported_position) > 0.001
            state_changed = state != self._timeline_state
            identity_changed = identity != self._timeline_identity
            if identity_changed:
                self._timeline_calibrated = False
            elif report_changed:
                self._timeline_calibrated = True
            if identity_changed or report_changed or state_changed:
                self._timeline_identity = identity
                self._timeline_state = state
                self._reported_position = reported_position
                self._position_anchor = reported_position
                self._anchor_time = observed_at

            position = self._position_anchor
            if state == "playing":
                position += max(0.0, observed_at - self._anchor_time)

            return SourceSession(
                identity=identity,
                title=video.get("title", "Plex video"),
                state=state,
                position_seconds=position,
                stream_url=stream_url,
                video_codec=video_codec,
                video_profile=video_profile,
                timeline_calibrated=self._timeline_calibrated,
            )
        return None


class HyperHdrFlatBufferSink:
    def __init__(self, host: str, port: int, priority: int):
        self.host = host
        self.port = port
        self.priority = priority
        self._socket: Optional[socket.socket] = None

    def close(self) -> None:
        if self._socket:
            self._socket.close()
        self._socket = None

    def _connect(self) -> None:
        self.close()
        self._socket = socket.create_connection((self.host, self.port), timeout=3)
        self._socket.setblocking(False)
        self._send_message(self._register_message())

    def send_nv12(self, frame: bytes, width: int, height: int) -> None:
        if self._socket is None:
            self._connect()
        try:
            self._send_message(self._image_message(frame, width, height))
            try:
                while self._socket.recv(4096, socket.MSG_DONTWAIT):
                    pass
            except BlockingIOError:
                pass
        except Exception:
            self.close()
            raise

    def _send_message(self, message: bytes) -> None:
        packet = memoryview(struct.pack(">I", len(message)) + message)
        deadline = time.monotonic() + 5
        while packet:
            try:
                sent = self._socket.send(packet)
                if sent == 0:
                    raise ConnectionError("HyperHDR closed the connection")
                packet = packet[sent:]
            except BlockingIOError:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("HyperHDR send timed out")
                _, writable, _ = select.select([], [self._socket], [], remaining)
                if not writable:
                    raise TimeoutError("HyperHDR send timed out")

    def _register_message(self) -> bytes:
        builder = flatbuffers.Builder(256)
        origin = builder.CreateString("PlexMirror")
        builder.StartObject(2)
        builder.PrependInt32Slot(1, self.priority, 0)
        builder.PrependUOffsetTRelativeSlot(0, origin, 0)
        registration = builder.EndObject()
        builder.StartObject(2)
        builder.PrependUOffsetTRelativeSlot(1, registration, 0)
        builder.PrependByteSlot(0, 4, 0)  # Command.Register
        request = builder.EndObject()
        builder.Finish(request)
        return bytes(builder.Output())

    @staticmethod
    def _image_message(frame: bytes, width: int, height: int) -> bytes:
        y_size = width * height
        if len(frame) != y_size + y_size // 2:
            raise ValueError("invalid NV12 frame size")
        builder = flatbuffers.Builder(len(frame) + 256)
        y_vector = builder.CreateByteVector(frame[:y_size])
        uv_vector = builder.CreateByteVector(frame[y_size:])

        builder.StartObject(6)
        builder.PrependUOffsetTRelativeSlot(0, y_vector, 0)
        builder.PrependUOffsetTRelativeSlot(1, uv_vector, 0)
        builder.PrependInt32Slot(2, width, 0)
        builder.PrependInt32Slot(3, height, 0)
        builder.PrependInt32Slot(4, width, 0)
        builder.PrependInt32Slot(5, width, 0)
        nv12 = builder.EndObject()

        builder.StartObject(3)
        builder.PrependByteSlot(0, 2, 0)  # ImageType.NV12Image
        builder.PrependUOffsetTRelativeSlot(1, nv12, 0)
        builder.PrependInt32Slot(2, -1, -1)
        image = builder.EndObject()

        builder.StartObject(2)
        builder.PrependByteSlot(0, 2, 0)  # Command.Image
        builder.PrependUOffsetTRelativeSlot(1, image, 0)
        request = builder.EndObject()
        builder.Finish(request)
        return bytes(builder.Output())


def read_exact(stream: BinaryIO, size: int) -> bytes:
    chunks = bytearray()
    while len(chunks) < size:
        chunk = stream.read(size - len(chunks))
        if not chunk:
            raise EOFError
        chunks.extend(chunk)
    return bytes(chunks)


class SourceBridge:
    def __init__(self, adapter: MediaSourceAdapter, sink: HyperHdrFlatBufferSink,
                 width: int, height: int, fps: int, sync_lead: float,
                 hardware_decoder: str):
        self.adapter = adapter
        self.sink = sink
        self.width = width
        self.height = height
        self.fps = fps
        self.sync_lead = sync_lead
        self.hardware_decoder = hardware_decoder
        self._disabled_hardware_modes: set[tuple[str, str]] = set()

    def _hardware_mode(self, codec: str) -> tuple[Optional[str], Optional[str]]:
        codec = codec.lower()
        cuda_decoder = {"hevc": "hevc_cuvid", "h265": "hevc_cuvid",
                        "h264": "h264_cuvid", "avc": "h264_cuvid"}.get(codec)
        if self.hardware_decoder == "off":
            return None, None
        has_cuda_device = os.path.exists("/dev/dxg") or os.path.exists("/dev/nvidia0")
        has_vaapi_device = os.path.exists("/dev/dri/renderD128")
        if (self.hardware_decoder in ("auto", "cuda") and has_cuda_device
                and cuda_decoder
                and ("cuda", codec) not in self._disabled_hardware_modes):
            return "cuda", cuda_decoder
        if (self.hardware_decoder in ("auto", "vaapi") and has_vaapi_device
                and ("vaapi", codec) not in self._disabled_hardware_modes):
            return "vaapi", None
        return None, None

    def run(self, run_seconds: float = 0) -> None:
        deadline = time.monotonic() + run_seconds if run_seconds else None
        while deadline is None or time.monotonic() < deadline:
            try:
                session = self.adapter.current_session()
                if session is None or session.state != "playing":
                    self.sink.close()
                    print("Waiting for a playing source session...", flush=True)
                    time.sleep(1)
                    continue
                self._mirror_session(session, deadline)
            except KeyboardInterrupt:
                break
            except Exception as error:
                self.sink.close()
                print(f"Bridge retry: {type(error).__name__}: {error}",
                      file=sys.stderr, flush=True)
                time.sleep(1)
        self.sink.close()

    def _mirror_session(self, session: SourceSession,
                        deadline: Optional[float]) -> None:
        hardware_mode, cuda_decoder = self._hardware_mode(session.video_codec)
        command = [
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-re"
        ]
        if hardware_mode == "cuda":
            command += ["-c:v", cuda_decoder,
                        "-resize", f"{self.width}x{self.height}"]
        elif hardware_mode == "vaapi":
            command += ["-hwaccel", "vaapi", "-hwaccel_device",
                        "/dev/dri/renderD128", "-hwaccel_output_format", "vaapi"]
        command += [
            "-ss", f"{session.position_seconds + self.sync_lead:.3f}",
            "-i", session.stream_url, "-an", "-vf"
        ]
        centered_scale = (
            f"scale={self.width}:{self.height}:force_original_aspect_ratio=decrease:"
            "flags=fast_bilinear,"
            f"pad={self.width}:{self.height}:(ow-iw)/2:(oh-ih)/2:color=black,"
            "setsar=1")
        if hardware_mode == "cuda":
            command += [f"fps={self.fps},format=nv12"]
        elif hardware_mode == "vaapi":
            # Kaby Lake / HD 630 decodes HEVC Main10 in hardware but its VAAPI
            # VPP cannot create a Main10 -> NV12 scaling pipeline. Download the
            # decoded surface and do only the inexpensive 320x180 scale on CPU.
            download_format = ("p010le" if "10" in session.video_profile
                               else "nv12")
            command += [(f"hwdownload,format={download_format},vflip,"
                         f"{centered_scale},"
                         f"format=nv12,fps={self.fps}")]
        else:
            command += [f"{centered_scale},fps={self.fps}"]
        command += [
            "-pix_fmt", "nv12", "-f", "rawvideo", "pipe:1",
        ]
        print(f"Mirroring {session.title!r} at {session.position_seconds:.3f}s "
              f"({self.width}x{self.height}@{self.fps}, "
              f"decoder={hardware_mode or 'software'}, source="
              f"{'local' if os.path.isfile(session.stream_url) else 'plex-http'}, "
              f"profile={session.video_profile or 'unknown'})",
              flush=True)
        # Keep FFmpeg diagnostics in the supervisor log. Its stdout contains
        # raw NV12 frames, while inherited stderr is redirected by the
        # supervisor to /state/source-bridge.log.
        process = subprocess.Popen(command, stdout=subprocess.PIPE)
        started = time.monotonic()
        sync_interval = 1.0 if session.timeline_calibrated else 0.2
        next_sync = started + sync_interval
        last_server_position = session.position_seconds
        frames = 0
        first_frame_at = None
        last_frame_at = None
        try:
            while process.poll() is None:
                if deadline is not None and time.monotonic() >= deadline:
                    return
                frame = read_exact(process.stdout,
                                   self.width * self.height * 3 // 2)
                self.sink.send_nv12(frame, self.width, self.height)
                frame_time = time.monotonic()
                if first_frame_at is None:
                    first_frame_at = frame_time
                last_frame_at = frame_time
                frames += 1

                now = time.monotonic()
                if now < next_sync:
                    continue
                current = self.adapter.current_session()
                expected = session.position_seconds + (now - started)
                changed_position = (current is not None
                                    and abs(current.position_seconds
                                            - last_server_position) > 0.2)
                seeked = (changed_position
                          and abs(current.position_seconds - expected) > 30.0)
                became_calibrated = (current is not None
                                     and current.timeline_calibrated
                                     and not session.timeline_calibrated)
                if (current is None or current.state != "playing"
                        or current.identity != session.identity or seeked
                        or became_calibrated):
                    print("Playback changed; resynchronizing", flush=True)
                    return
                if changed_position:
                    last_server_position = current.position_seconds
                next_sync = now + sync_interval
        finally:
            process.terminate()
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                process.kill()
            elapsed = max(time.monotonic() - started, 0.001)
            active = max((last_frame_at - first_frame_at)
                         if first_frame_at is not None and last_frame_at is not None
                         else 0, 0.001)
            steady_fps = (frames - 1) / active if frames > 1 else 0
            startup = (first_frame_at - started) if first_frame_at else elapsed
            if frames == 0 and hardware_mode:
                self._disabled_hardware_modes.add(
                    (hardware_mode, session.video_codec.lower()))
                print(f"Hardware decoder {hardware_mode} failed; falling back to software",
                      file=sys.stderr, flush=True)
            print(f"Session pass: {frames} frames, steady {steady_fps:.2f} FPS, "
                  f"startup {startup:.3f}s", flush=True)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--plex-url", default="http://192.168.10.10:32400")
    parser.add_argument("--tv-model", default="QE77S95FATXXH")
    parser.add_argument("--hyperhdr-host", default="192.168.10.10")
    parser.add_argument("--hyperhdr-port", type=int, default=19400)
    parser.add_argument("--priority", type=int, default=120)
    parser.add_argument("--width", type=int, default=320)
    parser.add_argument("--height", type=int, default=180)
    parser.add_argument("--fps", type=int, default=24)
    parser.add_argument("--sync-lead", type=float, default=1.0,
                        help="Seek this many seconds ahead to offset decoder startup")
    parser.add_argument("--hardware-decoder",
                        choices=("auto", "cuda", "vaapi", "off"),
                        default="auto")
    parser.add_argument("--plex-path-prefix", default="",
                        help="Plex metadata path prefix mapped into this host")
    parser.add_argument("--local-media-root", default="",
                        help="Local read-only root corresponding to Plex path prefix")
    parser.add_argument("--run-seconds", type=float, default=0,
                        help="Stop after N seconds; zero runs continuously")
    args = parser.parse_args()

    adapter = PlexSessionAdapter(
        args.plex_url, args.tv_model,
        plex_path_prefix=args.plex_path_prefix,
        local_media_root=args.local_media_root)
    sink = HyperHdrFlatBufferSink(args.hyperhdr_host, args.hyperhdr_port,
                                  args.priority)
    SourceBridge(adapter, sink, args.width, args.height, args.fps,
                 args.sync_lead, args.hardware_decoder).run(
        args.run_seconds)


if __name__ == "__main__":
    main()
