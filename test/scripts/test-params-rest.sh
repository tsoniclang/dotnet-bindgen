#!/bin/bash
# Test params array → rest parameter emission (TS1016 regression)
# Verifies that C# params T[] are emitted as TypeScript ...name: T[]

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Params Rest Parameter Test (TS1016)"
echo "================================================"
echo ""

echo "[1/3] Checking for rest parameter syntax in generated BCL..."

BCL_DIR=$(ensure_bcl default)
prepare_local_core_dependency "$BCL_DIR"
INTERNAL_FILE="$BCL_DIR/System/internal/index.d.ts"

if ! grep -q "ExecuteAssemblyByName(assemblyName: string, ...args: (string | null)\\[\\]): int;" "$INTERNAL_FILE"; then
    echo -e "${RED}❌ FAILED: ExecuteAssemblyByName should have a rest parameter with array element nullability only${NC}"
    grep "ExecuteAssemblyByName(assemblyName: string" "$INTERNAL_FILE"
    exit 1
fi
echo -e "${GREEN}✓ ExecuteAssemblyByName has valid rest parameter syntax${NC}"

if ! grep -q "Combine(...delegates: (Function | null)\\[\\]): Function | null;" "$INTERNAL_FILE"; then
    echo -e "${RED}❌ FAILED: Delegate.Combine should have a valid rest parameter${NC}"
    grep "Combine(...delegates" "$INTERNAL_FILE"
    exit 1
fi
echo -e "${GREEN}✓ Delegate.Combine has valid rest parameter syntax${NC}"

echo ""
echo "[2/3] Verifying no 'required after optional' errors..."

# Run tsc and check for rest-parameter syntax errors in the generated BCL
cd "$BCL_DIR"
tsc_path=$(get_tsc)
if [ -z "$tsc_path" ]; then
    echo -e "${RED}ERROR: TypeScript not installed. Run 'npm install' first.${NC}" >&2
    exit 1
fi

echo '{ "compilerOptions": { "strict": true, "noEmit": true, "skipLibCheck": false, "moduleResolution": "bundler", "target": "ES2020", "module": "ES2020" }, "include": ["**/*.d.ts"] }' > tsconfig.json
TSC_OUTPUT=$(timeout 120 "$tsc_path" --noEmit 2>&1 || true)

# Check for rest-parameter syntax errors across the generated BCL
REST_ERRORS=$(echo "$TSC_OUTPUT" | grep -E "error TS1016:|error TS2370:" || true)
if [ -n "$REST_ERRORS" ]; then
    echo -e "${RED}❌ FAILED: Rest parameter syntax errors found${NC}"
    echo "$REST_ERRORS"
    exit 1
fi

echo -e "${GREEN}✓ No TS1016/TS2370 rest parameter errors${NC}"

echo ""
echo "[3/3] Verifying rest param syntax is correct..."

# Check that no rest parameter retains a top-level nullable array type.
if grep -E "\\.\\.\\.[^:]+: .*\\[\\] \\| null" "$INTERNAL_FILE" | grep -v "^//" > /dev/null; then
    echo -e "${RED}❌ FAILED: Rest parameter should not carry a top-level nullable array type${NC}"
    grep -E "\\.\\.\\.[^:]+: .*\\[\\] \\| null" "$INTERNAL_FILE" | head -10
    exit 1
fi

echo -e "${GREEN}✓ Rest parameters do not carry top-level nullable array syntax${NC}"

echo ""
echo "================================================"
echo -e "${GREEN}✓ PARAMS REST PARAMETER TEST PASSED${NC}"
echo "================================================"
echo ""
echo "Verified:"
echo "  - C# params T[] → TypeScript ...name: T[]"
echo "  - Representative BCL params methods emit rest syntax"
echo "  - No TS1016 / TS2370 rest parameter errors"
echo "  - Top-level nullable array carriers are not emitted on rest parameters"
