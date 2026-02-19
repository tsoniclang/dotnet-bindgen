#!/bin/bash
# Regression: --lib merge should not hard-fail when multiple library packages
# contain the same CLR full name (common with duplicated internal helper types).
#
# We construct two --lib packages that both contain MyCompany.Duplicate.DupType,
# then generate a consumer that references OTHER types from both packages
# (LibAThing / LibBThing). Before the fix, tsbindgen failed up-front while
# merging --lib contracts. After the fix, it should succeed because DupType is
# never referenced by the emitted consumer surface.

set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Library Mode Ambiguous-Type Ownership Test"
echo "================================================"
echo ""

init_runtime

AMBIG_TEST_DIR="$TESTS_DIR/lib-ambiguous-type"

echo "[1/6] Cleaning previous test runs..."
rm -rf "$AMBIG_TEST_DIR"
mkdir -p "$AMBIG_TEST_DIR"

echo "[2/6] Generating BCL types (library contract)..."
if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -d "$DOTNET_RUNTIME" \
    -o "$AMBIG_TEST_DIR/bcl-types" \
    > "$AMBIG_TEST_DIR/bcl-gen.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: BCL generation failed${NC}"
    tail -50 "$AMBIG_TEST_DIR/bcl-gen.txt"
    exit 1
fi
echo '{"name": "@test/bcl-types", "version": "1.0.0"}' > "$AMBIG_TEST_DIR/bcl-types/package.json"

echo "[3/6] Building DupLibA fixture..."
if ! dotnet build "$PROJECT_ROOT/test/fixtures/DuplicateType/DupLibA/DupLibA.csproj" -c Release > /dev/null 2>&1; then
    echo -e "${RED}❌ FAILED: DupLibA build failed${NC}"
    exit 1
fi

dupa_dll=""
if [ -f "$PROJECT_ROOT/test/fixtures/DuplicateType/DupLibA/bin/Release/net10.0/DupLibA.dll" ]; then
    dupa_dll="$PROJECT_ROOT/test/fixtures/DuplicateType/DupLibA/bin/Release/net10.0/DupLibA.dll"
elif [ -f "$PROJECT_ROOT/artifacts/bin/DupLibA/Release/net10.0/DupLibA.dll" ]; then
    dupa_dll="$PROJECT_ROOT/artifacts/bin/DupLibA/Release/net10.0/DupLibA.dll"
else
    echo -e "${RED}❌ FAILED: DupLibA.dll not found${NC}"
    exit 1
fi

echo "      Generating DupLibA bindings (filtered by --lib BCL)..."
if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -a "$dupa_dll" \
    -d "$DOTNET_RUNTIME" \
    -o "$AMBIG_TEST_DIR/dup-a-types" \
    --lib "$AMBIG_TEST_DIR/bcl-types" \
    > "$AMBIG_TEST_DIR/dup-a-gen.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: DupLibA generation (--lib) failed${NC}"
    tail -100 "$AMBIG_TEST_DIR/dup-a-gen.txt"
    exit 1
fi
echo '{"name": "@test/dup-a-types", "version": "1.0.0"}' > "$AMBIG_TEST_DIR/dup-a-types/package.json"

echo "[4/6] Building DupLibB fixture..."
if ! dotnet build "$PROJECT_ROOT/test/fixtures/DuplicateType/DupLibB/DupLibB.csproj" -c Release > /dev/null 2>&1; then
    echo -e "${RED}❌ FAILED: DupLibB build failed${NC}"
    exit 1
fi

dupb_dll=""
if [ -f "$PROJECT_ROOT/test/fixtures/DuplicateType/DupLibB/bin/Release/net10.0/DupLibB.dll" ]; then
    dupb_dll="$PROJECT_ROOT/test/fixtures/DuplicateType/DupLibB/bin/Release/net10.0/DupLibB.dll"
elif [ -f "$PROJECT_ROOT/artifacts/bin/DupLibB/Release/net10.0/DupLibB.dll" ]; then
    dupb_dll="$PROJECT_ROOT/artifacts/bin/DupLibB/Release/net10.0/DupLibB.dll"
else
    echo -e "${RED}❌ FAILED: DupLibB.dll not found${NC}"
    exit 1
fi

echo "      Generating DupLibB bindings (filtered by --lib BCL)..."
if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -a "$dupb_dll" \
    -d "$DOTNET_RUNTIME" \
    -o "$AMBIG_TEST_DIR/dup-b-types" \
    --lib "$AMBIG_TEST_DIR/bcl-types" \
    > "$AMBIG_TEST_DIR/dup-b-gen.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: DupLibB generation (--lib) failed${NC}"
    tail -100 "$AMBIG_TEST_DIR/dup-b-gen.txt"
    exit 1
fi
echo '{"name": "@test/dup-b-types", "version": "1.0.0"}' > "$AMBIG_TEST_DIR/dup-b-types/package.json"

echo "[5/6] Building DupConsumer fixture (references both dup libs)..."
if ! dotnet build "$PROJECT_ROOT/test/fixtures/DuplicateType/DupConsumer/DupConsumer.csproj" -c Release > /dev/null 2>&1; then
    echo -e "${RED}❌ FAILED: DupConsumer build failed${NC}"
    exit 1
fi

consumer_dll=""
if [ -f "$PROJECT_ROOT/test/fixtures/DuplicateType/DupConsumer/bin/Release/net10.0/DupConsumer.dll" ]; then
    consumer_dll="$PROJECT_ROOT/test/fixtures/DuplicateType/DupConsumer/bin/Release/net10.0/DupConsumer.dll"
elif [ -f "$PROJECT_ROOT/artifacts/bin/DupConsumer/Release/net10.0/DupConsumer.dll" ]; then
    consumer_dll="$PROJECT_ROOT/artifacts/bin/DupConsumer/Release/net10.0/DupConsumer.dll"
else
    echo -e "${RED}❌ FAILED: DupConsumer.dll not found${NC}"
    exit 1
fi

echo "[6/6] Generating consumer bindings with TWO dup --lib packages (ambiguous CLR full name)..."
if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -a "$consumer_dll" \
    -d "$DOTNET_RUNTIME" \
    -o "$AMBIG_TEST_DIR/consumer-types" \
    --lib "$AMBIG_TEST_DIR/bcl-types" \
    --lib "$AMBIG_TEST_DIR/dup-a-types" \
    --lib "$AMBIG_TEST_DIR/dup-b-types" \
    > "$AMBIG_TEST_DIR/consumer-gen.txt" 2>&1; then
    echo -e "${RED}❌ FAILED: Consumer generation with ambiguous type ownership failed${NC}"
    tail -200 "$AMBIG_TEST_DIR/consumer-gen.txt"
    exit 1
fi

consumer_facade="$AMBIG_TEST_DIR/consumer-types/MyCompany.DuplicateConsumer.d.ts"
if [ ! -f "$consumer_facade" ]; then
    echo -e "${RED}❌ FAILED: Consumer facade file not found:${NC} $consumer_facade"
    find "$AMBIG_TEST_DIR/consumer-types" -maxdepth 1 -type f -name "*.d.ts" | head -n 20 || true
    exit 1
fi

echo "      Verifying imports from BOTH dup packages are present..."
if ! grep -q "@test/dup-a-types/MyCompany.Duplicate.js" "$consumer_facade"; then
    echo -e "${RED}❌ FAILED: Missing import from @test/dup-a-types/MyCompany.Duplicate.js${NC}"
    grep -n "MyCompany\\.Duplicate\\.js" "$consumer_facade" || true
    exit 1
fi

if ! grep -q "@test/dup-b-types/MyCompany.Duplicate.js" "$consumer_facade"; then
    echo -e "${RED}❌ FAILED: Missing import from @test/dup-b-types/MyCompany.Duplicate.js${NC}"
    grep -n "MyCompany\\.Duplicate\\.js" "$consumer_facade" || true
    exit 1
fi

echo ""
echo "================================================"
echo -e "${GREEN}✓ AMBIGUOUS TYPE OWNERSHIP DEFERRED AS EXPECTED${NC}"
echo "================================================"
echo ""
