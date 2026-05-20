#!/bin/bash
# Regression test: BaseOverloadAdder must NOT treat ViewOnly interface members as satisfying
# base-class overload coverage.
#
# Fixture: test/fixtures/UserLib/BaseOverloadViewOnlyDoesNotSatisfy.cs
# Scenario:
# - BaseWriter has Dispose() and Dispose(bool)
# - DerivedWriter overrides Dispose(bool) only
# - StructuralConformance adds a ViewOnly IMyDisposable.Dispose() on DerivedWriter
# - BaseOverloadAdder must still inject Dispose() onto DerivedWriter's class surface

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "BaseOverload ViewOnly Coverage Test"
echo "================================================"
echo ""

# Initialize runtime
init_runtime

OUT_DIR="$TESTS_DIR/baseoverload-viewonly"

echo "[1/3] Cleaning previous test runs..."
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

echo "[2/3] Building UserLib fixture..."
cd "$PROJECT_ROOT/test/fixtures/UserLib"
dotnet build -c Release > /dev/null 2>&1
cd "$PROJECT_ROOT"

userlib_dll=""
if [ -f "$PROJECT_ROOT/test/fixtures/UserLib/bin/Release/net10.0/UserLib.dll" ]; then
    userlib_dll="$PROJECT_ROOT/test/fixtures/UserLib/bin/Release/net10.0/UserLib.dll"
elif [ -f "$PROJECT_ROOT/artifacts/bin/UserLib/Release/net10.0/UserLib.dll" ]; then
    userlib_dll="$PROJECT_ROOT/artifacts/bin/UserLib/Release/net10.0/UserLib.dll"
else
    echo -e "${RED}❌ FAILED: UserLib.dll not found${NC}"
    exit 1
fi

echo "          ✓ Built fixture ($userlib_dll)"

echo ""
echo "[3/3] Generating declarations and validating DerivedWriter overloads..."

dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -a "$userlib_dll" \
    -d "$DOTNET_RUNTIME" \
    -o "$OUT_DIR" \
    > /dev/null 2>&1

DTS_FILE="$OUT_DIR/MyCompany.Utils/internal/index.d.ts"
BINDINGS_FILE="$OUT_DIR/MyCompany.Utils/bindings.json"

if [ ! -f "$DTS_FILE" ]; then
    echo -e "${RED}❌ FAILED: Expected output file missing: $DTS_FILE${NC}"
    exit 1
fi

if [ ! -f "$BINDINGS_FILE" ]; then
    echo -e "${RED}❌ FAILED: Expected bindings file missing: $BINDINGS_FILE${NC}"
    exit 1
fi

# Sanity: ensure StructuralConformance added the ViewOnly interface member on DerivedWriter.
BINDINGS_FILE="$BINDINGS_FILE" node <<'NODE'
const fs = require('fs');
const data = JSON.parse(fs.readFileSync(process.env.BINDINGS_FILE, 'utf8'));
const derived = data.types.find(t => t.targetName === 'MyCompany.Utils.DerivedWriter');
if (!derived) {
  console.error('❌ FAILED: DerivedWriter type not found in bindings.json');
  process.exit(1);
}
const viewOnlyDispose = derived.methods.find(m =>
  m.emitScope === 'ViewOnly' &&
  m.provenance === 'ExplicitView' &&
  typeof m.stableId === 'string' &&
  m.stableId.includes(':MyCompany.Utils.IMyDisposable::Dispose():System.Void')
);
if (!viewOnlyDispose) {
  console.error('❌ FAILED: Expected ViewOnly ExplicitView IMyDisposable.Dispose() on DerivedWriter');
  process.exit(1);
}
console.log('  [PASS] DerivedWriter has ViewOnly IMyDisposable.Dispose() (ExplicitView)');
NODE

echo ""

# Extract the DerivedWriter$instance interface block and assert both overloads exist.
DERIVED_BLOCK=$(awk '
    $0 ~ /^export interface DerivedWriter\$instance/ { inblock=1 }
    inblock { print }
    inblock && $0 ~ /^}/ { exit }
' "$DTS_FILE")

echo "$DERIVED_BLOCK" | grep -Fq "Dispose(disposing: boolean): void;"
echo -e "  ${GREEN}[PASS]${NC} DerivedWriter declares Dispose(bool)"

echo "$DERIVED_BLOCK" | grep -Fq "Dispose(): void;"
echo -e "  ${GREEN}[PASS]${NC} DerivedWriter includes injected base Dispose() overload"

echo ""
echo "================================================"
echo -e "${GREEN}✓ BASEOVERLOAD VIEWONLY COVERAGE TEST PASSED${NC}"
echo "================================================"
echo ""
