#!/bin/bash

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "CLI Runtime Autodetect Test"
echo "================================================"
echo ""

TEST_DIR="$TESTS_DIR/cli-runtime-autodetect"

echo "[1/4] Cleaning previous test runs..."
rm -rf "$TEST_DIR"
mkdir -p "$TEST_DIR"

echo "[2/4] Building UserLib fixture..."
if ! dotnet build "$PROJECT_ROOT/test/fixtures/UserLib" -c Release -o "$PROJECT_ROOT/artifacts/bin/UserLib/Release/net10.0" \
    > "$TEST_DIR/build.log" 2>&1; then
    echo -e "${RED}❌ FAILED: Build failed${NC}"
    cat "$TEST_DIR/build.log"
    exit 1
fi

USERLIB_DLL="$PROJECT_ROOT/artifacts/bin/UserLib/Release/net10.0/UserLib.dll"
if [ ! -f "$USERLIB_DLL" ]; then
    echo -e "${RED}❌ FAILED: UserLib.dll not found${NC}"
    exit 1
fi

echo "[3/4] Verifying generate auto-discovers runtime references..."
if ! dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -- \
    generate -a "$USERLIB_DLL" \
    -o "$TEST_DIR/out" \
    > "$TEST_DIR/generate.log" 2>&1; then
    echo -e "${RED}❌ FAILED: generate should succeed without --ref-dir${NC}"
    tail -50 "$TEST_DIR/generate.log"
    exit 1
fi

if [ ! -f "$TEST_DIR/out/MyCompany.Utils.d.ts" ] || [ ! -f "$TEST_DIR/out/MyCompany.Utils/internal/index.d.ts" ]; then
    echo -e "${RED}❌ FAILED: expected generated declarations${NC}"
    exit 1
fi

echo "[4/4] Verifying resolve-closure auto-discovers runtime references..."
if ! dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -- \
    resolve-closure -a "$USERLIB_DLL" \
    > "$TEST_DIR/closure.json" 2> "$TEST_DIR/closure.err"; then
    echo -e "${RED}❌ FAILED: resolve-closure should succeed without --ref-dir${NC}"
    cat "$TEST_DIR/closure.err"
    cat "$TEST_DIR/closure.json"
    exit 1
fi

if ! grep -Fq '"name":"System.Private.CoreLib"' "$TEST_DIR/closure.json"; then
    echo -e "${RED}❌ FAILED: closure output missing System.Private.CoreLib${NC}"
    exit 1
fi

echo ""
echo -e "${GREEN}All CLI runtime autodetect checks passed!${NC}"
