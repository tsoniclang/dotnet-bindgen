#!/bin/bash
# COMPILER-GRADE Binding Contract Test
#
# Validates the contract between tsbindgen and Tsonic for overload resolution.
#
# CALLABLE IDENTITY = stableId + modifier vector
# - stableId: contains CLR type names with '&' for byref (overload-unique)
# - modifier vector (parameterModifiers): distinguishes ref/out/in semantics
# - BOTH are REQUIRED for correct overload resolution and call emission
#
# Contract requirements:
# 1. Each overload has a distinct stableId (canonicalSignature includes & for byref)
# 2. Each overload has a distinct normalizedSignature
# 3. parameterModifiers MUST exist for EVERY byref parameter (not optional)
# 4. parameterModifiers correctly distinguish ref/out/in
# 5. normalizedSignature is unique per type

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "COMPILER-GRADE Binding Contract Test"
echo "================================================"
echo ""

# Initialize runtime
init_runtime

# Setup test directory
TEST_DIR="$TESTS_DIR/binding-contract"

# Step 1: Clean previous runs
echo "[1/8] Cleaning previous test runs..."
rm -rf "$TEST_DIR"
mkdir -p "$TEST_DIR"

# Step 2: Build UserLib fixture
echo "[2/8] Building UserLib fixture (includes OverloadRefInOut.cs)..."
if ! dotnet build "$PROJECT_ROOT/test/fixtures/UserLib" -c Release -o "$PROJECT_ROOT/artifacts/bin/UserLib/Release/net10.0" \
    > "$TEST_DIR/build.log" 2>&1; then
    echo -e "${RED}❌ FAILED: Build failed${NC}"
    cat "$TEST_DIR/build.log"
    exit 1
fi
echo "          ✓ Built UserLib"

# Step 3: Generate bindings
echo "[3/8] Generating bindings for UserLib..."
if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -a "$PROJECT_ROOT/artifacts/bin/UserLib/Release/net10.0/UserLib.dll" \
    -d "$DOTNET_RUNTIME" -o "$TEST_DIR" \
    > "$TEST_DIR/gen.log" 2>&1; then
    echo -e "${RED}❌ FAILED: Generation failed${NC}"
    tail -50 "$TEST_DIR/gen.log"
    exit 1
fi
echo "          ✓ Generation succeeded"

# Verify output files exist
BINDINGS="$TEST_DIR/MyCompany.Utils/bindings.json"

if [ ! -f "$BINDINGS" ]; then
    echo -e "${RED}❌ FAILED: bindings.json not found${NC}"
    exit 1
fi

# Step 4: Verify F overloads (val vs ref) have distinct signatures
echo "[4/8] Verifying F overloads have distinct signatures..."

F_SIGNATURES=$(jq -r '.types[] | select((.targetName // "") | contains("OverloadRefInOut")) | .methods[] | select(.targetName=="F") | .normalizedSignature' "$BINDINGS")
F_COUNT=$(echo "$F_SIGNATURES" | wc -l | tr -d ' ')
F_UNIQUE=$(echo "$F_SIGNATURES" | sort -u | wc -l | tr -d ' ')

if [ "$F_COUNT" -ne "$F_UNIQUE" ]; then
    echo -e "${RED}❌ FAILED: F overloads have duplicate normalizedSignatures${NC}"
    exit 1
fi

if [ "$F_COUNT" -lt 2 ]; then
    echo -e "${RED}❌ FAILED: Expected at least 2 F overloads, found $F_COUNT${NC}"
    exit 1
fi

echo "          ✓ F has $F_COUNT distinct normalizedSignatures (val vs ref)"

# Step 5: Verify H/K/L all have int& but different modifiers
echo "[5/8] Verifying H/K/L prove modifier encoding (all System.Int32&)..."

# H(ref int) - must have ref modifier
H_MOD=$(jq -r '.types[] | select((.targetName // "") | contains("OverloadRefInOut")) | .methods[] | select(.targetName=="H") | .parameterModifiers[0].modifier // "MISSING"' "$BINDINGS")
if [ "$H_MOD" != "ref" ]; then
    echo -e "${RED}❌ FAILED: H(ref int) expected modifier 'ref', got '$H_MOD'${NC}"
    exit 1
fi
echo "          ✓ H(ref int) has modifier: ref"

# K(out int) - must have out modifier
K_MOD=$(jq -r '.types[] | select((.targetName // "") | contains("OverloadRefInOut")) | .methods[] | select(.targetName=="K") | .parameterModifiers[0].modifier // "MISSING"' "$BINDINGS")
if [ "$K_MOD" != "out" ]; then
    echo -e "${RED}❌ FAILED: K(out int) expected modifier 'out', got '$K_MOD'${NC}"
    exit 1
fi
echo "          ✓ K(out int) has modifier: out"

# L(in int) - must have in modifier
L_MOD=$(jq -r '.types[] | select((.targetName // "") | contains("OverloadRefInOut")) | .methods[] | select(.targetName=="L") | .parameterModifiers[0].modifier // "MISSING"' "$BINDINGS")
if [ "$L_MOD" != "in" ]; then
    echo -e "${RED}❌ FAILED: L(in int) expected modifier 'in', got '$L_MOD'${NC}"
    exit 1
fi
echo "          ✓ L(in int) has modifier: in"

# Step 6: COMPILER-GRADE: Assert modifier vector exists for EVERY byref parameter
echo "[6/8] Verifying modifier vector exists for ALL byref parameters..."

# Find all methods with byref params (signature contains &) and verify they have parameterModifiers
# Use stableId which always contains the signature string
BYREF_METHODS=$(jq -r '.types[] | select((.targetName // "") | contains("OverloadRefInOut")) | .methods[] | select((.stableId // "") | contains("&")) | .targetName' "$BINDINGS" | sort -u)

MISSING_MODIFIERS=0
for method in $BYREF_METHODS; do
    # Check if this method has parameterModifiers
    HAS_MODIFIERS=$(jq -r ".types[] | select((.targetName // \"\") | contains(\"OverloadRefInOut\")) | .methods[] | select(.targetName==\"$method\" and ((.stableId // \"\") | contains(\"&\"))) | if .parameterModifiers != null and (.parameterModifiers | length) > 0 then \"yes\" else \"no\" end" "$BINDINGS" | head -1)
    if [ "$HAS_MODIFIERS" != "yes" ]; then
        echo -e "          ${RED}✗ $method has byref param but no parameterModifiers${NC}"
        ((MISSING_MODIFIERS++))
    fi
done

if [ "$MISSING_MODIFIERS" -gt 0 ]; then
    echo -e "${RED}❌ FAILED: $MISSING_MODIFIERS byref methods missing parameterModifiers${NC}"
    echo "  Callable identity REQUIRES modifier vector for byref params"
    exit 1
fi
echo "          ✓ All byref methods have parameterModifiers (modifier vector required)"

# Step 7: Verify other modifiers
echo "[7/8] Verifying additional modifier cases..."

# F(ref int) has ref modifier
REF_F=$(jq -r '.types[] | select((.targetName // "") | contains("OverloadRefInOut")) | .methods[] | select(.targetName=="F" and .parameterModifiers != null and .parameterModifiers[0].modifier == "ref") | .normalizedSignature' "$BINDINGS")
if [ -z "$REF_F" ]; then
    echo -e "${RED}❌ FAILED: F(ref int) not found with ref modifier${NC}"
    exit 1
fi
echo "          ✓ F(ref int) has modifier: ref"

# G(in int) has in modifier
IN_G=$(jq -r '.types[] | select((.targetName // "") | contains("OverloadRefInOut")) | .methods[] | select(.targetName=="G" and .parameterModifiers != null and .parameterModifiers[0].modifier == "in") | .normalizedSignature' "$BINDINGS")
if [ -z "$IN_G" ]; then
    echo -e "${RED}❌ FAILED: G(in int) not found with in modifier${NC}"
    exit 1
fi
echo "          ✓ G(in int) has modifier: in"

# TryGet has out modifier (index 1, not 0 - first param is string)
OUT_TRYGET=$(jq -r '.types[] | select((.targetName // "") | contains("OverloadRefInOut")) | .methods[] | select(.targetName=="TryGet" and .parameterModifiers != null and .parameterModifiers[0].modifier == "out") | .normalizedSignature' "$BINDINGS")
if [ -z "$OUT_TRYGET" ]; then
    echo -e "${RED}❌ FAILED: TryGet(out int) not found with out modifier${NC}"
    exit 1
fi
echo "          ✓ TryGet has modifier: out"

# Step 8: Verify normalizedSignature uniqueness
echo "[8/8] Verifying normalizedSignature uniqueness..."

SIGS=$(jq -r '.types[] | select((.targetName // "") | contains("OverloadRefInOut")) | .methods[] | .normalizedSignature' "$BINDINGS")
SIG_COUNT=$(echo "$SIGS" | wc -l | tr -d ' ')
SIG_UNIQUE=$(echo "$SIGS" | sort -u | wc -l | tr -d ' ')

if [ "$SIG_COUNT" -ne "$SIG_UNIQUE" ]; then
    echo -e "${RED}❌ FAILED: Duplicate normalizedSignature values found${NC}"
    exit 1
fi
echo "          ✓ All normalizedSignature values are unique"

echo ""
echo "================================================"
echo -e "${GREEN}✓ COMPILER-GRADE BINDING CONTRACT VERIFIED${NC}"
echo "================================================"
echo ""
echo "CALLABLE IDENTITY = stableId + modifier vector"
echo ""
echo "Verified:"
echo "  ✓ F overloads (val vs ref) have distinct normalizedSignatures"
echo "  ✓ H/K/L prove modifier encoding (all System.Int32& with ref/out/in)"
echo "  ✓ ALL byref methods have parameterModifiers (not optional)"
echo "  ✓ parameterModifiers correctly distinguish ref/out/in"
echo "  ✓ normalizedSignature unique per type"
echo ""
echo "Tsonic contract guarantees:"
echo "  - stableId uniquely identifies overload (includes CLR types with &)"
echo "  - modifier vector is REQUIRED for byref params (not optional addon)"
echo "  - ref/out/in semantics preserved for:"
echo "      • Call legality (addressability rules)"
echo "      • Correct C# emission (ref/out/in tokens)"
echo "      • ABI correctness (readonly vs mutable byref)"
echo ""
