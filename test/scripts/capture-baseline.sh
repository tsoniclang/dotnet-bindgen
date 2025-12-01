#!/bin/bash
# Capture surface manifest - baseline snapshot of emitted TypeScript API surface

source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

echo "================================================"
echo "Surface Manifest Capture"
echo "================================================"
echo ""

# Initialize runtime
init_runtime

# Configuration
TEMP_OUTPUT="$TESTS_DIR/surface-capture"
MANIFEST_FILE="$PROJECT_ROOT/test/baselines/bcl-surface-manifest.json"

# Clean and prepare
echo "[1/4] Preparing output directory..."
rm -rf "$TEMP_OUTPUT"
mkdir -p "$TEMP_OUTPUT"

# Run generation (without --strict to allow capture even with warnings)
echo "[2/4] Running generation..."
output=$(dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
    generate -d "$DOTNET_RUNTIME" \
    -o "$TEMP_OUTPUT" --logs PhaseGate 2>&1)

if [ $? -ne 0 ]; then
    echo -e "${RED}❌ Generation failed${NC}"
    echo "$output"
    exit 1
fi

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

mkdir -p "$(dirname "$MANIFEST_FILE")"

# Get .NET version from runtime path
dotnet_version=$(basename "$DOTNET_RUNTIME")

cat > "$MANIFEST_FILE" <<EOF
{
  "dotnetVersion": "$dotnet_version",
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
