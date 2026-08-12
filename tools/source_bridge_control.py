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
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Optional


BRIDGE_VERSION = "2026.08.12.3"


def system_diagnostics() -> dict:
    commands = {
        "identity": ["id"],
        "vaapi": ["vainfo", "--display", "drm", "--device",
                  "/dev/dri/renderD128"],
        "ffmpegHwaccels": ["ffmpeg", "-hide_banner", "-hwaccels"],
    }
    results = {}
    for name, command in commands.items():
        try:
            completed = subprocess.run(
                command, capture_output=True, text=True, timeout=10,
                check=False)
            results[name] = {
                "exitCode": completed.returncode,
                "output": (completed.stdout + completed.stderr)[-12000:],
            }
        except Exception as error:
            results[name] = {"error": f"{type(error).__name__}: {error}"}
    results["devices"] = {
        path: os.path.exists(path)
        for path in ("/dev/dri", "/dev/dri/card0", "/dev/dri/renderD128")
    }
    return {"bridgeVersion": BRIDGE_VERSION, **results}


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
    def __init__(self, command: list[str], log_path: Path, sync_lead: float,
                 settings_path: Path):
        self.command = command
        self.log_path = log_path
        self.settings_path = settings_path
        self.sync_lead = sync_lead
        self._process: Optional[subprocess.Popen] = None
        self._log = None
        self._paused = False
        self._lock = threading.Lock()
        self._load_settings()

    def _load_settings(self) -> None:
        try:
            saved = json.loads(self.settings_path.read_text(encoding="utf-8"))
            self.sync_lead = self._validate_sync_lead(saved["syncLead"])
        except (FileNotFoundError, KeyError, ValueError, TypeError,
                json.JSONDecodeError):
            pass

    @staticmethod
    def _validate_sync_lead(value) -> float:
        value = float(value)
        if not 0 <= value <= 5:
            raise ValueError("syncLead must be between 0 and 5 seconds")
        return round(value, 3)

    def _save_settings_locked(self) -> None:
        self.settings_path.parent.mkdir(parents=True, exist_ok=True)
        temporary = self.settings_path.with_suffix(".tmp")
        temporary.write_text(
            json.dumps({"syncLead": self.sync_lead}, indent=2),
            encoding="utf-8")
        temporary.replace(self.settings_path)

    def settings(self) -> dict:
        with self._lock:
            return {
                "syncLead": self.sync_lead,
                "appliesOnNextStart": self._process is not None
                and self._process.poll() is None,
            }

    def update_settings(self, sync_lead, restart: bool = False) -> dict:
        with self._lock:
            self.sync_lead = self._validate_sync_lead(sync_lead)
            self._save_settings_locked()
            was_running = (self._process is not None
                           and self._process.poll() is None)
        if restart and was_running:
            self.stop()
            self.start()
        return {**self.settings(), **self.status()}

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
            command = [*self.command, "--sync-lead", str(self.sync_lead)]
            self._process = subprocess.Popen(
                command,
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
            self._reply_bytes(status, payload, "application/json; charset=utf-8")

        def _reply_bytes(self, status: int, payload: bytes,
                         content_type: str) -> None:
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(payload)))
            self.send_header("Access-Control-Allow-Origin", "*")
            self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
            self.send_header("Access-Control-Allow-Headers", "Content-Type")
            self.end_headers()
            self.wfile.write(payload)

        def do_OPTIONS(self) -> None:
            self._reply(204, {})

        def do_GET(self) -> None:
            parsed = urllib.parse.urlparse(self.path)
            path = parsed.path.rstrip("/")
            if path in ("", "/controls.html"):
                self._serve_asset("controls.html")
            elif path == "/logs.html":
                self._serve_asset("server_logs.html")
            elif path == "/status":
                self._reply(200, {
                    **supervisor.status(), "bridgeVersion": BRIDGE_VERSION})
            elif path == "/settings":
                self._reply(200, supervisor.settings())
            elif path == "/api/logs":
                query = urllib.parse.parse_qs(parsed.query)
                tail = min(max(int(query.get("tail", ["300"])[0]), 1), 2000)
                try:
                    lines = supervisor.log_path.read_text(
                        encoding="utf-8", errors="replace").splitlines()[-tail:]
                except FileNotFoundError:
                    lines = []
                self._reply(200, {"lines": lines})
            elif path == "/diagnostics":
                self._reply(200, system_diagnostics())
            else:
                self._reply(404, {"error": "not found"})

        def _serve_asset(self, name: str) -> None:
            path = Path(__file__).with_name(name)
            try:
                payload = path.read_bytes()
            except FileNotFoundError:
                self._reply(404, {"error": f"missing asset: {name}"})
                return
            self._reply_bytes(200, payload, "text/html; charset=utf-8")

        def do_POST(self) -> None:
            action = self.path.strip("/")
            try:
                if action == "settings":
                    length = int(self.headers.get("Content-Length", "0"))
                    body = json.loads(self.rfile.read(length) or b"{}")
                    result = supervisor.update_settings(
                        body.get("syncLead"), bool(body.get("restart", False)))
                elif action == "start":
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
        "--hardware-decoder", args.hardware_decoder,
        "--plex-path-prefix", args.plex_path_prefix,
        "--local-media-root", args.local_media_root,
    ]
    log_path = Path(args.log)
    supervisor = BridgeSupervisor(
        command, log_path, args.sync_lead,
        log_path.with_name("bridge-settings.json"))
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
