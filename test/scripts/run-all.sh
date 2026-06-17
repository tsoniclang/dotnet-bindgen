#!/bin/bash
# Run all dotnet-bindgen regression tests
# Usage: ./scripts/test/run-all.sh [--no-clean]
#
# This script:
# 1. Cleans all BCL caches (unless --no-clean)
# 2. Pre-generates BCL cache (default)
# 3. Runs all test scripts
# 4. Reports summary

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/_common.sh"

# Parse arguments
CLEAN_FIRST=true
for arg in "$@"; do
    case "$arg" in
        --no-clean)
            CLEAN_FIRST=false
            ;;
        --help|-h)
            echo "Usage: $0 [--no-clean]"
            echo ""
            echo "Options:"
            echo "  --no-clean    Skip cleaning BCL caches (faster for re-runs)"
            echo ""
            exit 0
            ;;
    esac
done

echo "========================================"
echo "dotnet-bindgen Regression Test Suite"
echo "========================================"
echo ""

# Step 1: Clean caches (optional)
if [ "$CLEAN_FIRST" = true ]; then
    echo "[1/3] Cleaning BCL caches..."
    clean_bcl_caches
    echo "      Done."
else
    echo "[1/3] Skipping cache clean (--no-clean)"
fi

# Step 2: Pre-generate BCL caches
echo ""
echo "[2/3] Pre-generating BCL caches..."
echo "      Generating default mode..."
BCL_PATH=$(ensure_bcl default) || exit 1
echo "      Generated: $BCL_PATH"

# Step 3: Run all tests
echo ""
echo "[3/3] Running test scripts..."
echo ""

# Collect test scripts (exclude _common.sh and run-all.sh)
TEST_SCRIPTS=()
for script in "$SCRIPT_DIR"/test-*.sh; do
    if [ -f "$script" ] && [ -x "$script" ]; then
        TEST_SCRIPTS+=("$script")
    fi
done

PASSED=0
FAILED=0
FAILED_SCRIPTS=()

for script in "${TEST_SCRIPTS[@]}"; do
    script_name=$(basename "$script")
    echo "----------------------------------------"
    echo "Running: $script_name"
    echo "----------------------------------------"

    if bash "$script"; then
        echo -e "${GREEN}PASSED${NC}: $script_name"
        PASSED=$((PASSED + 1))
    else
        echo -e "${RED}FAILED${NC}: $script_name"
        FAILED=$((FAILED + 1))
        FAILED_SCRIPTS+=("$script_name")
    fi
    echo ""
done

# Summary
echo "========================================"
echo "Test Summary"
echo "========================================"
echo ""
echo "Total:  $((PASSED + FAILED))"
echo -e "Passed: ${GREEN}$PASSED${NC}"
echo -e "Failed: ${RED}$FAILED${NC}"

if [ ${#FAILED_SCRIPTS[@]} -gt 0 ]; then
    echo ""
    echo "Failed tests:"
    for script in "${FAILED_SCRIPTS[@]}"; do
        echo "  - $script"
    done
    echo ""
    exit 1
else
    echo ""
    echo -e "${GREEN}All tests passed!${NC}"
    exit 0
fi
