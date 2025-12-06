#!/bin/bash
set -e

# Build NativeAOT binaries for all platforms
# Run this script to build binaries for npm publishing

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
SRC_DIR="$ROOT_DIR/src/tsbindgen"
NPM_DIR="$ROOT_DIR/npm"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo "=== Building tsbindgen NativeAOT binaries ==="
echo ""

# Platform to RID mapping
declare -A PLATFORM_RIDS=(
  ["darwin-arm64"]="osx-arm64"
  ["darwin-x64"]="osx-x64"
  ["linux-arm64"]="linux-arm64"
  ["linux-x64"]="linux-x64"
)

# Detect current platform
CURRENT_PLATFORM=""
if [[ "$OSTYPE" == "darwin"* ]]; then
  if [[ "$(uname -m)" == "arm64" ]]; then
    CURRENT_PLATFORM="darwin-arm64"
  else
    CURRENT_PLATFORM="darwin-x64"
  fi
elif [[ "$OSTYPE" == "linux"* ]]; then
  if [[ "$(uname -m)" == "aarch64" ]]; then
    CURRENT_PLATFORM="linux-arm64"
  else
    CURRENT_PLATFORM="linux-x64"
  fi
fi

echo "Current platform: $CURRENT_PLATFORM"
echo ""

# Cross-compiler mapping (use wrapper scripts to filter clang-specific flags)
declare -A CROSS_COMPILERS=(
  ["linux-arm64"]="$SCRIPT_DIR/aarch64-gcc-wrapper.sh"
)

# Cross objcopy mapping
declare -A CROSS_OBJCOPY=(
  ["linux-arm64"]="aarch64-linux-gnu-objcopy"
)

# Build function
build_for_platform() {
  local PLATFORM=$1
  local RID=${PLATFORM_RIDS[$PLATFORM]}
  local OUTPUT_DIR="$NPM_DIR/$PLATFORM"

  echo -e "${YELLOW}Building for $PLATFORM (RID: $RID)...${NC}"

  # Determine cross-compiler if needed
  local EXTRA_PROPS=""
  if [[ "$PLATFORM" != "$CURRENT_PLATFORM" ]]; then
    echo -e "${YELLOW}  Cross-compiling from $CURRENT_PLATFORM to $PLATFORM${NC}"

    local CROSS_CC="${CROSS_COMPILERS[$PLATFORM]}"
    local CROSS_OC="${CROSS_OBJCOPY[$PLATFORM]}"
    if [[ -n "$CROSS_CC" ]]; then
      # Check if it's a file (wrapper script) or a command
      if [[ -x "$CROSS_CC" ]] || command -v "$CROSS_CC" &> /dev/null; then
        echo -e "${GREEN}  Using cross-compiler: $CROSS_CC${NC}"
        EXTRA_PROPS="-p:CppCompilerAndLinker=$CROSS_CC"

        # Add cross objcopy if available
        if [[ -n "$CROSS_OC" ]] && command -v "$CROSS_OC" &> /dev/null; then
          echo -e "${GREEN}  Using cross-objcopy: $CROSS_OC${NC}"
          EXTRA_PROPS="$EXTRA_PROPS -p:ObjCopyName=$CROSS_OC"
        fi
      else
        echo -e "${RED}  Cross-compiler $CROSS_CC not found${NC}"
        return 1
      fi
    else
      echo -e "${YELLOW}  Warning: No cross-compiler configured for $PLATFORM${NC}"
      echo -e "${YELLOW}  Build may fail. For Darwin targets, use macOS or CI.${NC}"
    fi
  fi

  # Build with NativeAOT
  dotnet publish "$SRC_DIR/tsbindgen.csproj" \
    -c Release \
    -r "$RID" \
    -p:PublishAot=true \
    -p:StripSymbols=true \
    -p:OptimizationPreference=Size \
    -o "$OUTPUT_DIR" \
    --self-contained true \
    $EXTRA_PROPS

  # Clean up unnecessary files, keep only the binary and package.json
  find "$OUTPUT_DIR" -type f ! -name "tsbindgen" ! -name "package.json" -delete 2>/dev/null || true
  find "$OUTPUT_DIR" -type f -name "*.pdb" -delete 2>/dev/null || true

  if [[ -f "$OUTPUT_DIR/tsbindgen" ]]; then
    chmod +x "$OUTPUT_DIR/tsbindgen"
    local SIZE=$(du -h "$OUTPUT_DIR/tsbindgen" | cut -f1)
    echo -e "${GREEN}  Built: $OUTPUT_DIR/tsbindgen ($SIZE)${NC}"
  else
    echo -e "${RED}  Failed to build for $PLATFORM${NC}"
    return 1
  fi
}

# Parse arguments
BUILD_ALL=false
BUILD_CURRENT=false
PLATFORMS_TO_BUILD=()

if [[ $# -eq 0 ]]; then
  # Default: build for current platform only
  BUILD_CURRENT=true
elif [[ "$1" == "--all" ]]; then
  BUILD_ALL=true
elif [[ "$1" == "--current" ]]; then
  BUILD_CURRENT=true
else
  # Build specific platforms
  PLATFORMS_TO_BUILD=("$@")
fi

# Determine which platforms to build
if [[ "$BUILD_ALL" == true ]]; then
  PLATFORMS_TO_BUILD=("darwin-arm64" "darwin-x64" "linux-arm64" "linux-x64")
elif [[ "$BUILD_CURRENT" == true ]]; then
  if [[ -z "$CURRENT_PLATFORM" ]]; then
    echo -e "${RED}Error: Could not detect current platform${NC}"
    exit 1
  fi
  PLATFORMS_TO_BUILD=("$CURRENT_PLATFORM")
fi

echo "Platforms to build: ${PLATFORMS_TO_BUILD[*]}"
echo ""

# Build each platform
FAILED=()
for PLATFORM in "${PLATFORMS_TO_BUILD[@]}"; do
  if build_for_platform "$PLATFORM"; then
    echo ""
  else
    FAILED+=("$PLATFORM")
    echo ""
  fi
done

# Summary
echo "=== Build Summary ==="
if [[ ${#FAILED[@]} -eq 0 ]]; then
  echo -e "${GREEN}All builds succeeded!${NC}"
else
  echo -e "${RED}Failed platforms: ${FAILED[*]}${NC}"
  exit 1
fi

echo ""
echo "Binaries are in: $NPM_DIR/<platform>/tsbindgen"
echo ""
echo "To publish, run:"
echo "  cd $NPM_DIR/<platform> && npm publish --access public"
