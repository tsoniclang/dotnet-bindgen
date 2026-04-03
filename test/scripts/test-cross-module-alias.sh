#!/bin/bash
# Test cross-module aliasing for duplicate type names (TS2300 regression)
# Verifies that types with the same name from different modules get aliased deterministically

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Cross-Module Alias Test (TS2300)"
echo "================================================"
echo ""

echo "[1/3] Checking for cross-module aliasing in generated BCL..."

BCL_DIR=$(ensure_bcl default)
prepare_local_core_dependency "$BCL_DIR"

FACADE_FILE="$BCL_DIR/System.Reflection.d.ts"
INTERNAL_FILE="$BCL_DIR/System.Reflection/internal/index.d.ts"

if ! grep -q "AssemblyHashAlgorithm as AssemblyHashAlgorithm_Assemblies" "$FACADE_FILE"; then
    echo -e "${RED}❌ FAILED: Missing cross-module alias import in $FACADE_FILE${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Facade uses deterministic cross-module aliasing${NC}"

if ! grep -q "AssemblyHashAlgorithm as AssemblyHashAlgorithm_Assemblies" "$INTERNAL_FILE"; then
    echo -e "${RED}❌ FAILED: Internal file should use deterministic cross-module aliasing when a local type collides${NC}"
    exit 1
fi

if ! grep -q "hashAlgorithm: AssemblyHashAlgorithm_Assemblies" "$INTERNAL_FILE"; then
    echo -e "${RED}❌ FAILED: Internal file is not using the aliased import at the call site${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Internal file uses deterministic internal aliasing for local collisions${NC}"

echo ""
echo "[2/3] Verifying no duplicate identifier errors..."

# Run tsc with timeout and check for TS2300 errors in the generated BCL
cd "$BCL_DIR"
tsc_path=$(get_tsc)
if [ -z "$tsc_path" ]; then
    echo -e "${RED}ERROR: TypeScript not installed. Run 'npm install' first.${NC}" >&2
    exit 1
fi

echo '{ "compilerOptions": { "strict": true, "noEmit": true, "skipLibCheck": false, "moduleResolution": "bundler", "target": "ES2020", "module": "ES2020" }, "include": ["**/*.d.ts"] }' > tsconfig.json
TSC_OUTPUT=$(timeout 120 "$tsc_path" --noEmit 2>&1 || true)

# Check for TS2300 errors in the generated BCL
TS2300_ERRORS=$(echo "$TSC_OUTPUT" | grep -E "error TS2300:" || true)
if [ -n "$TS2300_ERRORS" ]; then
    echo -e "${RED}❌ FAILED: TS2300 duplicate identifier errors found${NC}"
    echo "$TS2300_ERRORS"
    exit 1
fi

echo -e "${GREEN}✓ No TS2300 duplicate identifier errors${NC}"

echo ""
echo "[3/3] Verifying disambiguated names are used correctly..."

# Facade should contain the disambiguating alias import and still expose the local symbol name.
if ! grep -q "AssemblyHashAlgorithm as AssemblyHashAlgorithm_Assemblies" "$FACADE_FILE"; then
    echo -e "${RED}❌ FAILED: Missing cross-module alias import in facade${NC}"
    exit 1
fi

if ! grep -q "export { AssemblyHashAlgorithm as AssemblyHashAlgorithm }" "$FACADE_FILE"; then
    echo -e "${RED}❌ FAILED: Missing local export for System.Reflection.AssemblyHashAlgorithm${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Disambiguation present and local export remains stable${NC}"

echo ""
echo "================================================"
echo -e "${GREEN}✓ CROSS-MODULE ALIAS TEST PASSED${NC}"
echo "================================================"
echo ""
echo "Verified:"
echo "  - Cross-module duplicate names are deterministically aliased in facades"
echo "  - Internal files also alias deterministically when a local type collides"
echo "  - No TS2300 duplicate identifier errors"
echo "  - Public export names remain stable after aliasing"
