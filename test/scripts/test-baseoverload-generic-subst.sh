#!/bin/bash
# Regression test: BaseOverloadAdder must close self-referential generics and respect covariant returns.
#
# Fixture: test/fixtures/UserLib/BaseOverloadGenericSubstitution.cs
# Expectations:
# - Application adds a new overload "get(string name)" and must remain TS-compatible with Router,
#   so BaseOverloadAdder should add Router's overloads to Application.
# - Those added overloads must NOT leak an unbound TSelf (which becomes `unknown` in TS).
# - Covariant hiding/override should not force a redundant base overload.

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "BaseOverload Generic Substitution Test"
echo "================================================"
echo ""

# Initialize runtime
init_runtime

# Test output directory
OUT_DIR="$TESTS_DIR/baseoverload-generic-subst"

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
echo "[3/3] Generating declarations and validating signatures..."

dotnet run --project "$PROJECT_ROOT/src/DotnetBindgen/DotnetBindgen.csproj" -- \
    generate -a "$userlib_dll" \
    -d "$DOTNET_RUNTIME" \
    -o "$OUT_DIR" \
    > /dev/null 2>&1

FILE="$OUT_DIR/MyCompany.Utils/internal/index.d.ts"
if [ ! -f "$FILE" ]; then
    echo -e "${RED}❌ FAILED: Expected output file missing: $FILE${NC}"
    exit 1
fi

# Application must inherit/retain Router overloads for TS2430 compatibility. In the
# current structural surface this is expressed by extending Router$instance; the closed
# Router return lives on RoutingHost_1<Router>, not by duplicating the method on Application.
assert_grep 'interface Application$instance extends Router$instance' "$FILE" "Application inherits Router surface"
assert_grep 'interface Router$instance extends RoutingHost_1$instance<Router>' "$FILE" "Router closes TSelf to Router"
assert_grep "get(path: string, callback: Handler): TSelf;" "$FILE" "RoutingHost_1 keeps generic get return"

if grep -Fq "get(path: string, callback: Handler): unknown;" "$FILE"; then
    echo -e "${RED}❌ FAILED: Found unbound/unknown return type leak for Application.get(path, callback)${NC}"
    grep -n "get(path: string, callback: Handler)" "$FILE" | head -20
    exit 1
fi
echo -e "  ${GREEN}[PASS]${NC} No unknown-return leak for get(path, callback)"

echo ""
echo "================================================"
echo -e "${GREEN}✓ BASEOVERLOAD GENERIC SUBSTITUTION TEST PASSED${NC}"
echo "================================================"
echo ""
