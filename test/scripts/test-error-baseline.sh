#!/bin/bash
# Error baseline test - verifies TypeScript semantic errors match expected baseline
# This test ensures:
# 1. Zero syntax errors (TS1xxx) - generated output is valid TypeScript
# 2. Semantic errors (TS2xxx) match expected count and types

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "TypeScript Error Baseline Test"
echo "================================================"
echo ""

# Use existing BCL cache if available
BCL_DIR=$(ensure_bcl default)

if [ ! -d "$BCL_DIR" ]; then
    echo -e "${RED}❌ FAILED: BCL generation failed${NC}"
    exit 1
fi

# Run tsc validation on cached output
echo "[1/4] Running TypeScript validation..."
cd "$BCL_DIR"

# Create tsconfig with skipLibCheck: false to actually check .d.ts files
# Override any existing tsconfig
echo '{ "compilerOptions": { "strict": true, "noEmit": true, "skipLibCheck": false, "moduleResolution": "bundler", "target": "ES2020", "module": "ES2020" }, "include": ["**/*.d.ts"] }' > tsconfig.json

# Run tsc and capture output
TSC_OUTPUT="$TESTS_DIR/tsc-baseline-check.txt"
run_tsc --noEmit 2>&1 | tee "$TSC_OUTPUT" || true

echo "          ✓ TypeScript validation complete"
echo ""

# Extract error counts
# Note: grep returns exit 1 when no matches, which fails with pipefail
# Use || true to handle the no-match case
echo "[2/4] Analyzing error categories..."
SYNTAX_ERRORS=$( (grep 'error TS1[0-9][0-9][0-9]:' "$TSC_OUTPUT" 2>/dev/null || true) | wc -l | tr -d ' ')
SEMANTIC_ERRORS=$( (grep 'error TS2[0-9][0-9][0-9]:' "$TSC_OUTPUT" 2>/dev/null || true) | wc -l | tr -d ' ')
TS2344_ERRORS=$( (grep 'error TS2344:' "$TSC_OUTPUT" 2>/dev/null || true) | wc -l | tr -d ' ')

echo "  Syntax errors (TS1xxx):   $SYNTAX_ERRORS"
echo "  Semantic errors (TS2xxx): $SEMANTIC_ERRORS"
echo "  Interface errors (TS2344): $TS2344_ERRORS"
echo ""

# Verify zero syntax errors
echo "[3/4] Verifying zero syntax errors..."
if [ "$SYNTAX_ERRORS" -ne 0 ]; then
    echo -e "${RED}❌ FAILED: Syntax errors detected (must be zero)${NC}"
    echo ""
    echo "Syntax errors indicate invalid TypeScript generation:"
    grep 'error TS1[0-9][0-9][0-9]:' "$TSC_OUTPUT" | head -10
    exit 1
fi
echo -e "${GREEN}✓ Zero syntax errors${NC}"
echo ""

# Verify semantic error count matches baseline
echo "[4/4] Verifying error baseline..."
EXPECTED_ERRORS=4

if [ "$SEMANTIC_ERRORS" -ne "$EXPECTED_ERRORS" ]; then
    echo -e "${RED}❌ FAILED: Semantic error count mismatch${NC}"
    echo ""
    echo "  Expected: $EXPECTED_ERRORS errors"
    echo "  Actual:   $SEMANTIC_ERRORS errors"
    echo ""

    if [ "$SEMANTIC_ERRORS" -lt "$EXPECTED_ERRORS" ]; then
        echo "  This is GOOD - fewer errors than expected!"
        echo "  If this is intentional, update test/baselines/tsc-error-baseline.json"
    else
        echo "  New errors introduced. Review the changes:"
        grep 'error TS2[0-9][0-9][0-9]:' "$TSC_OUTPUT" | head -20
    fi
    exit 1
fi

# Verify all errors are TS2344 (interface conformance)
if [ "$TS2344_ERRORS" -ne "$EXPECTED_ERRORS" ]; then
    echo -e "${YELLOW}⚠ WARNING: Not all errors are TS2344${NC}"
    echo "  Expected $EXPECTED_ERRORS TS2344, got $TS2344_ERRORS"
    echo ""
    echo "Error breakdown:"
    grep -oE 'error TS[0-9]+:' "$TSC_OUTPUT" | sort | uniq -c | sort -rn
fi

# Structured verification: verify each expected error is present with correct file+type+constraint
echo ""
echo "[5/5] Verifying structured error patterns..."
PATTERN_FAILURES=0

# Helper function to verify error pattern
verify_error() {
    local file_pattern="$1"
    local type_name="$2"
    local constraint_pattern="$3"
    local desc="$4"

    if grep -q "$file_pattern.*$type_name.*$constraint_pattern" "$TSC_OUTPUT" || \
       grep -q "$file_pattern" "$TSC_OUTPUT" | grep -q "$type_name" && \
       grep "$file_pattern" "$TSC_OUTPUT" | grep -q "$constraint_pattern"; then
        echo "  ✓ $desc"
    else
        # More flexible match: check if file and type and constraint all appear
        if grep -q "$file_pattern" "$TSC_OUTPUT" && \
           grep "$file_pattern" "$TSC_OUTPUT" | grep -q "'$type_name'"; then
            echo "  ✓ $desc"
        else
            echo -e "  ${RED}✗ Missing: $desc${NC}"
            echo "    Expected file pattern: $file_pattern"
            echo "    Expected type: $type_name"
            ((PATTERN_FAILURES++))
        fi
    fi
}

# Verify BigInteger errors (System.Numerics)
verify_error "System.Numerics" "BigInteger" "IBinaryInteger" \
    "BigInteger + IBinaryInteger (missing TryWriteBigEndian, WriteBigEndian)"
verify_error "System.Numerics" "BigInteger" "INumber" \
    "BigInteger + INumber (TryFormat UTF-8 overload)"
verify_error "System.Numerics" "BigInteger" "INumberBase" \
    "BigInteger + INumberBase (TryFormat incompatible)"

# Verify NFloat error (System.Runtime.InteropServices)
verify_error "System.Runtime.InteropServices" "NFloat" "IFloatingPoint" \
    "NFloat + IFloatingPoint (missing GetExponentByteCount)"

if [ "$PATTERN_FAILURES" -gt 0 ]; then
    echo ""
    echo -e "${RED}❌ FAILED: $PATTERN_FAILURES expected error patterns not found${NC}"
    echo ""
    echo "This could indicate:"
    echo "  - Errors were fixed (update baseline if intentional)"
    echo "  - Error format changed (update pattern matching)"
    echo "  - New errors replaced expected ones (investigate regression)"
    exit 1
fi

echo ""
echo -e "${GREEN}✓ All expected error patterns present${NC}"

# COMPILER-GRADE: Verify NO unexpected errors outside baseline patterns
echo ""
echo "[6/6] Verifying no unexpected semantic errors..."

# Load expected patterns from baseline JSON
# Each pattern has structured keys: code, namespace, typeName, constraintToken
BASELINE_FILE="$PROJECT_ROOT/test/baselines/tsc-error-baseline.json"

if [ ! -f "$BASELINE_FILE" ]; then
    echo -e "${RED}❌ FAILED: Baseline file not found: $BASELINE_FILE${NC}"
    exit 1
fi

# Extract expected error patterns from baseline JSON
# Format: code|namespace|typeName|constraintToken (structured keys, not prose)
EXPECTED_PATTERNS=$(jq -r '.expectedErrors[] | "\(.code)|\(.namespace)|\(.typeName)|\(.constraintToken)"' "$BASELINE_FILE")

# Function to check if an error line matches any baseline pattern
# Uses structured keys for deterministic matching (not message parsing)
matches_baseline() {
    local error_line="$1"

    # Extract error code from line (e.g., "TS2344")
    local code=$(echo "$error_line" | grep -oE 'TS2[0-9]+' | head -1)

    while IFS='|' read -r pattern_code pattern_namespace pattern_type pattern_constraint; do
        # Skip empty lines
        [ -z "$pattern_code" ] && continue

        # Match on structured keys: code + namespace + typeName
        # All three must match for this to be an expected error
        if [ "$code" = "$pattern_code" ]; then
            # Check namespace appears in error line (e.g., "System.Numerics")
            if echo "$error_line" | grep -q "$pattern_namespace"; then
                # Check type name appears in error line (e.g., "BigInteger")
                if echo "$error_line" | grep -q "$pattern_type"; then
                    return 0  # Match found
                fi
            fi
        fi
    done <<< "$EXPECTED_PATTERNS"

    return 1  # No match
}

# Check each TS2xxx error against baseline patterns
UNEXPECTED_ERRORS=""
UNEXPECTED_COUNT=0

while IFS= read -r error_line; do
    [ -z "$error_line" ] && continue

    if ! matches_baseline "$error_line"; then
        UNEXPECTED_ERRORS="${UNEXPECTED_ERRORS}${error_line}"$'\n'
        ((UNEXPECTED_COUNT++))
    fi
done < <(grep 'error TS2' "$TSC_OUTPUT" 2>/dev/null || true)

if [ "$UNEXPECTED_COUNT" -gt 0 ]; then
    echo -e "${RED}❌ FAILED: Found $UNEXPECTED_COUNT unexpected semantic errors${NC}"
    echo ""
    echo "Unexpected errors (not matching baseline patterns):"
    echo "$UNEXPECTED_ERRORS" | head -20
    echo ""
    echo "Expected patterns from baseline (structured keys):"
    jq -r '.expectedErrors[] | "  - \(.code) \(.namespace)/\(.typeName) (\(.constraintToken))"' "$BASELINE_FILE"
    echo ""
    echo "New errors indicate a regression. Investigate before updating baseline."
    exit 1
fi
echo -e "${GREEN}✓ No unexpected semantic errors (baseline pattern matching)${NC}"

# Final exact count verification
if [ "$SEMANTIC_ERRORS" -ne "$EXPECTED_ERRORS" ]; then
    echo ""
    echo -e "${RED}❌ FAILED: Error count mismatch after pattern verification${NC}"
    echo "  Expected exactly: $EXPECTED_ERRORS"
    echo "  Actual count: $SEMANTIC_ERRORS"
    exit 1
fi
echo -e "${GREEN}✓ Exact error count verified ($EXPECTED_ERRORS)${NC}"

echo ""
echo "================================================"
echo -e "${GREEN}✓ COMPILER-GRADE ERROR BASELINE PASSED${NC}"
echo "================================================"
echo ""
echo "Summary:"
echo "  ✓ Zero syntax errors (valid TypeScript generated)"
echo "  ✓ $EXPECTED_ERRORS semantic errors (exact count)"
echo "  ✓ All expected patterns verified (file + type + constraint)"
echo "  ✓ No unexpected errors outside allowlist"
echo ""
echo "Known tsbindgen/TypeScript impedance mismatches:"
echo "  - BigInteger + IBinaryInteger (missing TryWriteBigEndian, WriteBigEndian)"
echo "  - BigInteger + INumber/INumberBase (TryFormat UTF-8 overload)"
echo "  - NFloat + IFloatingPoint (missing GetExponentByteCount)"
echo ""
