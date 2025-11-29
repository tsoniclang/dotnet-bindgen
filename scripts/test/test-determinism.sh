#!/bin/bash
# Determinism test - ensures identical outputs from identical inputs
# This guarantees that the generation pipeline is fully deterministic

set -euo pipefail

echo "================================================"
echo "Determinism Test"
echo "================================================"
echo ""

# Clean previous runs
echo "[1/3] Cleaning previous test runs..."
rm -rf .tests/determinism/run1 .tests/determinism/run2
mkdir -p .tests/determinism

# Run 1
echo "[2/3] Running generation (run 1)..."
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
    generate -d ~/dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.1.25451.107 \
    -o .tests/determinism/run1 --strict > /dev/null 2>&1

# Run 2
echo "          Running generation (run 2)..."
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
    generate -d ~/dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.1.25451.107 \
    -o .tests/determinism/run2 --strict > /dev/null 2>&1

# Diff
echo "[3/3] Comparing outputs..."
if diff -r .tests/determinism/run1 .tests/determinism/run2 > /dev/null 2>&1; then
    echo "✓ Outputs are identical (byte-for-byte)"
    echo ""
    echo "================================================"
    echo "✓ DETERMINISM VERIFIED"
    echo "================================================"
    echo ""
    echo "Summary:"
    echo "  - Two independent runs produced identical output"
    echo "  - No nondeterministic ordering, hashing, or traversal"
    echo "  - Safe for downstream consumption"
    echo ""
    exit 0
else
    echo "❌ FAILED: Outputs differ between runs"
    echo ""
    echo "This indicates nondeterministic behavior in:"
    echo "  - Dictionary/HashSet iteration order"
    echo "  - Reflection member ordering"
    echo "  - File system traversal"
    echo "  - Timestamp/GUID generation"
    echo ""
    echo "Run 'diff -r .tests/determinism/run1 .tests/determinism/run2' to see differences"
    echo ""
    exit 1
fi
