#!/usr/bin/env python3
"""Decrypt Samsung MSD sections through the TV's SWU TrustZone service.

The firmware passphrase and derived AES key never leave the television.  The
local client sends one encrypted section at a time to the temporary Probe app,
removes PKCS#7 padding, and validates the plaintext against the MSD CRC.
"""

from __future__ import annotations

import argparse
import http.client
import json
import pathlib
import sys
import time
import zlib


def post(host: str, port: int, path: str, body: bytes, timeout: int) -> bytes:
    connection = http.client.HTTPConnection(host, port, timeout=timeout)
    try:
        connection.request("POST", path, body, {"Content-Type": "application/octet-stream"})
        response = connection.getresponse()
        data = response.read()
        if response.status != 200:
            raise RuntimeError(f"TV returned HTTP {response.status}: {data[:200]!r}")
        if data.startswith(b"ERROR"):
            raise RuntimeError(data.decode("ascii", "replace"))
        return data
    finally:
        connection.close()


def strip_pkcs7(data: bytes) -> bytes:
    if not data:
        return data
    padding = data[-1]
    if 1 <= padding <= 16 and data[-padding:] == bytes([padding]) * padding:
        return data[:-padding]
    return data


def decrypt_section(
    source: pathlib.Path,
    destination: pathlib.Path,
    section: dict,
    host: str,
    port: int,
    chunk_size: int,
    timeout: int,
) -> None:
    name = section["name"]
    offset = int(section["offset"], 0) if isinstance(section["offset"], str) else section["offset"]
    size = int(section["size"])
    expected_crc = int(section["crc32"])
    salt = bytes.fromhex(section["salt"])
    if len(salt) != 8:
        raise ValueError(f"{name}: expected an 8-byte salt")

    answer = post(
        host,
        port,
        "/stream-begin?derivation=1&keysize=2&mode=1",
        salt,
        timeout,
    )
    if answer != b"OK":
        raise RuntimeError(f"{name}: unexpected begin response {answer!r}")

    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".partial")
    crc = 0
    written = 0
    started = time.monotonic()
    pending = b""
    try:
        with source.open("rb") as encrypted, temporary.open("wb") as plaintext:
            encrypted.seek(offset)
            remaining = size
            while remaining:
                chunk = encrypted.read(min(chunk_size, remaining))
                if not chunk:
                    raise EOFError(f"{name}: MSD ended with {remaining} encrypted bytes remaining")
                remaining -= len(chunk)
                decoded = post(host, port, "/stream-update", chunk, timeout)

                # Keep the final AES block until PKCS#7 can be checked.
                combined = pending + decoded
                if remaining:
                    emit, pending = combined[:-16], combined[-16:]
                    plaintext.write(emit)
                    crc = zlib.crc32(emit, crc)
                    written += len(emit)
                else:
                    pending = combined

                completed = size - remaining
                if completed == size or completed % (64 * 1024 * 1024) < len(chunk):
                    elapsed = max(time.monotonic() - started, 0.001)
                    print(
                        f"  {name}: {completed / size:6.1%} "
                        f"({completed / 1024 / 1024:.1f} MiB, "
                        f"{completed / 1024 / 1024 / elapsed:.1f} MiB/s)",
                        flush=True,
                    )

            pending += post(host, port, "/stream-finish", b"", timeout)
            tail = strip_pkcs7(pending)
            plaintext.write(tail)
            crc = zlib.crc32(tail, crc)
            written += len(tail)
    except Exception:
        try:
            post(host, port, "/stream-abort", b"", timeout)
        except Exception:
            pass
        raise

    crc &= 0xFFFFFFFF
    if crc != expected_crc:
        raise RuntimeError(
            f"{name}: CRC mismatch: got {crc} (0x{crc:08x}), "
            f"expected {expected_crc} (0x{expected_crc:08x})"
        )
    temporary.replace(destination)
    print(f"  {name}: OK, {written} plaintext bytes, CRC32 0x{crc:08x}", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("msd", type=pathlib.Path)
    parser.add_argument("manifest", type=pathlib.Path)
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("--host", default="192.168.10.100")
    parser.add_argument("--port", type=int, default=45679)
    parser.add_argument("--chunk-size", type=int, default=65536)
    parser.add_argument("--timeout", type=int, default=30)
    parser.add_argument("--section", action="append", help="decrypt only the named section")
    args = parser.parse_args()
    if not 16 <= args.chunk_size <= 65536 or args.chunk_size % 16:
        parser.error("--chunk-size must be a multiple of 16 between 16 and 65536")

    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    sections = manifest["sections"]
    if args.section:
        requested = set(args.section)
        sections = [section for section in sections if section["name"] in requested]
        missing = requested - {section["name"] for section in sections}
        if missing:
            parser.error("unknown section(s): " + ", ".join(sorted(missing)))

    for section in sections:
        print(f"Decrypting {section['name']}...", flush=True)
        decrypt_section(
            args.msd,
            args.output / section["name"],
            section,
            args.host,
            args.port,
            args.chunk_size,
            args.timeout,
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
