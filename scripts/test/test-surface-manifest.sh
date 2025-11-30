#!/bin/bash
# Test surface manifest - verify emitted API surface matches baseline
# This test does NOT generate - it verifies an existing output directory.
#
# Usage:
#   SURFACE_VERIFY_DIR=/path/to/output ./test-surface-manifest.sh
# Or:
#   ./test-surface-manifest.sh  # Uses cached BCL from ensure_bcl

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Surface Manifest Verification"
echo "================================================"
echo ""

# Use provided directory or cached BCL
if [ -n "${SURFACE_VERIFY_DIR:-}" ]; then
    TEMP_OUTPUT="$SURFACE_VERIFY_DIR"
    echo "Using provided output: $TEMP_OUTPUT"
else
    TEMP_OUTPUT=$(ensure_bcl default)
    echo "Using cached BCL: $TEMP_OUTPUT"
fi

BASELINE_MANIFEST="$PROJECT_ROOT/scripts/harness/baselines/bcl-surface-manifest.json"
CURRENT_MANIFEST="$TESTS_DIR/surface-current-manifest.json"

# Check baseline exists
if [ ! -f "$BASELINE_MANIFEST" ]; then
    echo -e "${RED}❌ FAILED: Baseline manifest not found: $BASELINE_MANIFEST${NC}"
    echo ""
    echo "Run: bash scripts/capture-surface-manifest.sh"
    exit 1
fi

# Check output exists
if [ ! -d "$TEMP_OUTPUT" ]; then
    echo -e "${RED}❌ FAILED: Output directory not found: $TEMP_OUTPUT${NC}"
    exit 1
fi

echo ""
echo "[1/3] Computing current manifest..."

files=$(find "$TEMP_OUTPUT" -type f \( -name "*.d.ts" -o -name "*.metadata.json" \) | sort)

manifest_entries=""
file_count=0

for file in $files; do
    rel_path="${file#$TEMP_OUTPUT/}"
    hash=$(sha256sum "$file" | cut -d' ' -f1)

    if [ $file_count -gt 0 ]; then
        manifest_entries="$manifest_entries,"
    fi

    manifest_entries="$manifest_entries
    \"$rel_path\": \"sha256:$hash\""

    file_count=$((file_count + 1))
done

# Extract stats from output (look for summary file or count directories)
namespaces=$(find "$TEMP_OUTPUT" -mindepth 1 -maxdepth 1 -type d | wc -l)
# For types/members, we'd need the actual generation output. Use placeholder if not available.
types="${SURFACE_TYPES:-0}"
members="${SURFACE_MEMBERS:-0}"

cat > "$CURRENT_MANIFEST" <<EOF
{
  "dotnetVersion": "auto-detected",
  "capturedAt": "$(date -u +"%Y-%m-%dT%H:%M:%SZ")",
  "generation": {
    "namespaces": $namespaces,
    "types": $types,
    "members": $members
  },
  "files": {$manifest_entries
  }
}
EOF

echo -e "${GREEN}✓ Current manifest computed${NC}"
echo ""

# Compare manifests
echo "[2/3] Comparing against baseline..."

# Extract baseline stats
baseline_namespaces=$(jq -r '.generation.namespaces' "$BASELINE_MANIFEST")

# Extract file lists
baseline_files=$(jq -r '.files | keys[]' "$BASELINE_MANIFEST" | sort)
current_files=$(jq -r '.files | keys[]' "$CURRENT_MANIFEST" | sort)

# Find added/removed files
added_files=$(comm -13 <(echo "$baseline_files") <(echo "$current_files"))
removed_files=$(comm -23 <(echo "$baseline_files") <(echo "$current_files"))

# Check for hash mismatches in common files
changed_files=""
for file in $(comm -12 <(echo "$baseline_files") <(echo "$current_files")); do
    baseline_hash=$(jq -r ".files[\"$file\"]" "$BASELINE_MANIFEST")
    current_hash=$(jq -r ".files[\"$file\"]" "$CURRENT_MANIFEST")

    if [ "$baseline_hash" != "$current_hash" ]; then
        changed_files="$changed_files
  - $file"
    fi
done

# Report results
echo "[3/3] Analyzing differences..."
echo ""

has_diff=false

if [ -n "$added_files" ] || [ -n "$removed_files" ] || [ -n "$changed_files" ]; then
    has_diff=true
fi

if [ "$has_diff" = true ]; then
    echo -e "${RED}❌ SURFACE REGRESSION DETECTED${NC}"
    echo ""
    echo "The emitted TypeScript API surface has changed compared to baseline."
    echo ""

    if [ -n "$added_files" ]; then
        echo "Added Files:"
        echo "$added_files" | sed 's/^/  + /'
        echo ""
    fi

    if [ -n "$removed_files" ]; then
        echo "Removed Files:"
        echo "$removed_files" | sed 's/^/  - /'
        echo ""
    fi

    if [ -n "$changed_files" ]; then
        echo "Changed Files (hash mismatch):"
        echo "$changed_files"
        echo ""
    fi

    echo "If this change is INTENTIONAL:"
    echo "  1. Review the changes carefully"
    echo "  2. Update baseline: bash scripts/capture-surface-manifest.sh"
    echo "  3. Commit the updated baseline"
    echo ""
    echo "If this change is UNINTENTIONAL:"
    echo "  1. Investigate what caused the drift"
    echo "  2. Fix the root cause"
    echo "  3. Re-run this test"
    echo ""

    exit 1
fi

echo -e "${GREEN}✓ Surface matches baseline${NC}"
echo ""

echo "================================================"
echo -e "${GREEN}✓ VERIFICATION PASSED${NC}"
echo "================================================"
echo ""
echo "Summary:"
echo "  Files verified:  $file_count"
echo "  Namespaces:      $namespaces"
echo "  All hashes:      ✓ match baseline"
echo ""
