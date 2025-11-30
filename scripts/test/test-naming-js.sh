#!/bin/bash
# Test script to verify --naming js flag works correctly
# This is a regression test for JS-style member naming

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
OUTPUT_DIR="$PROJECT_ROOT/.tests/naming-js-test-$$"

# Auto-detect .NET runtime path
if [ -n "${DOTNET_RUNTIME:-}" ]; then
    # Use explicit path if set
    :
elif [ -d "/home/jeswin/dotnet/shared/Microsoft.NETCore.App" ]; then
    # Development machine
    DOTNET_RUNTIME=$(ls -d /home/jeswin/dotnet/shared/Microsoft.NETCore.App/*/ 2>/dev/null | sort -V | tail -1)
elif [ -d "/usr/share/dotnet/shared/Microsoft.NETCore.App" ]; then
    # Standard Linux install
    DOTNET_RUNTIME=$(ls -d /usr/share/dotnet/shared/Microsoft.NETCore.App/*/ 2>/dev/null | sort -V | tail -1)
elif [ -d "/usr/local/share/dotnet/shared/Microsoft.NETCore.App" ]; then
    # macOS Homebrew
    DOTNET_RUNTIME=$(ls -d /usr/local/share/dotnet/shared/Microsoft.NETCore.App/*/ 2>/dev/null | sort -V | tail -1)
else
    echo "ERROR: Could not auto-detect .NET runtime. Set DOTNET_RUNTIME environment variable."
    exit 1
fi

# Remove trailing slash if present
DOTNET_RUNTIME="${DOTNET_RUNTIME%/}"

echo "=== --naming js Regression Test ==="
echo "Runtime: $DOTNET_RUNTIME"
echo "Output: $OUTPUT_DIR"

# Clean up on exit
trap "rm -rf $OUTPUT_DIR" EXIT

mkdir -p "$OUTPUT_DIR"

# Generate with --naming js
echo ""
echo "Generating with --naming js..."
dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- generate \
    -a "$DOTNET_RUNTIME/System.Collections.dll" \
    --out-dir "$OUTPUT_DIR" \
    --naming js \
    > /dev/null 2>&1

DTS_FILE="$OUTPUT_DIR/System.Collections/internal/index.d.ts"

# Test 1: PascalCase methods become lowerFirst
echo ""
echo "Test 1: PascalCase methods → lowerFirst..."
if ! grep -Fq "getEnumerator():" "$DTS_FILE"; then
    echo "FAIL: Expected 'getEnumerator()' (lowerFirst from GetEnumerator) but not found"
    exit 1
fi
echo "  OK: GetEnumerator → getEnumerator"

# Test 2: PascalCase properties become lowerFirst
echo "Test 2: PascalCase properties → lowerFirst..."
if ! grep -Fq "readonly count: int" "$DTS_FILE"; then
    echo "FAIL: Expected 'readonly count:' (lowerFirst from Count) but not found"
    exit 1
fi
echo "  OK: Count → count"

# Test 3: Multi-word PascalCase becomes camelCase
echo "Test 3: Multi-word PascalCase → camelCase..."
if ! grep -Fq "binarySearch(" "$DTS_FILE"; then
    echo "FAIL: Expected 'binarySearch(' (lowerFirst from BinarySearch) but not found"
    exit 1
fi
echo "  OK: BinarySearch → binarySearch"

# Generate System.Runtime.InteropServices for enum test
echo ""
echo "Generating System.Runtime.InteropServices for enum/special name tests..."
dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- generate \
    -a "$DOTNET_RUNTIME/System.Runtime.InteropServices.dll" \
    --out-dir "$OUTPUT_DIR" \
    --naming js \
    > /dev/null 2>&1

ENUM_FILE="$OUTPUT_DIR/System.Runtime.InteropServices.ComTypes/internal/index.d.ts"
if [ -f "$ENUM_FILE" ]; then
    # Test 4: ALL-UPPERCASE enum members stay unchanged
    echo ""
    echo "Test 4: ALL-UPPERCASE enum members → unchanged..."
    if ! grep -Fq "CC_CDECL" "$ENUM_FILE"; then
        echo "FAIL: Expected 'CC_CDECL' to remain unchanged but not found"
        echo "Searching for any CC pattern:"
        grep -i "cc" "$ENUM_FILE" | head -5 || true
        exit 1
    fi
    echo "  OK: CC_CDECL → CC_CDECL (unchanged)"
fi

# Test 5: Check value__ stays unchanged (CLR-reserved pattern)
echo ""
echo "Test 5: CLR-reserved pattern (value__) → unchanged..."
# Look in bindings.json for value__ field
BINDINGS_FILE="$OUTPUT_DIR/System.Runtime.InteropServices.ComTypes/bindings.json"
if [ -f "$BINDINGS_FILE" ]; then
    if grep -Fq '"tsEmitName": "value__"' "$BINDINGS_FILE"; then
        echo "  OK: value__ → value__ (unchanged)"
    elif grep -Fq '"tsEmitName": "value"' "$BINDINGS_FILE"; then
        echo "FAIL: value__ was transformed to 'value' - should remain unchanged"
        exit 1
    else
        echo "  SKIP: No value__ field found in this namespace (OK)"
    fi
else
    echo "  SKIP: No bindings.json found"
fi

# TypeScript compilation check (offline, no network)
echo ""
echo "Test 6: TypeScript compilation..."
cd "$OUTPUT_DIR"
cat > tsconfig.json << 'EOF'
{
  "compilerOptions": {
    "module": "ESNext",
    "target": "ESNext",
    "declaration": true,
    "strict": true,
    "noEmit": true,
    "skipLibCheck": true,
    "moduleResolution": "bundler"
  },
  "include": ["**/*.d.ts"]
}
EOF

# Use local tsc if available, fall back to npx
if [ -x "$PROJECT_ROOT/node_modules/.bin/tsc" ]; then
    if ! "$PROJECT_ROOT/node_modules/.bin/tsc" --noEmit 2>/dev/null; then
        echo "FAIL: TypeScript compilation failed"
        exit 1
    fi
elif command -v tsc &> /dev/null; then
    if ! tsc --noEmit 2>/dev/null; then
        echo "FAIL: TypeScript compilation failed"
        exit 1
    fi
else
    # Fall back to npx only if no local tsc
    if ! npx tsc --noEmit 2>/dev/null; then
        echo "FAIL: TypeScript compilation failed"
        exit 1
    fi
fi
echo "  OK: TypeScript compiles without errors"

echo ""
echo "=== All --naming js regression tests PASSED ==="
