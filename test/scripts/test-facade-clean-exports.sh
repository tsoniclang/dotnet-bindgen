#!/usr/bin/env bash
#
# test-facade-clean-exports.sh
#
# Regression test: Verify facade files don't leak internal $instance/$views types
#
# After removing `export *` from facades, we need to ensure:
# 1. No `export *` statements in facade files
# 2. No exported type names ending with $instance or $views
#    (they can appear in import statements when aliased, but not as exported names)

set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "=== Testing Facade Clean Exports ==="

# Use BCL cache for testing
OUTPUT_DIR=$(ensure_bcl default)

ERRORS=0

# Test 1: No `export *` in facade files
echo ""
echo "Test 1: Checking for 'export *' in facade files..."
# Facade files are at root level: Namespace.d.ts (not in subdirectories)
EXPORT_STAR=$(find "$OUTPUT_DIR" -maxdepth 1 -name "*.d.ts" -exec grep -l "export \*" {} \; || true)
if [[ -n "$EXPORT_STAR" ]]; then
    echo "ERROR: Found 'export *' in facade files:"
    echo "$EXPORT_STAR"
    ERRORS=$((ERRORS + 1))
else
    echo "✓ No 'export *' found in facade files"
fi

# Test 2: No exported names ending with $instance or $views
# We check for patterns like:
#   export { Foo$instance }  - BAD (exports $instance directly)
#   export type Foo$instance  - BAD
#   export { Foo$instance as Bar }  - OK (aliased, user sees Bar)
echo ""
echo "Test 2: Checking for leaked \$instance/\$views exports..."

# Find exports that ARE the $instance/$views types (not aliased)
# Pattern: "export { X$instance }" or "export { X$instance, Y }" without " as "
BAD_EXPORTS=$(find "$OUTPUT_DIR" -maxdepth 1 -name "*.d.ts" -exec grep -E 'export\s*\{\s*[^}]*\$instance\s*[,}]|export\s*\{\s*[^}]*\$views\s*[,}]|export\s+type\s+\w+\$(instance|views)' {} \; | grep -v ' as ' || true)

if [[ -n "$BAD_EXPORTS" ]]; then
    echo "ERROR: Found leaked \$instance/\$views exports:"
    echo "$BAD_EXPORTS"
    ERRORS=$((ERRORS + 1))
else
    echo "✓ No leaked \$instance/\$views exports (all are properly aliased)"
fi

# Summary
echo ""
if [[ $ERRORS -eq 0 ]]; then
    echo "=== All facade clean export tests passed ==="
    exit 0
else
    echo "=== FAILED: $ERRORS test(s) failed ==="
    exit 1
fi
