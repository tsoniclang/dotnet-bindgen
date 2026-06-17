#!/usr/bin/env bash
set -e

# Build script for dotnet-bindgen
# Uses standard .NET SDK build paths configured in Directory.Build.props
#
# Usage:
#   ./build.sh              # Build for current platform only
#   ./build.sh --all-archs  # Build for all supported architectures (linux-x64, linux-arm64, osx-x64, osx-arm64)

ALL_ARCHS=false
if [[ "$1" == "--all-archs" ]]; then
  ALL_ARCHS=true
fi

echo "Building DotnetBindgen..."

# Clean previous artifacts
rm -rf artifacts/

if [[ "$ALL_ARCHS" == "true" ]]; then
  # Build for all supported architectures (exclude Windows)
  RIDS=("linux-x64" "linux-arm64" "osx-x64" "osx-arm64")

  for rid in "${RIDS[@]}"; do
    echo ""
    echo "Building for $rid..."
    dotnet publish src/DotnetBindgen/DotnetBindgen.csproj \
      --configuration Release \
      --runtime "$rid" \
      --self-contained false
  done

  echo ""
  echo "Build complete for all architectures!"
  echo "Output locations:"
  for rid in "${RIDS[@]}"; do
    echo "  - artifacts/bin/dotnet-bindgen/Release/net10.0/$rid/publish/"
  done
else
  # Single-arch build (current platform)
  dotnet publish src/DotnetBindgen/DotnetBindgen.csproj \
    --configuration Release

  echo ""
  echo "Build complete!"
  echo "Output: artifacts/bin/dotnet-bindgen/Release/net10.0/publish/"
fi
