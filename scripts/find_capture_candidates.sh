#!/bin/bash

# Script to find candidate libraries for screen capture and pixel sampling on Tizen 9
# Searches for libraries with relevant names and exports useful symbols

TIZEN_DIR="/home/icetea/projects/fix-hypertizen/hypertizen-minimal-fix/references/tizen9_os_files"
OUTPUT_DIR="/home/icetea/projects/fix-hypertizen/hypertizen-minimal-fix/references/candidates"

mkdir -p "$OUTPUT_DIR"

echo "=================================================="
echo "Finding Tizen 9 Screen Capture Candidate Libraries"
echo "=================================================="
echo ""

# Search patterns for library names
PATTERNS=(
    "video"
    "capture"
    "screen"
    "display"
    "pixel"
    "enhance"
    "sample"
    "frame"
    "buffer"
    "graphic"
    "render"
    "compositor"
    "wayland"
    "surface"
    "hdmi"
    "scaler"
    "yuv"
    "rgb"
)

# Find all .so files and filter by patterns
echo "Step 1: Finding libraries matching search patterns..."
echo "---------------------------------------------------"

for pattern in "${PATTERNS[@]}"; do
    echo "Searching for: *${pattern}*.so*"
    find "$TIZEN_DIR" -type f -name "*${pattern}*.so*" 2>/dev/null | sort
done | sort -u > "$OUTPUT_DIR/all_matches.txt"

MATCH_COUNT=$(wc -l < "$OUTPUT_DIR/all_matches.txt")
echo ""
echo "Found $MATCH_COUNT candidate libraries"
echo ""

# Categorize libraries
echo "Step 2: Categorizing libraries..."
echo "-----------------------------------"

grep -i "video" "$OUTPUT_DIR/all_matches.txt" | sort -u > "$OUTPUT_DIR/video_libs.txt"
grep -i "capture" "$OUTPUT_DIR/all_matches.txt" | sort -u > "$OUTPUT_DIR/capture_libs.txt"
grep -i "screen\|display" "$OUTPUT_DIR/all_matches.txt" | sort -u > "$OUTPUT_DIR/screen_libs.txt"
grep -i "pixel\|enhance\|sample" "$OUTPUT_DIR/all_matches.txt" | sort -u > "$OUTPUT_DIR/pixel_libs.txt"
grep -i "graphic\|render\|compositor" "$OUTPUT_DIR/all_matches.txt" | sort -u > "$OUTPUT_DIR/graphics_libs.txt"
grep -i "buffer\|frame" "$OUTPUT_DIR/all_matches.txt" | sort -u > "$OUTPUT_DIR/buffer_libs.txt"

echo "Video libraries: $(wc -l < "$OUTPUT_DIR/video_libs.txt")"
echo "Capture libraries: $(wc -l < "$OUTPUT_DIR/capture_libs.txt")"
echo "Screen/Display libraries: $(wc -l < "$OUTPUT_DIR/screen_libs.txt")"
echo "Pixel/Enhance libraries: $(wc -l < "$OUTPUT_DIR/pixel_libs.txt")"
echo "Graphics libraries: $(wc -l < "$OUTPUT_DIR/graphics_libs.txt")"
echo "Buffer/Frame libraries: $(wc -l < "$OUTPUT_DIR/buffer_libs.txt")"
echo ""

# Create high-priority list (libraries with multiple relevant keywords)
echo "Step 3: Finding high-priority candidates..."
echo "--------------------------------------------"

> "$OUTPUT_DIR/high_priority.txt"

while read -r lib; do
    basename=$(basename "$lib")
    score=0

    # Score based on keyword matches
    [[ "$basename" =~ video ]] && ((score++))
    [[ "$basename" =~ capture ]] && ((score++))
    [[ "$basename" =~ screen ]] && ((score++))
    [[ "$basename" =~ display ]] && ((score++))
    [[ "$basename" =~ pixel ]] && ((score++))
    [[ "$basename" =~ enhance ]] && ((score++))
    [[ "$basename" =~ sample ]] && ((score++))

    # High priority if score >= 2
    if [ $score -ge 2 ]; then
        echo "$lib (score: $score)" >> "$OUTPUT_DIR/high_priority.txt"
    fi
done < "$OUTPUT_DIR/all_matches.txt"

sort -t: -k2 -nr "$OUTPUT_DIR/high_priority.txt" > "$OUTPUT_DIR/high_priority_sorted.txt"
mv "$OUTPUT_DIR/high_priority_sorted.txt" "$OUTPUT_DIR/high_priority.txt"

echo "High-priority candidates: $(wc -l < "$OUTPUT_DIR/high_priority.txt")"
echo ""

# Look for known Samsung/Tizen libraries from previous documentation
echo "Step 4: Searching for known library names..."
echo "----------------------------------------------"

KNOWN_LIBS=(
    "libvideo-capture"
    "libsec-video-capture"
    "libvideoenhance"
    "libgfx-video"
    "libscreen_connector"
    "libvideodev"
    "libdisplay"
)

> "$OUTPUT_DIR/known_libs_found.txt"

for known in "${KNOWN_LIBS[@]}"; do
    echo "Looking for: $known"
    grep -i "$known" "$OUTPUT_DIR/all_matches.txt" >> "$OUTPUT_DIR/known_libs_found.txt" 2>/dev/null || echo "  Not found"
done

echo ""
if [ -s "$OUTPUT_DIR/known_libs_found.txt" ]; then
    echo "Found $(wc -l < "$OUTPUT_DIR/known_libs_found.txt") known libraries!"
    sort -u "$OUTPUT_DIR/known_libs_found.txt" > "$OUTPUT_DIR/known_libs_found_sorted.txt"
    mv "$OUTPUT_DIR/known_libs_found_sorted.txt" "$OUTPUT_DIR/known_libs_found.txt"
else
    echo "No known libraries found"
fi
echo ""

echo "=================================================="
echo "Results saved to: $OUTPUT_DIR"
echo "=================================================="
echo ""
echo "Files created:"
echo "  - all_matches.txt (all candidates)"
echo "  - high_priority.txt (multi-keyword matches)"
echo "  - known_libs_found.txt (previously documented libs)"
echo "  - video_libs.txt, capture_libs.txt, etc. (categorized)"
echo ""
