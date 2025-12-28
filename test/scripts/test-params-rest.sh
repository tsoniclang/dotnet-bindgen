#!/bin/bash
# Test params array → rest parameter emission (TS1016 regression)
# Verifies that C# params T[] are emitted as TypeScript ...name: T[]

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Params Rest Parameter Test (TS1016)"
echo "================================================"
echo ""

# Use the nodejs package which has console methods with params
NODEJS_DIR="/home/jeswin/repos/tsoniclang/nodejs"

if [ ! -d "$NODEJS_DIR" ]; then
    echo -e "${RED}❌ FAILED: nodejs directory not found${NC}"
    exit 1
fi

echo "[1/3] Checking for rest parameter syntax in console methods..."

# Note: nodejs uses --namespace-map "nodejs=index" so path is index/internal/index.d.ts
INTERNAL_FILE="$NODEJS_DIR/index/internal/index.d.ts"

# Check console.assert has rest params
if ! grep -q "static assert.*\\.\\.\\.optionalParams:" "$INTERNAL_FILE"; then
    echo -e "${RED}❌ FAILED: console.assert should have ...optionalParams rest parameter${NC}"
    grep "static assert" "$INTERNAL_FILE"
    exit 1
fi
echo -e "${GREEN}✓ console.assert has ...optionalParams rest parameter${NC}"

# Check console.log has rest params
if ! grep -q "static log.*\\.\\.\\.optionalParams:" "$INTERNAL_FILE"; then
    echo -e "${RED}❌ FAILED: console.log should have ...optionalParams rest parameter${NC}"
    grep "static log" "$INTERNAL_FILE"
    exit 1
fi
echo -e "${GREEN}✓ console.log has ...optionalParams rest parameter${NC}"

echo ""
echo "[2/3] Verifying no 'required after optional' errors..."

# Run tsc and check for TS1016 errors in nodejs files
cd "$NODEJS_DIR"
TSC_OUTPUT=$(timeout 120 npx tsc 2>&1 || true)

# Check for TS1016 errors specifically in nodejs files
TS1016_ERRORS=$(echo "$TSC_OUTPUT" | grep -E "^nodejs.*TS1016" || true)
if [ -n "$TS1016_ERRORS" ]; then
    echo -e "${RED}❌ FAILED: TS1016 'required after optional' errors found${NC}"
    echo "$TS1016_ERRORS"
    exit 1
fi

echo -e "${GREEN}✓ No TS1016 'required after optional' errors${NC}"

echo ""
echo "[3/3] Verifying rest param syntax is correct..."

# Check that the rest param comes LAST (no regular params after ...)
# If we see a pattern like "...something: T[], regularParam:" that would be wrong
if grep -E "\\.\\.\\..*\\[\\], [a-zA-Z]+" "$INTERNAL_FILE" | grep -v "^//" > /dev/null; then
    echo -e "${RED}❌ FAILED: Rest parameter should be last in signature${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Rest parameters are always last in signature${NC}"

echo ""
echo "================================================"
echo -e "${GREEN}✓ PARAMS REST PARAMETER TEST PASSED${NC}"
echo "================================================"
echo ""
echo "Verified:"
echo "  - C# params T[] → TypeScript ...name: T[]"
echo "  - console.assert has ...optionalParams"
echo "  - console.log has ...optionalParams"
echo "  - No TS1016 'required after optional' errors"
