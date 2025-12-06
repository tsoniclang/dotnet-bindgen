#!/usr/bin/env bash
set -euo pipefail

# ================================================
# Facade Constraint Invariants Test
# ================================================
# Tests the three must-lock invariants from Alice's review:
# 1. No Internal.Internal.* double-qualification
# 2. No Internal.unknown/any/never (TS built-in leak)
# 3. No bigint carrier in constraints
# ================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

# Use cached BCL if available, otherwise use dotnet output
if [[ -d "$PROJECT_ROOT/.tests/bcl" ]]; then
    BCL_DIR="$PROJECT_ROOT/.tests/bcl"
elif [[ -d "$PROJECT_ROOT/../dotnet" ]]; then
    BCL_DIR="$PROJECT_ROOT/../dotnet"
else
    echo "ERROR: No BCL output found. Run generation first."
    exit 1
fi

echo "================================================"
echo "Facade Constraint Invariants Test"
echo "================================================"
echo ""
echo "Using BCL: $BCL_DIR"
echo ""

FAILED=0

# ------------------------------------------------
# Invariant 1: No Internal.Internal.* double-qualification
# ------------------------------------------------
echo "[1/3] Checking for Internal.Internal.* double-qualification..."

DOUBLE_QUAL=$(grep -rE "Internal\.Internal\." "$BCL_DIR"/*.d.ts "$BCL_DIR"/**/*.d.ts 2>/dev/null || true)
if [[ -n "$DOUBLE_QUAL" ]]; then
    echo -e "\033[0;31m❌ FAILED: Found Internal.Internal.* double-qualification:\033[0m"
    echo "$DOUBLE_QUAL" | head -10
    FAILED=1
else
    echo -e "\033[0;32m✓ No Internal.Internal.* double-qualification found\033[0m"
fi

# ------------------------------------------------
# Invariant 2: No Internal.unknown/any/never (TS built-in leak)
# ------------------------------------------------
echo ""
echo "[2/3] Checking for Internal.unknown/any/never..."

TS_BUILTIN_LEAK=$(grep -rE "\bInternal\.(unknown|any|never)\b" "$BCL_DIR"/*.d.ts "$BCL_DIR"/**/*.d.ts 2>/dev/null || true)
if [[ -n "$TS_BUILTIN_LEAK" ]]; then
    echo -e "\033[0;31m❌ FAILED: Found Internal.(unknown|any|never):\033[0m"
    echo "$TS_BUILTIN_LEAK" | head -10
    FAILED=1
else
    echo -e "\033[0;32m✓ No Internal.unknown/any/never leakage found\033[0m"
fi

# ------------------------------------------------
# Invariant 3: No bigint carrier in constraints or aliases
# ------------------------------------------------
echo ""
echo "[3/3] Checking for bigint carrier..."

# Check constraint positions (extends clauses)
BIGINT_CONSTRAINT=$(grep -rE "extends.*\bbigint\b" "$BCL_DIR"/*.d.ts "$BCL_DIR"/**/*.d.ts 2>/dev/null || true)
if [[ -n "$BIGINT_CONSTRAINT" ]]; then
    echo -e "\033[0;31m❌ FAILED: Found bigint in constraint extends clause:\033[0m"
    echo "$BIGINT_CONSTRAINT" | head -10
    FAILED=1
fi

# Check primitive alias definitions
BIGINT_ALIAS=$(grep -rE "^export type (Int64|UInt64|Int128|UInt128).*=.*bigint" "$BCL_DIR"/*.d.ts "$BCL_DIR"/**/*.d.ts 2>/dev/null || true)
if [[ -n "$BIGINT_ALIAS" ]]; then
    echo -e "\033[0;31m❌ FAILED: Found bigint in primitive alias definition:\033[0m"
    echo "$BIGINT_ALIAS" | head -10
    FAILED=1
fi

if [[ -z "$BIGINT_CONSTRAINT" && -z "$BIGINT_ALIAS" ]]; then
    echo -e "\033[0;32m✓ No bigint carrier found in constraints or aliases\033[0m"
fi

# ------------------------------------------------
# Summary
# ------------------------------------------------
echo ""
echo "================================================"
if [[ $FAILED -eq 0 ]]; then
    echo -e "\033[0;32m✓ ALL FACADE CONSTRAINT INVARIANTS VERIFIED\033[0m"
    echo "================================================"
    echo ""
    echo "Summary:"
    echo "  ✓ No Internal.Internal.* double-qualification"
    echo "  ✓ No Internal.unknown/any/never leakage"
    echo "  ✓ No bigint carrier in constraints or aliases"
else
    echo -e "\033[0;31m❌ FACADE CONSTRAINT INVARIANT VIOLATIONS DETECTED\033[0m"
    echo "================================================"
    exit 1
fi
