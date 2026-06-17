#!/bin/bash
# Determinism test - ensures identical outputs from identical inputs
# This guarantees that the generation pipeline is fully deterministic
#
# NOTE: This test intentionally generates twice for comparison.
# It cannot reuse the BCL cache because it needs fresh runs.

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Determinism Test"
echo "================================================"
echo ""

# Initialize runtime
init_runtime

# Test output directories
RUN1_DIR="$TESTS_DIR/determinism/run1"
RUN2_DIR="$TESTS_DIR/determinism/run2"

# Clean previous runs
echo "[1/3] Cleaning previous test runs..."
rm -rf "$RUN1_DIR" "$RUN2_DIR"
mkdir -p "$TESTS_DIR/determinism"

# Run 1
echo "[2/3] Running generation (run 1)..."
if ! dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -- \
    generate -d "$DOTNET_RUNTIME" \
    -o "$RUN1_DIR" > /dev/null 2>&1; then
    echo -e "${RED}FAILED: Generation run 1 failed${NC}"
    exit 1
fi

# Run 2
echo "          Running generation (run 2)..."
if ! dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -- \
    generate -d "$DOTNET_RUNTIME" \
    -o "$RUN2_DIR" > /dev/null 2>&1; then
    echo -e "${RED}FAILED: Generation run 2 failed${NC}"
    exit 1
fi

# Diff
echo "[3/3] Comparing outputs..."
if diff -r "$RUN1_DIR" "$RUN2_DIR" > /dev/null 2>&1; then
    echo -e "${GREEN}✓ Outputs are identical (byte-for-byte)${NC}"
    echo ""
    echo "================================================"
    echo -e "${GREEN}✓ DETERMINISM VERIFIED${NC}"
    echo "================================================"
    echo ""
    echo "Summary:"
    echo "  - Two independent runs produced identical output"
    echo "  - No nondeterministic ordering, hashing, or traversal"
    echo "  - Safe for downstream consumption"
    echo ""
    exit 0
else
    echo -e "${RED}❌ FAILED: Outputs differ between runs${NC}"
    echo ""
    echo "This indicates nondeterministic behavior in:"
    echo "  - Dictionary/HashSet iteration order"
    echo "  - Reflection member ordering"
    echo "  - File system traversal"
    echo "  - Timestamp/GUID generation"
    echo ""
    echo "Run 'diff -r $RUN1_DIR $RUN2_DIR' to see differences"
    echo ""
    exit 1
fi
