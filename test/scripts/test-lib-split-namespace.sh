#!/bin/bash
# Regression: --lib should allow CLR namespaces split across multiple packages
# (e.g., System.Transactions is provided by both @tsonic/dotnet and EFCore types).
#
# This test constructs two --lib packages that both contribute to System.Transactions, then
# generates a module-container-style user library that references types from both packages.
#
# Before fix: dotnet-bindgen would throw:
#   Namespace 'System.Transactions' is split across multiple packages ...
# because facade emission required a unique owning package for the namespace module.

set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Library Mode Split-Namespace Test"
echo "================================================"
echo ""

init_runtime

SPLIT_TEST_DIR="$TESTS_DIR/lib-split-namespace"

echo "[1/5] Cleaning previous test runs..."
rm -rf "$SPLIT_TEST_DIR"
mkdir -p "$SPLIT_TEST_DIR"

echo "[2/5] Generating BCL types (library contract)..."
if ! dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -- \
    generate -d "$DOTNET_RUNTIME" \
    -o "$SPLIT_TEST_DIR/bcl-types" \
    > "$SPLIT_TEST_DIR/bcl-gen.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: BCL generation failed${NC}"
    tail -50 "$SPLIT_TEST_DIR/bcl-gen.txt"
    exit 1
fi
echo '{"name": "@test/bcl-types", "version": "1.0.0"}' > "$SPLIT_TEST_DIR/bcl-types/package.json"

echo "[3/5] Building SplitNsLib fixture (adds System.Transactions.ExtraType)..."
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
if ! dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -- \
    generate -a "$splitns_dll" \
    -d "$DOTNET_RUNTIME" \
    -o "$SPLIT_TEST_DIR/split-ns-types" \
    --lib "$SPLIT_TEST_DIR/bcl-types" \
    > "$SPLIT_TEST_DIR/split-ns-gen.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: SplitNsLib generation (--lib) failed${NC}"
    tail -100 "$SPLIT_TEST_DIR/split-ns-gen.txt"
    exit 1
fi
echo '{"name": "@test/split-ns-types", "version": "1.0.0"}' > "$SPLIT_TEST_DIR/split-ns-types/package.json"

echo "[4/5] Building SplitNsConsumer fixture (module container uses both types)..."
if ! dotnet build "$PROJECT_ROOT/test/fixtures/SplitNamespace/SplitNsConsumer/SplitNsConsumer.csproj" -c Release > /dev/null 2>&1; then
    echo -e "${RED}❌ FAILED: SplitNsConsumer build failed${NC}"
    exit 1
fi

consumer_dll=""
if [ -f "$PROJECT_ROOT/test/fixtures/SplitNamespace/SplitNsConsumer/bin/Release/net10.0/SplitNsConsumer.dll" ]; then
    consumer_dll="$PROJECT_ROOT/test/fixtures/SplitNamespace/SplitNsConsumer/bin/Release/net10.0/SplitNsConsumer.dll"
elif [ -f "$PROJECT_ROOT/artifacts/bin/SplitNsConsumer/Release/net10.0/SplitNsConsumer.dll" ]; then
    consumer_dll="$PROJECT_ROOT/artifacts/bin/SplitNsConsumer/Release/net10.0/SplitNsConsumer.dll"
else
    echo -e "${RED}❌ FAILED: SplitNsConsumer.dll not found${NC}"
    exit 1
fi

echo "[5/5] Generating consumer bindings with TWO --lib packages (split namespace)..."
if ! dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -- \
    generate -a "$consumer_dll" \
    -d "$DOTNET_RUNTIME" \
    -o "$SPLIT_TEST_DIR/consumer-types" \
    --lib "$SPLIT_TEST_DIR/bcl-types" \
    --lib "$SPLIT_TEST_DIR/split-ns-types" \
    > "$SPLIT_TEST_DIR/consumer-gen.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: Consumer generation with split namespaces failed${NC}"
    tail -200 "$SPLIT_TEST_DIR/consumer-gen.txt"
    exit 1
fi

consumer_facade="$SPLIT_TEST_DIR/consumer-types/MyCompany.SplitNsConsumer.d.ts"
if [ ! -f "$consumer_facade" ]; then
    echo -e "${RED}❌ FAILED: Consumer facade file not found:${NC} $consumer_facade"
    find "$SPLIT_TEST_DIR/consumer-types" -maxdepth 1 -type f -name "*.d.ts" | head -n 20 || true
    exit 1
fi

echo "      Verifying both split namespace imports are present..."
if ! grep -q "@test/bcl-types/System.Transactions.js" "$consumer_facade"; then
    echo -e "${RED}❌ FAILED: Missing import from @test/bcl-types/System.Transactions.js${NC}"
    grep -n "System\\.Transactions\\.js" "$consumer_facade" || true
    exit 1
fi

if ! grep -q "@test/split-ns-types/System.Transactions.js" "$consumer_facade"; then
    echo -e "${RED}❌ FAILED: Missing import from @test/split-ns-types/System.Transactions.js${NC}"
    grep -n "System\\.Transactions\\.js" "$consumer_facade" || true
    exit 1
fi

echo ""
echo "================================================"
echo -e "${GREEN}✓ SPLIT NAMESPACE --lib VERIFIED${NC}"
echo "================================================"
echo ""
