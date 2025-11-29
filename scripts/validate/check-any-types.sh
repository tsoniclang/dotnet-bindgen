#!/bin/bash
# Check for 'any' type usage in generated TypeScript declarations
#
# This script scans all generated .d.ts files and fails if any 'any' types are found.
# 'any' weakens type safety and should never appear in generated declarations.
#
# Usage:
#   ./scripts/check-any-types.sh [validation_dir]
#
# Arguments:
#   validation_dir - Directory containing generated declarations (default: .tests/validate)

set -e

VALIDATION_DIR="${1:-.tests/validate}"

echo "================================================================"
echo "Checking for 'any' types in generated declarations"
echo "================================================================"
echo ""
echo "Scanning: $VALIDATION_DIR"
echo ""

if [ ! -d "$VALIDATION_DIR" ]; then
    echo "ERROR: Validation directory not found: $VALIDATION_DIR"
    echo "Run 'node scripts/validate.js' first to generate declarations"
    exit 1
fi

# Count occurrences of 'any' (word boundary match, excluding comments)
# The grep pattern matches 'any' as a standalone word
# We exclude lines that are comments (// any is acceptable for documentation)
ANY_OCCURRENCES=$(grep -rw '\bany\b' "$VALIDATION_DIR" --include="*.d.ts" 2>/dev/null || true)

# Filter out comment lines and count
ANY_COUNT=$(echo "$ANY_OCCURRENCES" | grep -v "^$" | grep -v "// " | wc -l)

if [ "$ANY_COUNT" -gt 0 ]; then
    echo "FAIL: Found 'any' type usage in generated declarations"
    echo ""
    echo "Occurrences (first 20):"
    echo "$ANY_OCCURRENCES" | grep -v "^$" | grep -v "// " | head -20
    echo ""
    echo "Total count: $ANY_COUNT"
    echo ""
    echo "The 'any' type weakens type safety and should not appear in"
    echo "generated declarations. Fix the generator to emit proper types."
    exit 1
fi

echo "SUCCESS: No 'any' types found in generated declarations"
echo ""
echo "Scanned $(find "$VALIDATION_DIR" -name "*.d.ts" | wc -l) declaration files"
