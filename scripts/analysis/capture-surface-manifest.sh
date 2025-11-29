#!/bin/bash
# Capture surface manifest - baseline snapshot of emitted TypeScript API surface

set -euo pipefail

echo "================================================"
echo "Surface Manifest Capture"
echo "================================================"
echo ""

# Configuration
BCL_PATH="$HOME/dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.1.25451.107"
TEMP_OUTPUT=".tests/surface-capture"
MANIFEST_FILE="scripts/harness/baselines/bcl-surface-manifest.json"

# Clean and prepare
echo "[1/4] Preparing output directory..."
rm -rf "$TEMP_OUTPUT"
mkdir -p "$TEMP_OUTPUT"

# Run generation
echo "[2/4] Running strict generation..."
output=$(dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
    generate -d "$BCL_PATH" \
    -o "$TEMP_OUTPUT" --strict --logs PhaseGate 2>&1)

# Extract statistics
namespaces=$(echo "$output" | grep "Namespaces:" | grep -o "[0-9]*" | head -1)
types=$(echo "$output" | grep "Types:" | grep -o "[0-9]*" | head -1)
members=$(echo "$output" | grep "Members:" | grep -o "[0-9]*" | head -1)

echo "✓ Generation complete"
echo "  Namespaces: $namespaces"
echo "  Types:      $types"
echo "  Members:    $members"
echo ""

# Compute file hashes
echo "[3/4] Computing file hashes..."

# Find all .d.ts and .metadata.json files, sorted for stability
files=$(find "$TEMP_OUTPUT" -type f \( -name "*.d.ts" -o -name "*.metadata.json" \) | sort)

# Build JSON manually for stable output
manifest_entries=""
file_count=0

for file in $files; do
    # Get path relative to output directory
    rel_path="${file#$TEMP_OUTPUT/}"

    # Compute SHA256 hash
    hash=$(sha256sum "$file" | cut -d' ' -f1)

    # Add comma separator after first entry
    if [ $file_count -gt 0 ]; then
        manifest_entries="$manifest_entries,"
    fi

    # Append entry (properly escaped)
    manifest_entries="$manifest_entries
    \"$rel_path\": \"sha256:$hash\""

    file_count=$((file_count + 1))
done

echo "✓ Computed hashes for $file_count files"
echo ""

# Write manifest
echo "[4/4] Writing manifest..."

mkdir -p baselines

cat > "$MANIFEST_FILE" <<EOF
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

echo "✓ Manifest written to: $MANIFEST_FILE"
echo ""

# Summary
echo "================================================"
echo "✓ SURFACE MANIFEST CAPTURED"
echo "================================================"
echo ""
echo "Summary:"
echo "  Files tracked:   $file_count"
echo "  Namespaces:      $namespaces"
echo "  Types:           $types"
echo "  Members:         $members"
echo ""
echo "Baseline file: $MANIFEST_FILE"
echo ""
echo "Next steps:"
echo "  1. Review the manifest: git diff $MANIFEST_FILE"
echo "  2. Commit if intentional: git add $MANIFEST_FILE"
echo ""
