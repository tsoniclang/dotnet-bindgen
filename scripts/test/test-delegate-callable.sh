#!/bin/bash
# Test: Delegates must be callable function types, not class-shaped
# This prevents regression to the broken delegate pattern that blocks LINQ.

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "Testing delegate emission format..."

# Use cached BCL output
BCL_DIR=$(ensure_bcl default)
INDEX_FILE="$BCL_DIR/System/internal/index.d.ts"

if [ ! -f "$INDEX_FILE" ]; then
    echo -e "${RED}FAIL: Generated index.d.ts not found at $INDEX_FILE${NC}"
    exit 1
fi

FAILED=0

# Test 1: No class-shaped Func_* or Action_*
echo -n "  Test 1: No class-shaped delegates... "
CLASS_DELEGATES=$(grep -E "^export class (Func_|Action_)" "$INDEX_FILE" 2>/dev/null || true)
if [ -n "$CLASS_DELEGATES" ]; then
    echo -e "${RED}FAIL${NC}"
    echo "    Found class-shaped delegates (should be callable types):"
    echo "$CLASS_DELEGATES" | head -5 | sed 's/^/      /'
    FAILED=1
else
    echo -e "${GREEN}PASS${NC}"
fi

# Test 2: Func_2 is a callable function type
echo -n "  Test 2: Func_2 is callable type... "
FUNC2_TYPE=$(grep "^export type Func_2<" "$INDEX_FILE" 2>/dev/null || true)
if echo "$FUNC2_TYPE" | grep -q "= ("; then
    echo -e "${GREEN}PASS${NC}"
    echo "    $FUNC2_TYPE" | head -1 | sed 's/^/      /'
else
    echo -e "${RED}FAIL${NC}"
    echo "    Expected callable function type, got:"
    echo "      $FUNC2_TYPE"
    FAILED=1
fi

# Test 3: Action_1 is a callable function type
echo -n "  Test 3: Action_1 is callable type... "
ACTION1_TYPE=$(grep "^export type Action_1<" "$INDEX_FILE" 2>/dev/null || true)
if echo "$ACTION1_TYPE" | grep -q "= ("; then
    echo -e "${GREEN}PASS${NC}"
    echo "    $ACTION1_TYPE" | head -1 | sed 's/^/      /'
else
    echo -e "${RED}FAIL${NC}"
    echo "    Expected callable function type, got:"
    echo "      $ACTION1_TYPE"
    FAILED=1
fi

# Test 4: System.Delegate is NOT a callable type (it's a class)
echo -n "  Test 4: System.Delegate is class (not callable)... "
DELEGATE_TYPE=$(grep "^export type Delegate = " "$INDEX_FILE" 2>/dev/null || true)
if echo "$DELEGATE_TYPE" | grep -q "\$instance"; then
    echo -e "${GREEN}PASS${NC}"
    echo "    $DELEGATE_TYPE" | head -1 | sed 's/^/      /'
else
    echo -e "${RED}FAIL${NC}"
    echo "    System.Delegate should be emitted as class pattern, got:"
    echo "      $DELEGATE_TYPE"
    FAILED=1
fi

# Test 5: System.MulticastDelegate is NOT a callable type (it's a class)
echo -n "  Test 5: System.MulticastDelegate is class (not callable)... "
MCAST_TYPE=$(grep "^export type MulticastDelegate = " "$INDEX_FILE" 2>/dev/null || true)
if echo "$MCAST_TYPE" | grep -q "\$instance"; then
    echo -e "${GREEN}PASS${NC}"
    echo "    $MCAST_TYPE" | head -1 | sed 's/^/      /'
else
    echo -e "${RED}FAIL${NC}"
    echo "    System.MulticastDelegate should be emitted as class pattern, got:"
    echo "      $MCAST_TYPE"
    FAILED=1
fi

echo ""
if [ $FAILED -eq 0 ]; then
    echo -e "${GREEN}All delegate tests passed!${NC}"
    exit 0
else
    echo -e "${RED}Some delegate tests failed!${NC}"
    exit 1
fi
