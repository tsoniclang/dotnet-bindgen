#!/bin/bash
# Test script to verify --naming js flag works correctly
# This is a regression test for JS-style member naming

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "=== --naming js Regression Test ==="

# Initialize runtime
init_runtime
echo "Runtime: $DOTNET_RUNTIME"

# Use naming-js cache directory
OUTPUT_DIR="$BCL_NAMING_JS_DIR"

# Generate with --naming js (reuses cache if available)
echo ""
echo "Generating with --naming js..."

# Force regeneration for this test to ensure we're testing current code
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- generate \
    -a "$DOTNET_RUNTIME/System.Collections.dll" \
    --out-dir "$OUTPUT_DIR" \
    --naming js \
    > /dev/null 2>&1; then
    echo -e "${RED}FAILED: Generation failed${NC}"
    exit 1
fi

DTS_FILE="$OUTPUT_DIR/System.Collections/internal/index.d.ts"

# Test 1: PascalCase methods become lowerFirst
echo ""
echo "Test 1: PascalCase methods → lowerFirst..."
if ! grep -Fq "getEnumerator():" "$DTS_FILE"; then
    echo -e "${RED}FAIL: Expected 'getEnumerator()' (lowerFirst from GetEnumerator) but not found${NC}"
    exit 1
fi
echo "  OK: GetEnumerator → getEnumerator"

# Test 2: PascalCase properties become lowerFirst
echo "Test 2: PascalCase properties → lowerFirst..."
if ! grep -Fq "readonly count: int" "$DTS_FILE"; then
    echo -e "${RED}FAIL: Expected 'readonly count:' (lowerFirst from Count) but not found${NC}"
    exit 1
fi
echo "  OK: Count → count"

# Test 3: Multi-word PascalCase becomes camelCase
echo "Test 3: Multi-word PascalCase → camelCase..."
if ! grep -Fq "binarySearch(" "$DTS_FILE"; then
    echo -e "${RED}FAIL: Expected 'binarySearch(' (lowerFirst from BinarySearch) but not found${NC}"
    exit 1
fi
echo "  OK: BinarySearch → binarySearch"

# Generate System.Runtime.InteropServices for enum test
echo ""
echo "Generating System.Runtime.InteropServices for enum/special name tests..."
if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- generate \
    -a "$DOTNET_RUNTIME/System.Runtime.InteropServices.dll" \
    --out-dir "$OUTPUT_DIR" \
    --naming js \
    > /dev/null 2>&1; then
    echo -e "${RED}FAILED: Generation failed${NC}"
    exit 1
fi

ENUM_FILE="$OUTPUT_DIR/System.Runtime.InteropServices.ComTypes/internal/index.d.ts"
if [ -f "$ENUM_FILE" ]; then
    # Test 4: ALL-UPPERCASE enum members stay unchanged
    echo ""
    echo "Test 4: ALL-UPPERCASE enum members → unchanged..."
    if ! grep -Fq "CC_CDECL" "$ENUM_FILE"; then
        echo -e "${RED}FAIL: Expected 'CC_CDECL' to remain unchanged but not found${NC}"
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
        echo -e "${RED}FAIL: value__ was transformed to 'value' - should remain unchanged${NC}"
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

if ! run_tsc --noEmit 2>/dev/null; then
    echo -e "${RED}FAIL: TypeScript compilation failed${NC}"
    exit 1
fi
echo "  OK: TypeScript compiles without errors"

echo ""
echo -e "${GREEN}=== All --naming js regression tests PASSED ===${NC}"
