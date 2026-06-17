#!/bin/bash
# Regression test: dotnet-bindgen emits CLR-faithful names and does not support
# JS-style naming transforms.

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "=== Naming Invariants Test ==="

# Initialize runtime
init_runtime
echo "Runtime: $DOTNET_RUNTIME"

OUTPUT_DIR=$(ensure_bcl default)
DTS_FILE="$OUTPUT_DIR/System.Collections/internal/index.d.ts"
BINDINGS_FILE="$OUTPUT_DIR/System.Collections/bindings.json"

echo ""
echo "Test 1: Preserve CLR method casing..."
if ! grep -Fq "GetEnumerator():" "$DTS_FILE"; then
    echo -e "${RED}FAIL: Expected 'GetEnumerator()' but not found${NC}"
    exit 1
fi
if grep -Fq "getEnumerator():" "$DTS_FILE"; then
    echo -e "${RED}FAIL: Found JS-style 'getEnumerator()' - naming transforms must not occur${NC}"
    exit 1
fi
echo "  OK: GetEnumerator preserved"

echo ""
echo "Test 2: Preserve CLR property casing..."
if ! grep -Fq "readonly Count: int" "$DTS_FILE"; then
    echo -e "${RED}FAIL: Expected 'readonly Count: int' but not found${NC}"
    exit 1
fi
if grep -Fq "readonly count: int" "$DTS_FILE"; then
    echo -e "${RED}FAIL: Found JS-style 'readonly count' - naming transforms must not occur${NC}"
    exit 1
fi
echo "  OK: Count preserved"

echo ""
echo "Test 3: bindings.json must not include tsEmitName or internal metadata.json..."
if grep -Fq "tsEmitName" "$BINDINGS_FILE"; then
    echo -e "${RED}FAIL: bindings.json contains tsEmitName - manifest must be TS-name-free${NC}"
    exit 1
fi
if [ -f "$OUTPUT_DIR/System.Collections/internal/metadata.json" ]; then
    echo -e "${RED}FAIL: internal/metadata.json exists - unified manifest must be only <Namespace>/bindings.json${NC}"
    exit 1
fi
echo "  OK: unified bindings.json only"

echo ""
echo "Test 4: --naming flag is rejected..."
if dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -- generate \
    -a "$DOTNET_RUNTIME/System.Collections.dll" \
    --out-dir "$TESTS_DIR/_tmp-naming-flag" \
    --naming js \
    > /dev/null 2>&1; then
    echo -e "${RED}FAIL: --naming was accepted but must be removed${NC}"
    exit 1
fi
rm -rf "$TESTS_DIR/_tmp-naming-flag"
echo "  OK: --naming rejected"

echo ""
echo "Test 5: TypeScript compilation..."
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
echo -e "${GREEN}=== All naming invariant tests PASSED ===${NC}"
