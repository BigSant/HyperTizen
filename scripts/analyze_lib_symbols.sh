#!/bin/bash

# Script to analyze symbol exports from candidate libraries
# Uses nm to extract function names and categorize them

TIZEN_DIR="/home/icetea/projects/fix-hypertizen/hypertizen-minimal-fix/references/tizen9_os_files"
CANDIDATES_DIR="/home/icetea/projects/fix-hypertizen/hypertizen-minimal-fix/references/candidates"
ANALYSIS_DIR="/home/icetea/projects/fix-hypertizen/hypertizen-minimal-fix/references/analysis"

mkdir -p "$ANALYSIS_DIR"

echo "=================================================="
echo "Analyzing Symbol Exports from Candidate Libraries"
echo "=================================================="
echo ""

# Top priority libraries to analyze in detail
PRIORITY_LIBS=(
    "$TIZEN_DIR/usr/lib/libvideoenhance.so.0.1"
    "$TIZEN_DIR/usr/lib/libvideo-capture.so.0.1.0"
    "$TIZEN_DIR/usr/lib/librm-video-capture.so.0.1.0"
    "$TIZEN_DIR/usr/lib/libdisplay-capture-api.so.0.0"
    "$TIZEN_DIR/usr/lib/libep-common-screencapture.so"
    "$TIZEN_DIR/usr/lib/libcapi-video-capture.so.0.1.0"
    "$TIZEN_DIR/usr/lib/libcapi-rm-video-capture.so.0.0.1"
    "$TIZEN_DIR/usr/lib/libgfx-video-output.so.0.2.6"
    "$TIZEN_DIR/usr/lib/libscreen_connector_remote_surface.so.1.9.5"
    "$TIZEN_DIR/usr/lib/libdisplay-panel.so.0.1"
)

# Create master analysis file
> "$ANALYSIS_DIR/MASTER_ANALYSIS.md"

cat >> "$ANALYSIS_DIR/MASTER_ANALYSIS.md" << 'EOF'
# Tizen 9 Screen Capture Library Analysis

This file contains detailed analysis of candidate libraries for screen capture and pixel sampling.

## Analysis Date
EOF

date >> "$ANALYSIS_DIR/MASTER_ANALYSIS.md"

cat >> "$ANALYSIS_DIR/MASTER_ANALYSIS.md" << 'EOF'

## Libraries Analyzed

EOF

# Analyze each priority library
for lib in "${PRIORITY_LIBS[@]}"; do
    if [ ! -f "$lib" ]; then
        echo "SKIPPED: $lib (not found)"
        continue
    fi

    libname=$(basename "$lib")
    echo "Analyzing: $libname"

    # Create individual analysis file
    analysis_file="$ANALYSIS_DIR/${libname}.analysis.txt"

    echo "==================================================================" > "$analysis_file"
    echo "Library: $libname" >> "$analysis_file"
    echo "Path: $lib" >> "$analysis_file"
    echo "==================================================================" >> "$analysis_file"
    echo "" >> "$analysis_file"

    # Get library info
    echo "--- File Info ---" >> "$analysis_file"
    file "$lib" >> "$analysis_file" 2>&1
    echo "" >> "$analysis_file"

    echo "--- Size ---" >> "$analysis_file"
    ls -lh "$lib" | awk '{print $5}' >> "$analysis_file"
    echo "" >> "$analysis_file"

    # Extract all symbols
    echo "--- All Exported Symbols (nm -D) ---" >> "$analysis_file"
    nm -D "$lib" 2>&1 >> "$analysis_file"
    echo "" >> "$analysis_file"

    # Extract dynamic symbols
    echo "--- Dynamic Symbols (readelf --dyn-syms) ---" >> "$analysis_file"
    readelf --dyn-syms "$lib" 2>&1 | head -200 >> "$analysis_file"
    echo "" >> "$analysis_file"

    # Search for specific keywords in symbols
    echo "--- Relevant Function Names ---" >> "$analysis_file"

    echo "CAPTURE functions:" >> "$analysis_file"
    nm -D "$lib" 2>/dev/null | grep -i "capture" | grep " T " >> "$analysis_file" || echo "  None found" >> "$analysis_file"

    echo "" >> "$analysis_file"
    echo "SCREEN functions:" >> "$analysis_file"
    nm -D "$lib" 2>/dev/null | grep -i "screen" | grep " T " >> "$analysis_file" || echo "  None found" >> "$analysis_file"

    echo "" >> "$analysis_file"
    echo "PIXEL/SAMPLE functions:" >> "$analysis_file"
    nm -D "$lib" 2>/dev/null | grep -iE "pixel|sample" | grep " T " >> "$analysis_file" || echo "  None found" >> "$analysis_file"

    echo "" >> "$analysis_file"
    echo "VIDEO functions:" >> "$analysis_file"
    nm -D "$lib" 2>/dev/null | grep -i "video" | grep " T " >> "$analysis_file" || echo "  None found" >> "$analysis_file"

    echo "" >> "$analysis_file"
    echo "GET/INSTANCE functions:" >> "$analysis_file"
    nm -D "$lib" 2>/dev/null | grep -iE "get|instance" | grep " T " >> "$analysis_file" || echo "  None found" >> "$analysis_file"

    echo "" >> "$analysis_file"
    echo "LOCK/UNLOCK functions:" >> "$analysis_file"
    nm -D "$lib" 2>/dev/null | grep -iE "lock|unlock" | grep " T " >> "$analysis_file" || echo "  None found" >> "$analysis_file"

    echo "" >> "$analysis_file"
    echo "YUV/RGB/FRAME/BUFFER functions:" >> "$analysis_file"
    nm -D "$lib" 2>/dev/null | grep -iE "yuv|rgb|frame|buffer" | grep " T " >> "$analysis_file" || echo "  None found" >> "$analysis_file"

    # Add summary to master file
    cat >> "$ANALYSIS_DIR/MASTER_ANALYSIS.md" << LIBEOF

### $libname

**Path:** \`$lib\`

**Key Functions Found:**

\`\`\`
LIBEOF

    nm -D "$lib" 2>/dev/null | grep -iE "capture|screen|pixel|sample|video|yuv|rgb|frame|get.*video|lock|unlock|enhance" | grep " T " | head -30 >> "$ANALYSIS_DIR/MASTER_ANALYSIS.md" || echo "No relevant functions found" >> "$ANALYSIS_DIR/MASTER_ANALYSIS.md"

    cat >> "$ANALYSIS_DIR/MASTER_ANALYSIS.md" << 'LIBEOF'
```

---
LIBEOF

    echo "  ✓ Created $analysis_file"
done

echo ""
echo "=================================================="
echo "Analysis Complete!"
echo "=================================================="
echo ""
echo "Results:"
echo "  - Master summary: $ANALYSIS_DIR/MASTER_ANALYSIS.md"
echo "  - Individual analysis files: $ANALYSIS_DIR/*.analysis.txt"
echo ""
