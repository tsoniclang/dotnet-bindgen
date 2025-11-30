#!/bin/bash
# CLROf Wrapping Regression Test
#
# This test verifies that SafeToExtend correctly identifies types where:
# - The interface expects CLROf<primitive> (e.g., CLROf<char>)
# - The class surface uses the primitive directly (e.g., char)
#
# These types must NOT have merged interface extends, because the property
# types are incompatible (would cause TS2430).
#
# Canary: CharEnumerator
# - CharEnumerator.Current returns 'char' (primitive)
# - IEnumerator_1<Char>.Current returns 'CLROf<char>' (wrapper)
# - SafeToExtend must mark IEnumerator_1<Char> as unsafe

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================================"
echo "CLROf Wrapping Regression Test"
echo "================================================================"
echo ""

# Use cached BCL output
BCL_DIR=$(ensure_bcl default)
SYSTEM_INDEX="$BCL_DIR/System/internal/index.d.ts"

if [ ! -f "$SYSTEM_INDEX" ]; then
    echo -e "${RED}ERROR: $SYSTEM_INDEX not found${NC}"
    exit 1
fi

echo "Testing CharEnumerator (primitive/CLROf mismatch canary)..."
echo ""

FAILED=0

# Test 1: CharEnumerator should NOT extend IEnumerator_1
# If it does, SafeToExtend is not working correctly
if grep -E "interface CharEnumerator\\\$instance.*extends.*IEnumerator_1" "$SYSTEM_INDEX" > /dev/null 2>&1; then
    echo -e "  ${RED}[FAIL]${NC} CharEnumerator incorrectly extends IEnumerator_1"
    echo ""
    echo "CharEnumerator.Current returns 'char' (primitive)"
    echo "IEnumerator_1<Char>.Current expects 'CLROf<char>' (wrapper)"
    echo "These are incompatible - SafeToExtend should have filtered this!"
    echo ""
    grep -A5 "interface CharEnumerator\\\$instance" "$SYSTEM_INDEX" || true
    FAILED=1
else
    echo -e "  ${GREEN}[PASS]${NC} CharEnumerator does NOT extend IEnumerator_1 (correct)"
fi

# Test 2: CharEnumerator SHOULD have a view for IEnumerator_1
# This ensures the interface is still accessible via views
if ! grep -E "As_IEnumerator_1.*IEnumerator_1\\\$instance<CLROf<char>>" "$SYSTEM_INDEX" > /dev/null 2>&1; then
    echo -e "  ${RED}[FAIL]${NC} CharEnumerator missing IEnumerator_1 view with CLROf<char>"
    echo ""
    echo "Expected: As_IEnumerator_1(): IEnumerator_1\$instance<CLROf<char>>"
    echo ""
    grep -A10 "__CharEnumerator\\\$views" "$SYSTEM_INDEX" || true
    FAILED=1
else
    echo -e "  ${GREEN}[PASS]${NC} CharEnumerator has IEnumerator_1 view with CLROf<char> (correct)"
fi

# Test 3: CharEnumerator SHOULD extend ICloneable (safe interface)
# This verifies SafeToExtend allows safe interfaces
if ! grep -E "interface CharEnumerator\\\$instance extends ICloneable" "$SYSTEM_INDEX" > /dev/null 2>&1; then
    echo -e "  ${RED}[FAIL]${NC} CharEnumerator should extend ICloneable (it's safe)"
    echo ""
    grep -A3 "interface CharEnumerator\\\$instance" "$SYSTEM_INDEX" || true
    FAILED=1
else
    echo -e "  ${GREEN}[PASS]${NC} CharEnumerator extends ICloneable (correct - it's safe)"
fi

# Test 4: CharEnumerator.Current should return 'char' not 'CLROf<char>'
# This verifies the class surface uses primitives directly
if ! grep -E "readonly Current: char;" "$SYSTEM_INDEX" > /dev/null 2>&1; then
    echo -e "  ${RED}[FAIL]${NC} CharEnumerator.Current should return 'char' (primitive)"
    echo ""
    grep -B2 -A2 "Current" "$SYSTEM_INDEX" | grep -A2 "CharEnumerator" || true
    FAILED=1
else
    echo -e "  ${GREEN}[PASS]${NC} CharEnumerator.Current returns 'char' (correct - uses primitive)"
fi

echo ""
if [ $FAILED -eq 0 ]; then
    echo "================================================================"
    echo -e "${GREEN}CLROf Regression Test: ALL PASSED${NC}"
    echo "================================================================"
    echo ""
    echo "SafeToExtend correctly handles primitive/CLROf type mismatches:"
    echo "  - CharEnumerator does not extend IEnumerator_1<Char> (would cause TS2430)"
    echo "  - CharEnumerator extends ICloneable (safe, no type mismatch)"
    echo "  - CLROf<char> wrapping is used in view return types"
    echo ""
    exit 0
else
    echo "================================================================"
    echo -e "${RED}CLROf Regression Test: FAILED${NC}"
    echo "================================================================"
    exit 1
fi
