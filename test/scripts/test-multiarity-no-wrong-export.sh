#!/bin/bash
# ======================================================
# Multi-Arity Family: No Wrong Export Regression Test
# ======================================================
# Verifies that multi-arity families are exported correctly:
# 1. NO "ValueTuple_1 as ValueTuple" exports (would break arity routing)
# 2. YES "export type ValueTuple<" conditional aliases
# 3. NO nested type pollution in family exports

set -e

echo "================================================"
echo "Multi-Arity No-Wrong-Export Regression Test"
echo "================================================"
echo

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
BCL_DIR="${PROJECT_ROOT}/.tests/bcl"

# Ensure BCL is generated
if [[ ! -d "$BCL_DIR" ]]; then
    echo "[ERROR] BCL directory not found: $BCL_DIR"
    echo "Run: bash test/scripts/capture-baseline.sh"
    exit 1
fi

ERRORS=0

echo "[1/3] Checking for wrong ValueTuple exports..."

# Check that NO facade exports ValueTuple_N as ValueTuple
WRONG_EXPORTS=$(grep -rn "ValueTuple_[0-9] as ValueTuple" "$BCL_DIR"/*.d.ts 2>/dev/null || true)
if [[ -n "$WRONG_EXPORTS" ]]; then
    echo "[FAIL] Found wrong ValueTuple exports (arity-N aliased to stem):"
    echo "$WRONG_EXPORTS"
    ERRORS=$((ERRORS + 1))
else
    echo "[PASS] No wrong ValueTuple_N as ValueTuple exports found"
fi

echo

echo "[2/3] Checking for conditional type aliases..."

# Check that System.d.ts has conditional type ValueTuple
if grep -q "^export type ValueTuple<" "$BCL_DIR/System.d.ts" 2>/dev/null; then
    echo "[PASS] System.d.ts contains 'export type ValueTuple<' conditional alias"
else
    echo "[FAIL] System.d.ts missing conditional type alias for ValueTuple"
    ERRORS=$((ERRORS + 1))
fi

# Check that System.d.ts has conditional type Action
if grep -q "^export type Action<" "$BCL_DIR/System.d.ts" 2>/dev/null; then
    echo "[PASS] System.d.ts contains 'export type Action<' conditional alias"
else
    echo "[FAIL] System.d.ts missing conditional type alias for Action"
    ERRORS=$((ERRORS + 1))
fi

# Check that System.d.ts has conditional type Func
if grep -q "^export type Func<" "$BCL_DIR/System.d.ts" 2>/dev/null; then
    echo "[PASS] System.d.ts contains 'export type Func<' conditional alias"
else
    echo "[FAIL] System.d.ts missing conditional type alias for Func"
    ERRORS=$((ERRORS + 1))
fi

echo

echo "[3/3] Checking for nested type pollution..."

# Check that FrozenDictionary family doesn't include Enumerator
# The facade should NOT have FrozenDictionary_2$Enumerator in its conditional ladder
NESTED_POLLUTION=$(grep -n "FrozenDictionary.*Enumerator" "$BCL_DIR/System.Collections.Frozen.d.ts" 2>/dev/null | grep -v "^export { FrozenDictionary" || true)
if [[ -n "$NESTED_POLLUTION" ]]; then
    echo "[FAIL] FrozenDictionary family includes nested types:"
    echo "$NESTED_POLLUTION"
    ERRORS=$((ERRORS + 1))
else
    echo "[PASS] FrozenDictionary family excludes nested types"
fi

echo
echo "================================================"

if [[ $ERRORS -gt 0 ]]; then
    echo "[FAILED] $ERRORS error(s) found"
    exit 1
else
    echo "[PASSED] All multi-arity export checks passed"
    exit 0
fi
