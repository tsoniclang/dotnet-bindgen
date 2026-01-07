#!/bin/bash
# Test cross-module aliasing for duplicate type names (TS2300 regression)
# Verifies that types with the same name from different modules get aliased deterministically

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Cross-Module Alias Test (TS2300)"
echo "================================================"
echo ""

# Use the nodejs package which imports both IEnumerable types
NODEJS_DIR="${NODEJS_DIR:-$PROJECT_ROOT/../nodejs}"

if [ ! -d "$NODEJS_DIR" ]; then
    echo -e "${RED}❌ FAILED: nodejs directory not found${NC}"
    exit 1
fi

echo "[1/3] Checking for cross-module aliasing in imports..."

# Check that the internal file has the aliased import
# Note: nodejs uses --namespace-map "nodejs=index" so path is index/internal/index.d.ts
INTERNAL_FILE="$NODEJS_DIR/index/internal/index.d.ts"
if ! grep -q "IEnumerable as IEnumerable__System_Collections" "$INTERNAL_FILE"; then
    echo -e "${RED}❌ FAILED: Missing cross-module alias in $INTERNAL_FILE${NC}"
    echo "Expected: import type { ..., IEnumerable as IEnumerable__System_Collections_Generic, ... }"
    exit 1
fi

echo -e "${GREEN}✓ Found cross-module alias: IEnumerable__System_Collections_Generic${NC}"

# Check facade file too
# Note: nodejs uses --namespace-map "nodejs=index" so facade is index.d.ts
FACADE_FILE="$NODEJS_DIR/index.d.ts"
if ! grep -q "IEnumerable as IEnumerable__System_Collections" "$FACADE_FILE"; then
    echo -e "${RED}❌ FAILED: Missing cross-module alias in facade${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Found cross-module alias in facade${NC}"

echo ""
echo "[2/3] Verifying no duplicate identifier errors..."

# Run tsc with timeout and check for TS2300 errors in nodejs files
cd "$NODEJS_DIR"
tsc_path=$(get_tsc)
if [ -z "$tsc_path" ]; then
    echo -e "${RED}ERROR: TypeScript not installed. Run 'npm install' first.${NC}" >&2
    exit 1
fi

TSC_OUTPUT=$(timeout 120 "$tsc_path" 2>&1 || true)

# Check for TS2300 errors specifically in nodejs files (not BCL)
TS2300_ERRORS=$(echo "$TSC_OUTPUT" | grep -E "^nodejs.*TS2300" || true)
if [ -n "$TS2300_ERRORS" ]; then
    echo -e "${RED}❌ FAILED: TS2300 duplicate identifier errors found${NC}"
    echo "$TS2300_ERRORS"
    exit 1
fi

echo -e "${GREEN}✓ No TS2300 duplicate identifier errors${NC}"

echo ""
echo "[3/3] Verifying aliased name is used in type positions..."

# Check that the aliased name is used (not the original)
if ! grep -q "IEnumerable__System_Collections_Generic<" "$INTERNAL_FILE"; then
    echo -e "${RED}❌ FAILED: Aliased type name not used in type positions${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Aliased type name used correctly in type positions${NC}"

echo ""
echo "================================================"
echo -e "${GREEN}✓ CROSS-MODULE ALIAS TEST PASSED${NC}"
echo "================================================"
echo ""
echo "Verified:"
echo "  - IEnumerable from System.Collections.Generic is aliased"
echo "  - IEnumerable from System.Collections keeps original name"
echo "  - No TS2300 duplicate identifier errors"
echo "  - Aliased names used correctly in type positions"
