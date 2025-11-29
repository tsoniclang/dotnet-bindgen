#!/bin/bash
# Regression test for honest interface implementation
# Ensures interfaces moved to views are NOT in implements clause

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
VALIDATE_DIR="$PROJECT_DIR/.tests/validate"

echo "=== Honest Implements Regression Test ==="
echo ""

# Check that validation output exists
if [ ! -d "$VALIDATE_DIR" ]; then
    echo "ERROR: Validation output not found. Run 'node scripts/validate.js' first."
    exit 1
fi

ERRORS=0

# Test 1: BlockingCollection should NOT implement IEnumerable (members in views)
echo "Test 1: BlockingCollection should not implement IEnumerable"
if grep -q "class BlockingCollection_1\$instance.*implements.*IEnumerable" "$VALIDATE_DIR/System.Collections.Concurrent/internal/index.d.ts" 2>/dev/null; then
    echo "  FAIL: BlockingCollection_1\$instance still implements IEnumerable"
    ERRORS=$((ERRORS + 1))
else
    echo "  PASS: BlockingCollection_1\$instance does not implement IEnumerable"
fi

# Test 2: List should NOT implement IEnumerable (members in views)
echo "Test 2: List should not implement IEnumerable"
if grep -q "class List_1\$instance.*implements.*IEnumerable" "$VALIDATE_DIR/System.Collections.Generic/internal/index.d.ts" 2>/dev/null; then
    echo "  FAIL: List_1\$instance still implements IEnumerable"
    ERRORS=$((ERRORS + 1))
else
    echo "  PASS: List_1\$instance does not implement IEnumerable"
fi

# Test 3: Enumerators SHOULD still implement IDisposable (satisfiable)
echo "Test 3: Enumerators should still implement IDisposable"
if grep -q "class.*Enumerator\$instance.*implements.*IDisposable" "$VALIDATE_DIR/System.Collections.Generic/internal/index.d.ts" 2>/dev/null; then
    echo "  PASS: Enumerator types still implement IDisposable"
else
    echo "  FAIL: Enumerator types lost IDisposable implementation"
    ERRORS=$((ERRORS + 1))
fi

# Test 4: No TS2420 errors (Class incorrectly implements interface)
echo "Test 4: No TS2420 errors in validation output"
if grep -q "TS2420" "$PROJECT_DIR/.tests/tsc-validation.txt" 2>/dev/null; then
    TS2420_COUNT=$(grep -c "TS2420" "$PROJECT_DIR/.tests/tsc-validation.txt")
    echo "  FAIL: Found $TS2420_COUNT TS2420 errors"
    ERRORS=$((ERRORS + 1))
else
    echo "  PASS: No TS2420 errors"
fi

# Test 5: No TS2416 errors (Property not assignable to same property in base)
echo "Test 5: No TS2416 errors in validation output"
if grep -q "TS2416" "$PROJECT_DIR/.tests/tsc-validation.txt" 2>/dev/null; then
    TS2416_COUNT=$(grep -c "TS2416" "$PROJECT_DIR/.tests/tsc-validation.txt")
    echo "  FAIL: Found $TS2416_COUNT TS2416 errors"
    ERRORS=$((ERRORS + 1))
else
    echo "  PASS: No TS2416 errors"
fi

# Test 6: Views provide typed accessors for interface members
echo "Test 6: Views provide typed accessors for interface members"
if grep -q "As_IEnumerable_1" "$VALIDATE_DIR/System.Collections.Generic/internal/index.d.ts" 2>/dev/null; then
    echo "  PASS: View accessors exist (As_IEnumerable_1, etc.)"
else
    echo "  FAIL: View accessors missing"
    ERRORS=$((ERRORS + 1))
fi

# Test 7: Type alias intersects class with views
echo "Test 7: Type alias intersects class with views"
if grep -q "List_1<T> = List_1\$instance<T> & __List_1\$views<T>" "$VALIDATE_DIR/System.Collections.Generic/internal/index.d.ts" 2>/dev/null; then
    echo "  PASS: List_1<T> is intersection of instance and views"
else
    echo "  FAIL: List_1<T> intersection type missing"
    ERRORS=$((ERRORS + 1))
fi

echo ""
if [ $ERRORS -eq 0 ]; then
    echo "✓ ALL HONEST IMPLEMENTS TESTS PASSED"
    exit 0
else
    echo "✗ $ERRORS test(s) failed"
    exit 1
fi
