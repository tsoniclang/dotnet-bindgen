#!/bin/bash
# Primitive Lifting Regression Test
#
# This test verifies that:
# 1. Generic type arguments use CLR type names (Char, Int32, etc.)
# 2. Value positions use TS primitive aliases (char, int, etc.)
# 3. SafeToExtend correctly identifies types where interface/class signatures differ
#
# Canary: CharEnumerator
# - CharEnumerator.Current returns 'char' (TS primitive alias)
# - IEnumerator_1<Char>.Current returns 'Char' (CLR type name)
# - SafeToExtend must mark IEnumerator_1<Char> as unsafe due to signature mismatch

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================================"
echo "Primitive Lifting Regression Test"
echo "================================================================"
echo ""

# Use cached BCL output
BCL_DIR=$(ensure_bcl default)
SYSTEM_INDEX="$BCL_DIR/System/internal/index.d.ts"

if [ ! -f "$SYSTEM_INDEX" ]; then
    echo -e "${RED}ERROR: $SYSTEM_INDEX not found${NC}"
    exit 1
fi

echo "Testing CharEnumerator (primitive/CLR name mismatch canary)..."
echo ""

FAILED=0

# Test 1: CharEnumerator should NOT extend IEnumerator_1
# If it does, SafeToExtend is not working correctly
if grep -E "interface CharEnumerator\\\$instance.*extends.*IEnumerator_1" "$SYSTEM_INDEX" > /dev/null 2>&1; then
    echo -e "  ${RED}[FAIL]${NC} CharEnumerator incorrectly extends IEnumerator_1"
    echo ""
    echo "CharEnumerator.Current returns 'char' (TS primitive)"
    echo "IEnumerator_1<Char>.Current expects 'Char' (CLR type name)"
    echo "These are incompatible - SafeToExtend should have filtered this!"
    echo ""
    grep -A5 "interface CharEnumerator\\\$instance" "$SYSTEM_INDEX" || true
    FAILED=1
else
    echo -e "  ${GREEN}[PASS]${NC} CharEnumerator does NOT extend IEnumerator_1 (correct)"
fi

# Test 2: CharEnumerator SHOULD have a view for IEnumerator_1 with CLR type name (Char)
# This ensures the interface is still accessible via views
if ! grep -E "As_IEnumerator_1.*IEnumerator_1\\\$instance<Char>" "$SYSTEM_INDEX" > /dev/null 2>&1; then
    echo -e "  ${RED}[FAIL]${NC} CharEnumerator missing IEnumerator_1 view with Char"
    echo ""
    echo "Expected: As_IEnumerator_1(): IEnumerator_1\$instance<Char>"
    echo ""
    grep -A10 "__CharEnumerator\\\$views" "$SYSTEM_INDEX" || true
    FAILED=1
else
    echo -e "  ${GREEN}[PASS]${NC} CharEnumerator has IEnumerator_1 view with Char (correct)"
fi

# Test 3: CharEnumerator SHOULD carry ICloneable structurally.
# The current surface uses nominal interface brands plus the concrete method, so direct
# `extends ICloneable$instance` is not required for assignability.
if ! grep -A12 -E "interface CharEnumerator\\\$instance" "$SYSTEM_INDEX" | grep -E "__tsonic_iface_System_ICloneable" > /dev/null 2>&1 ||
   ! grep -A12 -E "interface CharEnumerator\\\$instance" "$SYSTEM_INDEX" | grep -E "Clone\\(\\): unknown;" > /dev/null 2>&1; then
    echo -e "  ${RED}[FAIL]${NC} CharEnumerator should carry ICloneable brand and Clone()"
    echo ""
    grep -A12 "interface CharEnumerator\\\$instance" "$SYSTEM_INDEX" || true
    FAILED=1
else
    echo -e "  ${GREEN}[PASS]${NC} CharEnumerator carries ICloneable brand and Clone()"
fi

# Test 4: CharEnumerator.Current should return 'char' not 'Char'
# This verifies the class surface uses primitives directly
if ! grep -E "readonly Current: char;" "$SYSTEM_INDEX" > /dev/null 2>&1; then
    echo -e "  ${RED}[FAIL]${NC} CharEnumerator.Current should return 'char' (primitive)"
    echo ""
    grep -B2 -A2 "Current" "$SYSTEM_INDEX" | grep -A2 "CharEnumerator" || true
    FAILED=1
else
    echo -e "  ${GREEN}[PASS]${NC} CharEnumerator.Current returns 'char' (correct - uses primitive)"
fi

# Test 5: Verify no CLROf usage in output (CLROf has been removed)
if grep -E "CLROf<" "$SYSTEM_INDEX" > /dev/null 2>&1; then
    echo -e "  ${RED}[FAIL]${NC} Found CLROf<> usage - should have been removed"
    echo ""
    grep -n "CLROf<" "$SYSTEM_INDEX" | head -5
    FAILED=1
else
    echo -e "  ${GREEN}[PASS]${NC} No CLROf<> usage found (correct - uses direct CLR names)"
fi

echo ""
if [ $FAILED -eq 0 ]; then
    echo "================================================================"
    echo -e "${GREEN}Primitive Lifting Test: ALL PASSED${NC}"
    echo "================================================================"
    echo ""
    echo "SafeToExtend correctly handles primitive/CLR type name differences:"
    echo "  - CharEnumerator does not extend IEnumerator_1<Char> (would cause TS2430)"
    echo "  - CharEnumerator carries ICloneable brand + Clone() (safe assignability)"
    echo "  - Generic type args use CLR names (Char, Int32, etc.)"
    echo "  - Value positions use TS primitives (char, int, etc.)"
    echo ""
    exit 0
else
    echo "================================================================"
    echo -e "${RED}Primitive Lifting Test: FAILED${NC}"
    echo "================================================================"
    exit 1
fi
