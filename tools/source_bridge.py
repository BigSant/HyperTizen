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


class MediaSourceAdapter:
    name = "unknown"

    def current_session(self) -> Optional[SourceSession]:
        raise NotImplementedError


class PlexSessionAdapter(MediaSourceAdapter):
    name = "Plex session"

    def __init__(self, base_url: str, tv_model: str, timeout: float = 3.0):
        self.base_url = base_url.rstrip("/")
        self.tv_model = tv_model
        self.timeout = timeout

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
            metadata = self._xml(metadata_key)
            part = metadata.find("./Video/Media/Part")
            if part is None or not part.get("key"):
                continue

            return SourceSession(
                identity=video.get("playbackSessionId")
                or player.get("playbackSessionId")
                or video.get("sessionKey", metadata_key),
                title=video.get("title", "Plex video"),
                state=player.get("state", "unknown"),
                position_seconds=float(video.get("viewOffset", "0")) / 1000.0,
                stream_url=self.base_url + part.get("key"),
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
                 width: int, height: int, fps: int, sync_lead: float):
        self.adapter = adapter
        self.sink = sink
        self.width = width
        self.height = height
        self.fps = fps
        self.sync_lead = sync_lead

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
        command = [
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-re",
            "-ss", f"{session.position_seconds + self.sync_lead:.3f}",
            "-i", session.stream_url,
            "-an", "-vf", f"scale={self.width}:{self.height},fps={self.fps}",
            "-pix_fmt", "nv12", "-f", "rawvideo", "pipe:1",
        ]
        print(f"Mirroring {session.title!r} at {session.position_seconds:.3f}s "
              f"({self.width}x{self.height}@{self.fps})", flush=True)
        process = subprocess.Popen(command, stdout=subprocess.PIPE,
                                   stderr=subprocess.DEVNULL)
        started = time.monotonic()
        next_sync = started + 1
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
                          and abs(current.position_seconds - expected) > 10.0)
                if (current is None or current.state != "playing"
                        or current.identity != session.identity or seeked):
                    print("Playback changed; resynchronizing", flush=True)
                    return
                if changed_position:
                    last_server_position = current.position_seconds
                next_sync = now + 1
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
    parser.add_argument("--sync-lead", type=float, default=0.8,
                        help="Seek this many seconds ahead to offset decoder startup")
    parser.add_argument("--run-seconds", type=float, default=0,
                        help="Stop after N seconds; zero runs continuously")
    args = parser.parse_args()

    adapter = PlexSessionAdapter(args.plex_url, args.tv_model)
    sink = HyperHdrFlatBufferSink(args.hyperhdr_host, args.hyperhdr_port,
                                  args.priority)
    SourceBridge(adapter, sink, args.width, args.height, args.fps,
                 args.sync_lead).run(
        args.run_seconds)


if __name__ == "__main__":
    main()
