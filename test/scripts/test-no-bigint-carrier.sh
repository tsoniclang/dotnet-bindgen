#!/bin/bash
# Test: No bigint carrier in emitted constraints
# Verifies that constraint relaxation uses only { number, string, boolean } per @tsonic/core contract.
# This prevents contract drift between dotnet-bindgen and @tsonic/core.

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "Running no-bigint carrier regression test..."

# Use cached BCL output
BCL_DIR=$(ensure_bcl default)

# Check for bigint in constraint text (extends clauses)
echo "  Checking for bigint in constraint text..."

# Pattern: "extends (...bigint...)" in internal index files (where constraints are emitted)
# Note: grep returns exit code 1 when no matches found, which is success for us
BIGINT_MATCHES=$(grep -rh "extends.*bigint" "$BCL_DIR"/*/internal/index.d.ts 2>/dev/null || true)

if [ -n "$BIGINT_MATCHES" ]; then
    echo -e "${RED}FAILED: Found bigint in constraint text${NC}"
    echo ""
    echo "The following contain 'bigint' in extends clauses:"
    echo "$BIGINT_MATCHES" | head -5
    echo ""
    echo "This violates the @tsonic/core contract which specifies:"
    echo "  - ALL numeric primitives are number-carried (including long, nint, nuint)"
    echo "  - Constraint relaxation must use only: number | string | boolean"
    echo ""
    echo "Fix: Update PrimitiveLift.Rules to use 'number' carrier for all numeric types."
    exit 1
fi

# Also check primitive alias definitions don't use bigint
echo "  Checking primitive alias definitions..."
ALIAS_BIGINT=$(grep -rh "type.*=.*bigint" "$BCL_DIR"/*/internal/index.d.ts 2>/dev/null || true)

if [ -n "$ALIAS_BIGINT" ]; then
    echo -e "${RED}FAILED: Found bigint in primitive alias definitions${NC}"
    echo ""
    echo "The following contain 'bigint' in type aliases:"
    echo "$ALIAS_BIGINT" | head -5
    echo ""
    echo "Per @tsonic/core, all numeric primitives should be number-carried."
    exit 1
fi

echo -e "${GREEN}No-bigint carrier test passed!${NC}"
echo ""
echo "Verified:"
echo "  - No 'bigint' in constraint extends clauses"
echo "  - No 'bigint' in primitive alias definitions"
echo "  - Constraint relaxation uses only: number | string | boolean"
exit 0
