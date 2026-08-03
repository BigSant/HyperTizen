#!/usr/bin/env python3
"""Inventory firmware components that may expose frames or Hyperion color data.

The scanner is intentionally broad: it searches executable string tables and
configuration/service metadata, then emits machine-readable evidence for
manual ABI and permission analysis.  It never modifies the firmware tree.
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import subprocess
from collections import Counter


GROUPS = {
    "capture": (
        "capture", "screenshot", "screen shot", "screenshooter",
        "screenmirror", "screen mirror", "frame grab", "framegrab",
    ),
    "display-frame": (
        "framebuffer", "frame buffer", "display plane", "display source",
        "scanout", "writeback", "last captured", "post yuv", "video yuv",
    ),
    "encoded-frame": (
        "displayencodesrc", "screen encode", "video encoder", "h264 encoder",
        "encoder input", "encoded packet", "force keyframe",
    ),
    "preview-thumbnail": (
        "thumbnail", "preview frame", "video preview", "live preview",
        "snapshot", "poster frame", "trickplay", "trick play",
    ),
    "color-analysis": (
        "histogram", "dominant color", "average color", "mean color",
        "color analysis", "screen analysis", "image analysis", "luminance",
        "chrominance", "apl", "average picture level", "scene detection",
    ),
    "video-pipeline": (
        "videosink", "video sink", "videosrc", "video source", "appsrc",
        "appsink", "gst_buffer", "tbm_surface", "dmabuf", "dma-buf",
        "waylandsink", "glimagesink", "decodebin", "videoconvert",
    ),
    "graphics": (
        "eglimage", "egl image", "glreadpixels", "readpixels", "vulkan",
        "render target", "texture export", "surface dump", "gpu dump",
    ),
    "hardware-io": (
        "/dev/video", "/dev/dri", "v4l2", "drm_ioctl", "gem_create",
        "hw_capture", "scaler", "m2m", "writeback connector",
    ),
    "external-input": (
        "hdmi capture", "hdmi input", "external input", "tv source",
        "live source", "tuner source", "composite input", "pip source",
    ),
    "stream-sharing": (
        "remote guide", "remoteguide", "live-ss", "screen share",
        "screen sharing", "miracast", "smart view", "multiview", "multi view",
        "second screen", "dlna", "webrtc", "rtsp", "rtp pay",
    ),
    "ambient-light": (
        "ambient light", "ambientlight", "bias light", "backlight color",
        "led color", "hue sync", "light sync", "lighting sync",
    ),
}

TEXT_SUFFIXES = {
    ".conf", ".ini", ".json", ".manifest", ".pc", ".service", ".socket",
    ".target", ".timer", ".xml", ".yaml", ".yml", ".rules", ".list",
    ".policy", ".smack", ".desktop", ".sh", ".py", ".js", ".html",
}


def strings_for(path: pathlib.Path) -> str:
    result = subprocess.run(
        ["strings", "-a", "-n", "5", os.fspath(path)],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        errors="replace",
        check=False,
    )
    return result.stdout


def read_candidate_text(path: pathlib.Path) -> tuple[str, bool]:
    try:
        with path.open("rb") as source:
            prefix = source.read(4)
        is_elf = prefix == b"\x7fELF"
        if is_elf:
            return strings_for(path), True
        if path.suffix.lower() in TEXT_SUFFIXES and path.stat().st_size <= 8 * 1024 * 1024:
            return path.read_text(encoding="utf-8", errors="replace"), False
    except (OSError, ValueError):
        pass
    return "", False


def evidence(text: str, terms: tuple[str, ...], limit: int = 12) -> list[str]:
    pattern = re.compile("|".join(re.escape(term) for term in terms), re.IGNORECASE)
    found: list[str] = []
    for line in text.splitlines():
        if pattern.search(line):
            compact = " ".join(line.strip().split())
            if compact and compact not in found:
                found.append(compact[:300])
                if len(found) >= limit:
                    break
    return found


def scan_root(root: pathlib.Path, label: str) -> tuple[list[dict], dict]:
    records: list[dict] = []
    stats = Counter()
    for path in root.rglob("*"):
        if not path.is_file() or path.is_symlink():
            continue
        stats["regular_files"] += 1
        text, is_elf = read_candidate_text(path)
        if is_elf:
            stats["elf_files"] += 1
        elif text:
            stats["metadata_files"] += 1
        if not text:
            continue
        matches = {}
        lower = text.lower()
        for group, terms in GROUPS.items():
            if any(term in lower for term in terms):
                matches[group] = evidence(text, terms)
        if matches:
            stats["matched_files"] += 1
            records.append({
                "tree": label,
                "path": "/" + path.relative_to(root).as_posix(),
                "kind": "elf" if is_elf else "metadata",
                "size": path.stat().st_size,
                "groups": matches,
            })
    return records, dict(stats)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument(
        "root", nargs="+", metavar="LABEL=PATH",
        help="firmware root with a stable label, for example rsm=/path/root",
    )
    args = parser.parse_args()

    all_records: list[dict] = []
    all_stats = {}
    for spec in args.root:
        if "=" not in spec:
            parser.error(f"root must be LABEL=PATH: {spec}")
        label, raw_path = spec.split("=", 1)
        root = pathlib.Path(raw_path)
        if not root.is_dir():
            parser.error(f"not a directory: {root}")
        records, stats = scan_root(root, label)
        all_records.extend(records)
        all_stats[label] = stats

    group_counts = Counter(
        group for record in all_records for group in record["groups"]
    )
    result = {
        "schema": 1,
        "groups": {key: list(value) for key, value in GROUPS.items()},
        "stats": all_stats,
        "group_counts": dict(sorted(group_counts.items())),
        "records": sorted(all_records, key=lambda item: (item["tree"], item["path"])),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2, sort_keys=False), encoding="utf-8")
    print(json.dumps({"stats": all_stats, "group_counts": result["group_counts"]}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
