#!/bin/bash

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "CLI npm-pack Smoke Test"
echo "================================================"
echo ""

TEST_DIR="$TESTS_DIR/cli-npm-pack-smoke"
rm -rf "$TEST_DIR"
mkdir -p "$TEST_DIR/tooling" "$TEST_DIR/lib"

echo "[1/6] Packing local npm packages..."
if ! npm pack --pack-destination "$TEST_DIR" "$PROJECT_ROOT" > "$TEST_DIR/core-pack.log" 2>&1; then
    echo -e "${RED}❌ FAILED: could not pack @tsonic/tsbindgen${NC}"
    cat "$TEST_DIR/core-pack.log"
    exit 1
fi

if ! npm pack --pack-destination "$TEST_DIR" "$PROJECT_ROOT/npm/tsbindgen" > "$TEST_DIR/wrapper-pack.log" 2>&1; then
    echo -e "${RED}❌ FAILED: could not pack tsbindgen wrapper${NC}"
    cat "$TEST_DIR/wrapper-pack.log"
    exit 1
fi

CORE_TGZ=$(tail -n1 "$TEST_DIR/core-pack.log")
WRAPPER_TGZ=$(tail -n1 "$TEST_DIR/wrapper-pack.log")

echo "[2/6] Installing packed wrapper and core..."
cat > "$TEST_DIR/tooling/package.json" <<'EOF_JSON'
{
  "name": "tsbindgen-cli-pack-smoke",
  "private": true
}
EOF_JSON

if ! (
    cd "$TEST_DIR/tooling" &&
    npm install "./../$CORE_TGZ" "./../$WRAPPER_TGZ"
) > "$TEST_DIR/npm-install.log" 2>&1; then
    echo -e "${RED}❌ FAILED: npm install of packed tsbindgen packages failed${NC}"
    cat "$TEST_DIR/npm-install.log"
    exit 1
fi

export PATH="$TEST_DIR/tooling/node_modules/.bin:$PATH"

echo "[3/6] Building SmokeLib fixture..."
BUILD_OUT="$TEST_DIR/artifacts/bin/SmokeLib/Release/net10.0"
if ! (
    cd "$TEST_DIR/lib" &&
    dotnet new classlib -n SmokeLib &&
    cd SmokeLib &&
    cat > Class1.cs <<'EOF_CS'
namespace SmokeLib;

public static class MathUtil
{
    public static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
EOF_CS
    dotnet build -c Release -o "$BUILD_OUT"
) > "$TEST_DIR/build.log" 2>&1; then
    echo -e "${RED}❌ FAILED: SmokeLib build failed${NC}"
    cat "$TEST_DIR/build.log"
    exit 1
fi

SMOKELIB_DLL="$BUILD_OUT/SmokeLib.dll"

echo "[4/6] Verifying packed CLI generate auto-discovers runtime references..."
if ! tsbindgen generate -a "$SMOKELIB_DLL" -o "$TEST_DIR/out" > "$TEST_DIR/generate.log" 2>&1; then
    echo -e "${RED}❌ FAILED: packed tsbindgen generate should succeed without --ref-dir${NC}"
    tail -50 "$TEST_DIR/generate.log"
    exit 1
fi

if [ ! -f "$TEST_DIR/out/SmokeLib.d.ts" ]; then
    echo -e "${RED}❌ FAILED: packed tsbindgen did not generate declarations${NC}"
    exit 1
fi

echo "[5/6] Verifying packed CLI resolve-closure auto-discovers runtime references..."
if ! tsbindgen resolve-closure -a "$SMOKELIB_DLL" > "$TEST_DIR/closure.json" 2> "$TEST_DIR/closure.err"; then
    echo -e "${RED}❌ FAILED: packed tsbindgen resolve-closure should succeed without --ref-dir${NC}"
    cat "$TEST_DIR/closure.err"
    cat "$TEST_DIR/closure.json"
    exit 1
fi

if ! grep -Fq '"name":"System.Private.CoreLib"' "$TEST_DIR/closure.json"; then
    echo -e "${RED}❌ FAILED: packed tsbindgen closure missing System.Private.CoreLib${NC}"
    exit 1
fi

echo "[6/6] Verifying wrapper resolves to packed core..."
if [ ! -x "$TEST_DIR/tooling/node_modules/.bin/tsbindgen" ]; then
    echo -e "${RED}❌ FAILED: packed wrapper binary missing${NC}"
    exit 1
fi

echo ""
echo -e "${GREEN}Packed CLI smoke checks passed!${NC}"
