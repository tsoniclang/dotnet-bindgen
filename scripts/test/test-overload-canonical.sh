#!/bin/bash
# Regression test for canonical overload signature handling
# Ensures methods with same name+paramCount but different param types are valid overloads
# and methods with identical TS signatures don't slip through
#
# Hot spots from TBG101 investigation:
# - System.Array.BinarySearch/Sort/IndexOf
# - System.TupleExtensions.ToValueTuple/ToTuple
# - Debug.AssertInterpolatedStringHandler.AppendFormatted
# - TaskFactory.ContinueWhenAll/ContinueWhenAny
# - Vector.Create

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Canonical Overload Signature Test"
echo "================================================"
echo ""

# Initialize runtime
init_runtime

# Use cached BCL
BCL_DIR=$(ensure_bcl default)
echo "Using BCL: $BCL_DIR"
echo ""

# Test 1: Verify no TBG101 warnings in strict mode
echo "[1/3] Checking for duplicate signature warnings..."
output=$(dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -d "$DOTNET_RUNTIME" \
    -o "$TESTS_DIR/canonical-test" --strict --logs PhaseGate 2>&1)

if echo "$output" | grep -q "TBG101"; then
    echo -e "${RED}FAILED: TBG101 warnings still present${NC}"
    echo "$output" | grep "TBG101" | head -20
    exit 1
fi
echo -e "${GREEN}✓ No TBG101 warnings${NC}"

# Test 2: Verify hot spot types compile correctly
echo ""
echo "[2/3] Checking hot spot type overloads..."

# Check System.Array (BinarySearch, Sort, IndexOf)
ARRAY_FILE="$BCL_DIR/System/internal/index.d.ts"
if [ -f "$ARRAY_FILE" ]; then
    # Count BinarySearch overloads - should be multiple (valid overloads with different param types)
    binary_count=$(grep -c "binarySearch\|BinarySearch" "$ARRAY_FILE" || echo "0")
    if [ "$binary_count" -lt 2 ]; then
        echo -e "${RED}FAILED: Expected multiple BinarySearch overloads, found $binary_count${NC}"
        exit 1
    fi
    echo "  ✓ System.Array.BinarySearch: $binary_count overloads"

    # Count Sort overloads
    sort_count=$(grep -c "sort\|Sort" "$ARRAY_FILE" | head -1 || echo "0")
    if [ "$sort_count" -lt 2 ]; then
        echo -e "${RED}FAILED: Expected multiple Sort overloads, found $sort_count${NC}"
        exit 1
    fi
    echo "  ✓ System.Array.Sort: $sort_count overloads"
fi

# Check System.TupleExtensions (ToValueTuple, ToTuple)
TUPLE_FILE="$BCL_DIR/System/internal/index.d.ts"
if [ -f "$TUPLE_FILE" ]; then
    # ToValueTuple should have multiple overloads for different tuple arities
    tuple_count=$(grep -c "toValueTuple\|ToValueTuple" "$TUPLE_FILE" || echo "0")
    if [ "$tuple_count" -lt 2 ]; then
        echo -e "${RED}FAILED: Expected multiple ToValueTuple overloads, found $tuple_count${NC}"
        exit 1
    fi
    echo "  ✓ TupleExtensions.ToValueTuple: $tuple_count overloads"
fi

# Check Vector.Create
VECTOR_FILE="$BCL_DIR/System.Numerics/internal/index.d.ts"
if [ -f "$VECTOR_FILE" ]; then
    create_count=$(grep -c "create\|Create" "$VECTOR_FILE" | head -1 || echo "0")
    if [ "$create_count" -lt 2 ]; then
        echo -e "${RED}FAILED: Expected multiple Vector.Create overloads, found $create_count${NC}"
        exit 1
    fi
    echo "  ✓ Vector.Create: $create_count overloads"
fi

# Test 3: TypeScript compilation
echo ""
echo "[3/3] Verifying TypeScript compilation..."
cd "$BCL_DIR"

# Create minimal tsconfig if not exists
if [ ! -f "tsconfig.json" ]; then
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
fi

if ! run_tsc --noEmit 2>/dev/null; then
    echo -e "${RED}FAILED: TypeScript compilation failed${NC}"
    run_tsc --noEmit 2>&1 | head -30
    exit 1
fi
echo -e "${GREEN}✓ TypeScript compiles without errors${NC}"

echo ""
echo "================================================"
echo -e "${GREEN}✓ ALL CANONICAL OVERLOAD TESTS PASSED${NC}"
echo "================================================"
echo ""
echo "Summary:"
echo "  - No TBG101 duplicate signature warnings"
echo "  - Hot spot types have multiple valid overloads"
echo "  - TypeScript compilation succeeds"
echo ""
