#!/bin/bash
# Regression test for strict mode - ensures zero errors, zero warnings, disciplined INFO codes, and surface stability

set -e  # Exit on error

echo "================================================"
echo "Strict Mode Regression Test"
echo "================================================"
echo ""

# Run strict mode validation
echo "[1/4] Running strict mode validation..."
output=$(dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
    generate -d ~/dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.1.25451.107 \
    -o .tests/strict-test --strict --logs PhaseGate 2>&1)

# Check exit code
exit_code=$?

if [ $exit_code -ne 0 ]; then
    echo "❌ FAILED: Strict mode validation failed"
    echo ""
    echo "Output:"
    echo "$output"
    exit 1
fi

echo "✓ Strict mode validation passed"
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
    echo "❌ FAILED: Expected 0 errors, got $errors"
    exit 1
fi

echo "✓ Zero errors verified"

# Verify warning count is zero (strict mode zero tolerance)
if [ "$warnings" != "0" ]; then
    echo "❌ FAILED: Expected 0 warnings (strict mode zero tolerance), got $warnings"
    exit 1
fi

echo "✓ Zero warnings verified (strict mode zero tolerance)"
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
    echo "❌ FAILED: INFO diagnostic codes don't match expected set"
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

echo "✓ INFO diagnostic codes match expected set"
echo ""

# Verify surface manifest
echo "[4/4] Verifying API surface stability..."

# Run surface manifest verification
bash scripts/test-surface-manifest.sh > /dev/null 2>&1

if [ $? -eq 0 ]; then
    echo "✓ Surface matches baseline (no drift detected)"
else
    echo "❌ FAILED: Surface manifest verification failed"
    echo ""
    echo "The emitted TypeScript API surface has changed."
    echo "Run: bash scripts/test-surface-manifest.sh"
    echo ""
    echo "For details and remediation steps."
    exit 1
fi

echo ""

echo "================================================"
echo "✓ ALL TESTS PASSED"
echo "================================================"
echo ""
echo "Summary:"
echo "  - Strict mode validation passes"
echo "  - Zero errors (ERROR level diagnostics)"
echo "  - Zero warnings (strict mode zero tolerance achieved)"
echo "  - INFO codes disciplined (only TBG120, TBG310, TBG410 allowed)"
echo "  - Surface stable (matches baseline manifest)"
echo ""
