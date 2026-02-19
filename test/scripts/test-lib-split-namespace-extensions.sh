#!/bin/bash
# Regression: ExtensionsEmitter must allow CLR namespaces split across multiple --lib packages.
#
# This constructs two --lib packages that both contribute to System.Transactions:
#   - @test/bcl-types: provides System.Transactions.Transaction (BCL)
#   - @test/split-ns-types: provides System.Transactions.ExtraType (fixture)
#
# Then it generates a package for a user assembly that defines an extension method
# whose signature references BOTH types. Before the fix, ExtensionsEmitter threw
# when choosing an owning package for the System.Transactions namespace module.

set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Library Mode Split-Namespace Extensions Test"
echo "================================================"
echo ""

init_runtime

EXT_TEST_DIR="$TESTS_DIR/lib-split-namespace-extensions"

echo "[1/6] Cleaning previous test runs..."
rm -rf "$EXT_TEST_DIR"
mkdir -p "$EXT_TEST_DIR"

echo "[2/6] Generating BCL types (library contract)..."
if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -d "$DOTNET_RUNTIME" \
    -o "$EXT_TEST_DIR/bcl-types" \
    > "$EXT_TEST_DIR/bcl-gen.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: BCL generation failed${NC}"
    tail -50 "$EXT_TEST_DIR/bcl-gen.txt"
    exit 1
fi
echo '{"name": "@test/bcl-types", "version": "1.0.0"}' > "$EXT_TEST_DIR/bcl-types/package.json"

echo "[3/6] Building SplitNsLib fixture (adds System.Transactions.ExtraType)..."
if ! dotnet build "$PROJECT_ROOT/test/fixtures/SplitNamespace/SplitNsLib/SplitNsLib.csproj" -c Release > /dev/null 2>&1; then
    echo -e "${RED}❌ FAILED: SplitNsLib build failed${NC}"
    exit 1
fi

splitns_dll=""
if [ -f "$PROJECT_ROOT/test/fixtures/SplitNamespace/SplitNsLib/bin/Release/net10.0/SplitNsLib.dll" ]; then
    splitns_dll="$PROJECT_ROOT/test/fixtures/SplitNamespace/SplitNsLib/bin/Release/net10.0/SplitNsLib.dll"
elif [ -f "$PROJECT_ROOT/artifacts/bin/SplitNsLib/Release/net10.0/SplitNsLib.dll" ]; then
    splitns_dll="$PROJECT_ROOT/artifacts/bin/SplitNsLib/Release/net10.0/SplitNsLib.dll"
else
    echo -e "${RED}❌ FAILED: SplitNsLib.dll not found${NC}"
    exit 1
fi

echo "      Generating SplitNsLib bindings (filtered by --lib BCL)..."
if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -a "$splitns_dll" \
    -d "$DOTNET_RUNTIME" \
    -o "$EXT_TEST_DIR/split-ns-types" \
    --lib "$EXT_TEST_DIR/bcl-types" \
    > "$EXT_TEST_DIR/split-ns-gen.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: SplitNsLib generation (--lib) failed${NC}"
    tail -100 "$EXT_TEST_DIR/split-ns-gen.txt"
    exit 1
fi
echo '{"name": "@test/split-ns-types", "version": "1.0.0"}' > "$EXT_TEST_DIR/split-ns-types/package.json"

echo "[4/6] Building SplitNsExtensions fixture (defines extension method referencing BOTH types)..."
if ! dotnet build "$PROJECT_ROOT/test/fixtures/SplitNamespace/SplitNsExtensions/SplitNsExtensions.csproj" -c Release > /dev/null 2>&1; then
    echo -e "${RED}❌ FAILED: SplitNsExtensions build failed${NC}"
    exit 1
fi

ext_dll=""
if [ -f "$PROJECT_ROOT/test/fixtures/SplitNamespace/SplitNsExtensions/bin/Release/net10.0/SplitNsExtensions.dll" ]; then
    ext_dll="$PROJECT_ROOT/test/fixtures/SplitNamespace/SplitNsExtensions/bin/Release/net10.0/SplitNsExtensions.dll"
elif [ -f "$PROJECT_ROOT/artifacts/bin/SplitNsExtensions/Release/net10.0/SplitNsExtensions.dll" ]; then
    ext_dll="$PROJECT_ROOT/artifacts/bin/SplitNsExtensions/Release/net10.0/SplitNsExtensions.dll"
else
    echo -e "${RED}❌ FAILED: SplitNsExtensions.dll not found${NC}"
    exit 1
fi

echo "[5/6] Generating SplitNsExtensions bindings with TWO --lib packages (split namespace)..."
if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -a "$ext_dll" \
    -d "$DOTNET_RUNTIME" \
    -o "$EXT_TEST_DIR/ext-types" \
    --lib "$EXT_TEST_DIR/bcl-types" \
    --lib "$EXT_TEST_DIR/split-ns-types" \
    > "$EXT_TEST_DIR/ext-gen.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: Extensions generation with split namespaces failed${NC}"
    tail -200 "$EXT_TEST_DIR/ext-gen.txt"
    exit 1
fi

echo "[6/6] Verifying extensions bucket imports from BOTH packages..."
ext_file="$EXT_TEST_DIR/ext-types/__internal/extensions/index.d.ts"
if [ ! -f "$ext_file" ]; then
    echo -e "${RED}❌ FAILED: Extensions file not found:${NC} $ext_file"
    find "$EXT_TEST_DIR/ext-types" -maxdepth 4 -type f | head -n 50 || true
    exit 1
fi

if ! grep -q "@test/bcl-types/System.Transactions/internal/index.js" "$ext_file"; then
    echo -e "${RED}❌ FAILED: Missing import from @test/bcl-types System.Transactions internal module${NC}"
    grep -n "System\\.Transactions" "$ext_file" | head -n 50 || true
    exit 1
fi

if ! grep -q "@test/split-ns-types/System.Transactions/internal/index.js" "$ext_file"; then
    echo -e "${RED}❌ FAILED: Missing import from @test/split-ns-types System.Transactions internal module${NC}"
    grep -n "System\\.Transactions" "$ext_file" | head -n 50 || true
    exit 1
fi

echo ""
echo "================================================"
echo -e "${GREEN}✓ SPLIT NAMESPACE EXTENSION BUCKET EMISSION VERIFIED${NC}"
echo "================================================"
echo ""

