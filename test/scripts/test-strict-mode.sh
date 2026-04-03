#!/bin/bash
# Regression test for strict mode - ensures zero errors, zero warnings,
# disciplined INFO codes, and surface stability

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Strict Mode Regression Test"
echo "================================================"
echo ""

# Initialize runtime
init_runtime

# Use strict test output directory
STRICT_DIR="$TESTS_DIR/strict-test"

# Run strict mode validation
echo "[1/4] Running strict mode validation..."
rm -rf "$STRICT_DIR"
mkdir -p "$STRICT_DIR"

output=$(dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -d "$DOTNET_RUNTIME" \
    -o "$STRICT_DIR" --strict --logs PhaseGate 2>&1)

exit_code=$?

if [ $exit_code -ne 0 ]; then
    echo -e "${RED}❌ FAILED: Strict mode validation failed${NC}"
    echo ""
    echo "Output:"
    echo "$output"
    exit 1
fi

echo -e "${GREEN}✓ Strict mode validation passed${NC}"
echo ""

# Check validation output
echo "[2/4] Checking diagnostic counts..."

# Extract counts from output
errors=$(echo "$output" | grep -o "Validation complete - [0-9]* errors" | grep -o "[0-9]*" || echo "0")
warnings=$(echo "$output" | grep -o "Validation complete - [0-9]* errors, [0-9]* warnings" | grep -o "[0-9]*" | tail -1 || echo "unknown")
info_count=$(echo "$output" | grep -o "[0-9]* info" | grep -o "[0-9]*" || echo "unknown")

echo "  Errors:   $errors"
echo "  Warnings: $warnings"
echo "  Info:     $info_count"
echo ""

# Verify error count is zero
if [ "$errors" != "0" ]; then
    echo -e "${RED}❌ FAILED: Expected 0 errors, got $errors${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Zero errors verified${NC}"

# Verify warning count is zero (strict mode zero tolerance)
if [ "$warnings" != "0" ]; then
    echo -e "${RED}❌ FAILED: Expected 0 warnings (strict mode zero tolerance), got $warnings${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Zero warnings verified (strict mode zero tolerance)${NC}"
echo ""

# Check INFO code discipline
echo "[3/4] Validating INFO diagnostic codes..."

# Expected INFO codes (these are the only allowed codes)
# TBG120: Reserved word collisions (8 instances - core BCL types in qualified contexts)
# TBG310: Property covariance (TypeScript language limitation)
# TBG410: Narrowed generic constraints (valid TypeScript pattern)
expected_codes="TBG120 TBG310 TBG410"

# Extract actual INFO codes from diagnostic summary
actual_codes=$(echo "$output" | grep -A 10 "Diagnostic Summary by Code:" | \
    grep "TBG[0-9]*:" | \
    sed 's/.*\(TBG[0-9]*\).*/\1/' | \
    sort -u | \
    tr '\n' ' ' | \
    sed 's/ $//')

echo "  Expected INFO codes: $expected_codes"
echo "  Actual INFO codes:   $actual_codes"
echo ""

# Compare expected vs actual
if [ "$actual_codes" != "$expected_codes" ]; then
    echo -e "${RED}❌ FAILED: INFO diagnostic codes don't match expected set${NC}"
    echo ""
    echo "This indicates either:"
    echo "  - A new INFO code was introduced (requires review)"
    echo "  - An expected INFO code is missing (BCL change or regression)"
    echo ""
    echo "Expected: $expected_codes"
    echo "Actual:   $actual_codes"
    echo ""
    echo "Diagnostic Summary:"
    echo "$output" | grep -A 10 "Diagnostic Summary by Code:"
    exit 1
fi

echo -e "${GREEN}✓ INFO diagnostic codes match expected set${NC}"
echo ""

# Verify surface manifest using the output we just generated
echo "[4/4] Verifying API surface stability..."

# Set environment variable for surface-manifest test to use our output
export SURFACE_VERIFY_DIR="$STRICT_DIR"
if bash "$SCRIPT_DIR/test-surface-manifest.sh" > /dev/null 2>&1; then
    echo -e "${GREEN}✓ Surface matches baseline (no drift detected)${NC}"
else
    echo -e "${RED}❌ FAILED: Surface manifest verification failed${NC}"
    echo ""
    echo "The emitted TypeScript API surface has changed."
    echo "Run: bash test/scripts/test-surface-manifest.sh"
    echo ""
    echo "For details and remediation steps."
    exit 1
fi

echo ""

echo "================================================"
echo -e "${GREEN}✓ ALL TESTS PASSED${NC}"
echo "================================================"
echo ""
echo "Summary:"
echo "  - Strict mode validation passes"
echo "  - Zero errors (ERROR level diagnostics)"
echo "  - Zero warnings (strict mode zero tolerance achieved)"
echo "  - INFO codes disciplined (only TBG120, TBG310, TBG410 allowed)"
echo "  - Surface stable (matches baseline manifest)"
echo ""
