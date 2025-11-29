#!/bin/bash
# Test surface manifest - verify emitted API surface matches baseline

set -euo pipefail

echo "================================================"
echo "Surface Manifest Verification"
echo "================================================"
echo ""

# Configuration
BCL_PATH="$HOME/dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.1.25451.107"
TEMP_OUTPUT=".tests/surface-verify"
BASELINE_MANIFEST="scripts/harness/baselines/bcl-surface-manifest.json"
CURRENT_MANIFEST=".tests/surface-current-manifest.json"

# Check baseline exists
if [ ! -f "$BASELINE_MANIFEST" ]; then
    echo "❌ FAILED: Baseline manifest not found: $BASELINE_MANIFEST"
    echo ""
    echo "Run: bash scripts/capture-surface-manifest.sh"
    exit 1
fi

# Clean and prepare
echo "[1/4] Preparing output directory..."
rm -rf "$TEMP_OUTPUT"
mkdir -p "$TEMP_OUTPUT"

# Run generation
echo "[2/4] Running strict generation..."
output=$(dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
    generate -d "$BCL_PATH" \
    -o "$TEMP_OUTPUT" --strict 2>&1) || {
    echo "❌ FAILED: Generation failed"
    exit 1
}

# Extract statistics
namespaces=$(echo "$output" | grep "Namespaces:" | grep -o "[0-9]*" | head -1)
types=$(echo "$output" | grep "Types:" | grep -o "[0-9]*" | head -1)
members=$(echo "$output" | grep "Members:" | grep -o "[0-9]*" | head -1)

echo "✓ Generation complete"
echo ""

# Compute current manifest
echo "[3/4] Computing current manifest..."

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

cat > "$CURRENT_MANIFEST" <<EOF
{
  "dotnetVersion": "10.0.0-rc.1.25451.107",
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

echo "✓ Current manifest computed"
echo ""

# Compare manifests
echo "[4/4] Comparing against baseline..."

# Extract baseline stats
baseline_namespaces=$(jq -r '.generation.namespaces' "$BASELINE_MANIFEST")
baseline_types=$(jq -r '.generation.types' "$BASELINE_MANIFEST")
baseline_members=$(jq -r '.generation.members' "$BASELINE_MANIFEST")

# Check counts
counts_match=true
if [ "$namespaces" != "$baseline_namespaces" ]; then
    counts_match=false
fi
if [ "$types" != "$baseline_types" ]; then
    counts_match=false
fi
if [ "$members" != "$baseline_members" ]; then
    counts_match=false
fi

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
has_diff=false

if [ -n "$added_files" ] || [ -n "$removed_files" ] || [ -n "$changed_files" ] || [ "$counts_match" = false ]; then
    has_diff=true
fi

if [ "$has_diff" = true ]; then
    echo "❌ SURFACE REGRESSION DETECTED"
    echo ""
    echo "The emitted TypeScript API surface has changed compared to baseline."
    echo ""

    if [ "$counts_match" = false ]; then
        echo "Generation Counts Changed:"
        echo "  Namespaces: $baseline_namespaces → $namespaces"
        echo "  Types:      $baseline_types → $types"
        echo "  Members:    $baseline_members → $members"
        echo ""
    fi

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

echo "✓ Surface matches baseline"
echo ""

echo "================================================"
echo "✓ VERIFICATION PASSED"
echo "================================================"
echo ""
echo "Summary:"
echo "  Files verified:  $file_count"
echo "  Namespaces:      $namespaces"
echo "  Types:           $types"
echo "  Members:         $members"
echo "  All hashes:      ✓ match baseline"
echo ""
