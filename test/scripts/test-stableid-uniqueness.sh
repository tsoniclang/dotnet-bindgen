#!/bin/bash
# StableId uniqueness test - validates that byref overloads produce distinct stableIds
# Tests that Process(int x) and Process(ref int x) have different canonicalSignatures

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "StableId Uniqueness Test"
echo "================================================"
echo ""

# Initialize runtime
init_runtime

# Test output directory
TEST_DIR="$TESTS_DIR/stableid-test"

# Clean previous runs
echo "[1/6] Cleaning previous test runs..."
rm -rf "$TEST_DIR"
mkdir -p "$TEST_DIR"

# Step 1: Build user library fixture with OverloadTest.cs
echo "[2/6] Building UserLib fixture (includes OverloadTest.cs)..."
cd "$PROJECT_ROOT/test/fixtures/UserLib"
if ! dotnet build -c Release > /dev/null 2>&1; then
    echo -e "${RED}❌ FAILED: UserLib build failed${NC}"
    exit 1
fi
cd "$PROJECT_ROOT"

# Find the DLL
userlib_dll=""
if [ -f "$PROJECT_ROOT/test/fixtures/UserLib/bin/Release/net10.0/UserLib.dll" ]; then
    userlib_dll="$PROJECT_ROOT/test/fixtures/UserLib/bin/Release/net10.0/UserLib.dll"
elif [ -f "$PROJECT_ROOT/artifacts/bin/UserLib/Release/net10.0/UserLib.dll" ]; then
    userlib_dll="$PROJECT_ROOT/artifacts/bin/UserLib/Release/net10.0/UserLib.dll"
else
    echo -e "${RED}❌ FAILED: UserLib.dll not found${NC}"
    exit 1
fi
echo "          ✓ Built: $userlib_dll"

# Step 2: Generate bindings for UserLib
echo "[3/6] Generating bindings for UserLib..."
if ! dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -- \
    generate -a "$userlib_dll" \
    -d "$DOTNET_RUNTIME" \
    -o "$TEST_DIR" \
    > "$TEST_DIR/gen.log" 2>&1; then
    echo -e "${RED}❌ FAILED: Generation failed${NC}"
    tail -50 "$TEST_DIR/gen.log"
    exit 1
fi
echo "          ✓ Generation succeeded"

# Verify output files exist
BINDINGS="$TEST_DIR/MyCompany.Utils/bindings.json"
INDEX_DTS="$TEST_DIR/MyCompany.Utils/internal/index.d.ts"

if [ ! -f "$BINDINGS" ]; then
    echo -e "${RED}❌ FAILED: bindings.json not found${NC}"
    exit 1
fi

# Step 3: Test byref vs non-byref signatures are distinct
echo "[4/6] Testing byref vs non-byref produce different stableIds..."

signature_status=$(BINDINGS="$BINDINGS" node <<'NODE'
const fs = require('fs');
const data = JSON.parse(fs.readFileSync(process.env.BINDINGS, 'utf8'));
const type = data.targetSurface.types.find(t => t.targetName === 'MyCompany.Utils.OverloadTest');
const methods = type?.methods?.filter(m => m.targetName === 'Process') ?? [];
const hasNonRef = methods.some(m => m.canonicalSignature === '(System.Int32):System.Void');
const hasRef = methods.some(m => m.canonicalSignature === '(System.Int32&):System.Void');
console.log(JSON.stringify({ hasNonRef, hasRef }));
NODE
)
non_ref=$(node -e "const s = $signature_status; process.stdout.write(s.hasNonRef ? 'yes' : '')")
ref_sig=$(node -e "const s = $signature_status; process.stdout.write(s.hasRef ? 'yes' : '')")

if [ -z "$non_ref" ]; then
    echo -e "${RED}❌ FAILED: Process(int) signature not found${NC}"
    exit 1
fi
echo "          ✓ Found Process(int) signature"

if [ -z "$ref_sig" ]; then
    echo -e "${RED}❌ FAILED: Process(ref int) byref signature not found (expected System.Int32&)${NC}"
    exit 1
fi
echo "          ✓ Found Process(ref int) byref signature (System.Int32&)"

# Step 4: Verify parameterModifiers are present in bindings.json
echo "[5/6] Testing parameterModifiers tracked in bindings.json..."

# Check for ref modifier (Process(ref int x))
if ! grep -q '"modifier": "ref"' "$BINDINGS"; then
    echo -e "${RED}❌ FAILED: ref modifier not found in bindings.json${NC}"
    exit 1
fi
echo "          ✓ ref modifier tracked"

# Check for out modifier (TryGet(string key, out int value))
if ! grep -q '"modifier": "out"' "$BINDINGS"; then
    echo -e "${RED}❌ FAILED: out modifier not found in bindings.json${NC}"
    exit 1
fi
echo "          ✓ out modifier tracked"

# Check for in modifier (ReadOnly(in int x))
# CRITICAL: 'in' must be detected correctly - it differs from 'ref' for:
# - ABI (readonly byref vs mutable byref)
# - Call legality (readonly rules)
# - Overload resolution (overloads can differ only by in/ref)
if ! grep -q '"modifier": "in"' "$BINDINGS"; then
    echo -e "${RED}❌ FAILED: in modifier not detected${NC}"
    echo "  'in' vs 'ref' must be distinguished for correct ABI and overload resolution"
    exit 1
fi
echo "          ✓ in modifier tracked"

# Step 5: TypeScript compilation test (UserLib only, skip BCL known issues)
echo "[6/6] Testing TypeScript compilation (UserLib only)..."
cd "$TEST_DIR/MyCompany.Utils"
echo '{ "compilerOptions": { "strict": true, "noEmit": true, "skipLibCheck": true } }' > tsconfig.json

if ! run_tsc --noEmit 2>&1 | tee "$TEST_DIR/tsc.log"; then
    echo -e "${RED}❌ FAILED: TypeScript compilation failed${NC}"
    cat "$TEST_DIR/tsc.log"
    exit 1
fi
echo "          ✓ UserLib TypeScript compiles successfully"

echo ""
echo "================================================"
echo -e "${GREEN}✓ STABLEID UNIQUENESS TEST PASSED${NC}"
echo "================================================"
echo ""
echo "Verified:"
echo "  ✓ Process(int) and Process(ref int) have distinct canonicalSignatures"
echo "  ✓ Byref parameter signatures include '&' suffix"
echo "  ✓ ref/out/in modifiers tracked in metadata"
echo "  ✓ TypeScript output compiles cleanly"
echo ""
