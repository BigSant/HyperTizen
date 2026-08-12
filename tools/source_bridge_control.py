#!/usr/bin/env python3
"""HTTP supervisor used by controls.html to manage source_bridge.py."""

from __future__ import annotations

import argparse
import json
import os
import signal
import subprocess
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Optional


def terminate_stale_bridges(bridge_name: str) -> None:
    """Remove bridge process groups orphaned by a previous supervisor."""
    own_pid = os.getpid()
    for entry in Path("/proc").iterdir():
        if not entry.name.isdigit() or int(entry.name) == own_pid:
            continue
        try:
            arguments = (entry / "cmdline").read_bytes().split(b"\0")
            names = [Path(value.decode(errors="ignore")).name
                     for value in arguments if value]
            if bridge_name in names:
                os.killpg(os.getpgid(int(entry.name)), signal.SIGTERM)
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            continue


class BridgeSupervisor:
    def __init__(self, command: list[str], log_path: Path):
        self.command = command
        self.log_path = log_path
        self._process: Optional[subprocess.Popen] = None
        self._log = None
        self._paused = False
        self._lock = threading.Lock()

    def _status_locked(self) -> dict:
        process = self._process
        running = process is not None and process.poll() is None
        return {
            "running": running,
            "paused": self._paused if running else False,
            "pid": process.pid if running else None,
            "exitCode": None if running or process is None else process.returncode,
            "logPath": str(self.log_path),
        }

    def status(self) -> dict:
        with self._lock:
            return self._status_locked()

    def start(self) -> dict:
        with self._lock:
            if self._process is not None and self._process.poll() is None:
                return self._status_locked()
            self.log_path.parent.mkdir(parents=True, exist_ok=True)
            self._log = self.log_path.open("a", encoding="utf-8")
            self._process = subprocess.Popen(
                self.command,
                stdout=self._log,
                stderr=subprocess.STDOUT,
                start_new_session=True,
            )
            self._paused = False
            return self._status_locked()

    def stop(self) -> dict:
        with self._lock:
            process = self._process
            if process is not None and process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=2)
            self._paused = False
            if self._log is not None:
                self._log.close()
                self._log = None
            return self._status_locked()

    def pause(self) -> dict:
        with self._lock:
            if self._process is not None and self._process.poll() is None:
                os.killpg(self._process.pid, signal.SIGSTOP)
                self._paused = True
            return self._status_locked()

    def resume(self) -> dict:
        with self._lock:
            if self._process is not None and self._process.poll() is None:
                os.killpg(self._process.pid, signal.SIGCONT)
                self._paused = False
            return self._status_locked()


def handler_for(supervisor: BridgeSupervisor):
    class Handler(BaseHTTPRequestHandler):
        def _reply(self, status: int, body: dict) -> None:
            payload = json.dumps(body).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.send_header("Access-Control-Allow-Origin", "*")
            self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
            self.send_header("Access-Control-Allow-Headers", "Content-Type")
            self.end_headers()
            self.wfile.write(payload)

        def do_OPTIONS(self) -> None:
            self._reply(204, {})

        def do_GET(self) -> None:
            if self.path.rstrip("/") in ("", "/status"):
                self._reply(200, supervisor.status())
            else:
                self._reply(404, {"error": "not found"})

        def do_POST(self) -> None:
            action = self.path.strip("/")
            try:
                if action == "start":
                    result = supervisor.start()
                elif action == "stop":
                    result = supervisor.stop()
                elif action == "pause":
                    result = supervisor.pause()
                elif action == "resume":
                    result = supervisor.resume()
                elif action == "restart":
                    supervisor.stop()
                    result = supervisor.start()
                else:
                    self._reply(404, {"error": "not found"})
                    return
                self._reply(200, result)
            except Exception as error:
                self._reply(500, {"error": f"{type(error).__name__}: {error}"})

        def log_message(self, fmt: str, *args) -> None:
            print(f"Bridge control: {self.address_string()} {fmt % args}", flush=True)

    return Handler


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--listen", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=19445)
    parser.add_argument("--plex-url", default="http://192.168.10.10:32400")
    parser.add_argument("--tv-model", default="QE77S95FATXXH")
    parser.add_argument("--hyperhdr-host", default="192.168.10.10")
    parser.add_argument("--hyperhdr-port", type=int, default=19400)
    parser.add_argument("--sync-lead", type=float, default=1.0)
    parser.add_argument("--hardware-decoder",
                        choices=("auto", "cuda", "vaapi", "off"),
                        default="auto")
    parser.add_argument("--plex-path-prefix", default="")
    parser.add_argument("--local-media-root", default="")
    parser.add_argument("--log", default="/tmp/hypertizen-source-bridge.log")
    args = parser.parse_args()

    bridge = Path(__file__).with_name("source_bridge.py")
    terminate_stale_bridges(bridge.name)
    command = [
        sys.executable, str(bridge),
        "--plex-url", args.plex_url,
        "--tv-model", args.tv_model,
        "--hyperhdr-host", args.hyperhdr_host,
        "--hyperhdr-port", str(args.hyperhdr_port),
        "--sync-lead", str(args.sync_lead),
        "--hardware-decoder", args.hardware_decoder,
        "--plex-path-prefix", args.plex_path_prefix,
        "--local-media-root", args.local_media_root,
    ]
    supervisor = BridgeSupervisor(command, Path(args.log))
    server = ThreadingHTTPServer((args.listen, args.port), handler_for(supervisor))
    print(f"Source bridge control listening on http://{args.listen}:{args.port}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        supervisor.stop()
        server.server_close()


if __name__ == "__main__":
    main()
