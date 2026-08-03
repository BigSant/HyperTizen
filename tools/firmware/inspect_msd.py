#!/usr/bin/env python3
"""Inspect Samsung MSDU11 firmware containers without modifying them.

The clear-text MSDU11 header exposes section offsets and sizes.  The OUIT
table and every section can be encrypted.  If a unixtract keys.ukf file is
provided, this tool also tests known MSD11 AES keys against the encrypted OUIT
header using the IV derivations seen in Samsung Tizen firmware.
"""

from __future__ import annotations

import argparse
import hashlib
import re
import struct
from dataclasses import dataclass
from pathlib import Path

try:
    from Crypto.Cipher import AES
except ImportError as exc:  # pragma: no cover - depends on host tooling
    raise SystemExit("PyCryptodome is required: python3 -m pip install pycryptodome") from exc


OUIT_MAGICS = (
    b"Tizen Software Upgrade Tree Binary Format ver. 1.8",
    b"Tizen Software Upgrade Tree Binary Format ver. 1.9",
)


@dataclass(frozen=True)
class Section:
    item_id: int
    offset: int
    size: int


@dataclass(frozen=True)
class EncryptedHeader:
    name: str
    offset: int
    size: int


def read_exact(stream, size: int) -> bytes:
    data = stream.read(size)
    if len(data) != size:
        raise ValueError(f"Unexpected end of file: wanted {size}, got {len(data)}")
    return data


def parse_msdu11(path: Path) -> tuple[int, int, list[Section], list[EncryptedHeader]]:
    with path.open("rb") as stream:
        if read_exact(stream, 6) != b"MSDU11":
            raise ValueError("Not an MSDU11 container")

        checksum, header_size, section_count = struct.unpack("<IQI", read_exact(stream, 16))
        sections = [Section(*struct.unpack("<IQQ", read_exact(stream, 20))) for _ in range(section_count)]

        (encrypted_header_count,) = struct.unpack("<I", read_exact(stream, 4))
        encrypted_headers: list[EncryptedHeader] = []
        for _ in range(encrypted_header_count):
            offset, size, name_length = struct.unpack("<QIB", read_exact(stream, 13))
            name = read_exact(stream, name_length).decode("ascii")
            encrypted_headers.append(EncryptedHeader(name, offset, size))

    return checksum, header_size, sections, encrypted_headers


def load_msd11_keys(path: Path) -> dict[str, bytes]:
    text = path.read_text(encoding="utf-8")
    collection = re.search(r'collection\s+"MSD11"\s*:\s*\{(.*?)\n\}', text, re.DOTALL)
    if not collection:
        raise ValueError(f"MSD11 collection not found in {path}")

    keys: dict[str, bytes] = {}
    for name, hex_key in re.findall(r'"([^"]+)"\s*:\s*\{x"([0-9a-fA-F]+)"\}', collection.group(1)):
        keys[name] = bytes.fromhex(hex_key)
    return keys


def unpad_pkcs7(data: bytes) -> bytes | None:
    if not data:
        return None
    count = data[-1]
    if count < 1 or count > AES.block_size or data[-count:] != bytes([count]) * count:
        return None
    return data[:-count]


def test_keys(path: Path, header: EncryptedHeader, keys: dict[str, bytes]) -> list[tuple[str, str]]:
    with path.open("rb") as stream:
        # Samsung header entries include an 8-byte descriptor before Salted__.
        stream.seek(header.offset + 8)
        encrypted = read_exact(stream, header.size - 8)

    if not encrypted.startswith(b"Salted__"):
        raise ValueError(f"{header.name}: encrypted header has no Salted__ marker")

    salt = encrypted[8:16]
    ciphertext = encrypted[16:]
    matches: list[tuple[str, str]] = []

    for name, key in keys.items():
        digests = ("md5",) if len(key) == 16 else ("sha256", "sha512")
        for digest_name in digests:
            iv = hashlib.new(digest_name, salt).digest()[: AES.block_size]
            plaintext = unpad_pkcs7(AES.new(key, AES.MODE_CBC, iv).decrypt(ciphertext))
            if plaintext and any(magic in plaintext for magic in OUIT_MAGICS):
                matches.append((name, digest_name))
    return matches


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("msd", type=Path, help="Path to upgrade.msd")
    parser.add_argument("--keys", type=Path, help="Optional unixtract keys.ukf")
    args = parser.parse_args()

    checksum, header_size, sections, headers = parse_msdu11(args.msd)
    print(f"file: {args.msd}")
    print(f"header CRC32: 0x{checksum:08x}")
    print(f"clear header size: {header_size}")
    print(f"sections: {len(sections)}")
    for index, section in enumerate(sections):
        print(
            f"  [{index}] id={section.item_id} offset=0x{section.offset:x} "
            f"size={section.size}"
        )

    for header in headers:
        print(
            f"encrypted OUIT: {header.name} offset=0x{header.offset:x} "
            f"size={header.size}"
        )

    if args.keys:
        keys = load_msd11_keys(args.keys)
        print(f"testing {len(keys)} public MSD11 keys")
        all_matches = [
            (header.name, key_name, digest)
            for header in headers
            for key_name, digest in test_keys(args.msd, header, keys)
        ]
        if all_matches:
            for header_name, key_name, digest in all_matches:
                print(f"MATCH: header={header_name} key={key_name} IV={digest}")
            return 0
        print("no public key matched")
        return 2

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
