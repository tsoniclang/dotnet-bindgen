#!/bin/bash
# Test script to verify camelCase naming flags work correctly
# This is a PhaseGate regression test for the camelCase fix

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
OUTPUT_DIR="$PROJECT_ROOT/.tests/camelcase-test-$$"

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

echo "=== camelCase Regression Test ==="
echo "Runtime: $DOTNET_RUNTIME"
echo "Output: $OUTPUT_DIR"

# Clean up on exit
trap "rm -rf $OUTPUT_DIR" EXIT

mkdir -p "$OUTPUT_DIR"

# Generate with camelCase flags
echo ""
echo "Generating with --method-names camelCase --property-names camelCase --enum-member-names camelCase..."
dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- generate \
    -a "$DOTNET_RUNTIME/System.Collections.dll" \
    --out-dir "$OUTPUT_DIR" \
    --method-names camelCase \
    --property-names camelCase \
    --enum-member-names camelCase \
    > /dev/null 2>&1

# Check ICollection interface members are camelCase
echo ""
echo "Checking ICollection\$instance members..."
DTS_FILE="$OUTPUT_DIR/System.Collections/internal/index.d.ts"

if ! grep -Fq "readonly count: int" "$DTS_FILE"; then
    echo "FAIL: Expected 'readonly count: int' (camelCase) but not found"
    echo "Actual content:"
    grep -A 5 'ICollection\$instance' "$DTS_FILE" | head -10
    exit 1
fi

if ! grep -Fq "copyTo(array:" "$DTS_FILE"; then
    echo "FAIL: Expected 'copyTo(' (camelCase) but not found"
    exit 1
fi

if ! grep -Fq "getEnumerator():" "$DTS_FILE"; then
    echo "FAIL: Expected 'getEnumerator()' (camelCase) but not found"
    exit 1
fi

# Check IEqualityComparer interface members
echo "Checking IEqualityComparer\$instance members..."
if ! grep -Fq "equals(x: unknown, y: unknown): boolean" "$DTS_FILE"; then
    echo "FAIL: Expected 'equals(' (camelCase) but not found"
    exit 1
fi

if ! grep -Fq "getHashCode(obj: unknown): int" "$DTS_FILE"; then
    echo "FAIL: Expected 'getHashCode(' (camelCase) but not found"
    exit 1
fi

# Check class members (ArrayList)
echo "Checking ArrayList class members..."
if ! grep -Fq "binarySearch(" "$DTS_FILE"; then
    echo "FAIL: Expected 'binarySearch(' (camelCase) but not found"
    exit 1
fi

if ! grep -Fq "readonly count: int" "$DTS_FILE"; then
    echo "FAIL: Expected 'readonly count:' property (camelCase) but not found"
    exit 1
fi

# Check static members are also camelCase
echo "Checking static members..."
if ! grep -Fq "static adapter(" "$DTS_FILE"; then
    echo "FAIL: Expected 'static adapter(' (camelCase) but not found"
    exit 1
fi

# Check enum members (need to generate a namespace with enums)
echo ""
echo "Generating System.Runtime.InteropServices.ComTypes for enum test..."
dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- generate \
    -a "$DOTNET_RUNTIME/System.Runtime.InteropServices.dll" \
    --out-dir "$OUTPUT_DIR" \
    --method-names camelCase \
    --property-names camelCase \
    --enum-member-names camelCase \
    > /dev/null 2>&1

ENUM_FILE="$OUTPUT_DIR/System.Runtime.InteropServices.ComTypes/internal/index.d.ts"
if [ -f "$ENUM_FILE" ]; then
    echo "Checking enum members..."
    # CALLCONV enum: CC_CDECL should become ccCdecl (true camelCase)
    if grep -Fq "CC_CDECL" "$ENUM_FILE"; then
        echo "FAIL: Found 'CC_CDECL' (UPPERCASE) - enum members should be camelCase"
        exit 1
    fi
    # Verify proper camelCase transformation: CC_CDECL -> ccCdecl
    if ! grep -Fq "ccCdecl" "$ENUM_FILE"; then
        echo "FAIL: Expected 'ccCdecl' (true camelCase from CC_CDECL) but not found"
        echo "Searching for any cc pattern:"
        grep -i "cc" "$ENUM_FILE" | head -5 || true
        exit 1
    fi
    echo "Enum members: OK (true camelCase: CC_CDECL -> ccCdecl)"
fi

# TypeScript compilation check (offline, no network)
echo ""
echo "Checking TypeScript compilation..."
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
if command -v "$PROJECT_ROOT/node_modules/.bin/tsc" &> /dev/null; then
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

echo ""
echo "=== All camelCase regression tests PASSED ==="
