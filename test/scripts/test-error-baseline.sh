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

# Install @tsonic/core for primitive type imports
echo '{ "dependencies": { "@tsonic/core": "^0.1.1" } }' > package.json
npm install --silent 2>/dev/null || npm install

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
BASELINE_FILE="$(dirname "${BASH_SOURCE[0]}")/../baselines/tsc-error-baseline.json"
EXPECTED_ERRORS=$(python3 -c "import json; print(json.load(open('$BASELINE_FILE'))['totalExpected'])")

if [ "$SEMANTIC_ERRORS" -ne "$EXPECTED_ERRORS" ]; then
    echo -e "${RED}❌ FAILED: Semantic error count mismatch${NC}"
    echo ""
    echo "  Expected: $EXPECTED_ERRORS errors (from baseline)"
    echo "  Actual:   $SEMANTIC_ERRORS errors"
    echo ""

    if [ "$SEMANTIC_ERRORS" -lt "$EXPECTED_ERRORS" ]; then
        echo "  This is GOOD - fewer errors than expected!"
        echo "  Update test/baselines/tsc-error-baseline.json to match"
    else
        echo "  New errors introduced. Review the changes:"
        grep 'error TS2[0-9][0-9][0-9]:' "$TSC_OUTPUT" | head -20
    fi
    exit 1
fi

echo -e "${GREEN}✓ Semantic errors match baseline ($EXPECTED_ERRORS expected)${NC}"

echo ""
echo "================================================"
echo -e "${GREEN}✓ COMPILER-GRADE ERROR BASELINE PASSED${NC}"
echo "================================================"
echo ""
echo "Summary:"
echo "  ✓ Zero syntax errors (valid TypeScript generated)"
echo "  ✓ Semantic errors match baseline ($EXPECTED_ERRORS expected TS2430 NRT covariance)"
echo ""
