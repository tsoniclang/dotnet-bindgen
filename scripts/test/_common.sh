#!/bin/bash
# Common test utilities for tsbindgen regression tests
# Source this file at the top of each test script:
#   source "$(dirname "${BASH_SOURCE[0]}")/_common.sh"

set -euo pipefail

# ============================================================
# Path Setup
# ============================================================

# Get absolute paths
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[1]:-${BASH_SOURCE[0]}}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TESTS_DIR="$PROJECT_ROOT/.tests"

# Cached BCL output directories (one per mode)
BCL_DEFAULT_DIR="$TESTS_DIR/bcl"
BCL_NAMING_JS_DIR="$TESTS_DIR/bcl-naming-js"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
NC='\033[0m' # No Color

# ============================================================
# Runtime Detection
# ============================================================

detect_runtime() {
    if [ -n "${DOTNET_RUNTIME:-}" ]; then
        # Use explicit path if set via environment
        echo "$DOTNET_RUNTIME"
        return 0
    fi

    local runtime_path=""

    # Check known .NET runtime locations
    local known_paths=(
        "/home/jeswin/dotnet/shared/Microsoft.NETCore.App"
        "/usr/share/dotnet/shared/Microsoft.NETCore.App"
        "/usr/local/share/dotnet/shared/Microsoft.NETCore.App"
        "$HOME/.dotnet/shared/Microsoft.NETCore.App"
    )

    for base_path in "${known_paths[@]}"; do
        if [ -d "$base_path" ]; then
            # Get the latest version directory
            runtime_path=$(ls -d "$base_path"/*/ 2>/dev/null | sort -V | tail -1)
            if [ -n "$runtime_path" ]; then
                break
            fi
        fi
    done

    if [ -z "$runtime_path" ]; then
        echo "ERROR: Could not auto-detect .NET runtime." >&2
        echo "Set DOTNET_RUNTIME environment variable to your runtime path." >&2
        echo "Example: export DOTNET_RUNTIME=/usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.0" >&2
        return 1
    fi

    # Remove trailing slash
    echo "${runtime_path%/}"
}

# Export detected runtime (call this once per script)
init_runtime() {
    DOTNET_RUNTIME=$(detect_runtime) || exit 1
    export DOTNET_RUNTIME
}

# ============================================================
# TypeScript Compiler
# ============================================================

# Get path to tsc, preferring local installation
get_tsc() {
    if [ -x "$PROJECT_ROOT/node_modules/.bin/tsc" ]; then
        echo "$PROJECT_ROOT/node_modules/.bin/tsc"
    elif command -v tsc &> /dev/null; then
        echo "tsc"
    else
        echo ""
    fi
}

# Run tsc without network access
# Usage: run_tsc [args...]
run_tsc() {
    local tsc_path
    tsc_path=$(get_tsc)

    if [ -n "$tsc_path" ]; then
        "$tsc_path" "$@"
    else
        # Fall back to npx --no-install (fails if not cached)
        if ! npx --no-install tsc "$@" 2>/dev/null; then
            echo -e "${RED}ERROR: TypeScript not installed. Run 'npm install' first.${NC}" >&2
            return 1
        fi
    fi
}

# ============================================================
# BCL Generation Cache
# ============================================================

# Ensure BCL is generated for the given mode
# Usage: ensure_bcl [default|naming-js]
# Returns: path to the BCL output directory
ensure_bcl() {
    local mode="${1:-default}"
    local out_dir
    local extra_args=""

    case "$mode" in
        default)
            out_dir="$BCL_DEFAULT_DIR"
            ;;
        naming-js)
            out_dir="$BCL_NAMING_JS_DIR"
            extra_args="--naming js"
            ;;
        *)
            echo "ERROR: Unknown BCL mode: $mode" >&2
            echo "Valid modes: default, naming-js" >&2
            return 1
            ;;
    esac

    # Check if cache exists and is valid
    if [ -d "$out_dir" ] && [ -f "$out_dir/System/internal/index.d.ts" ]; then
        echo "$out_dir"
        return 0
    fi

    # Need to generate
    init_runtime

    echo "Generating BCL cache ($mode)..." >&2
    mkdir -p "$out_dir"

    if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
        generate -d "$DOTNET_RUNTIME" \
        --out-dir "$out_dir" \
        $extra_args \
        > /dev/null 2>&1; then
        echo -e "${RED}ERROR: BCL generation failed${NC}" >&2
        rm -rf "$out_dir"
        return 1
    fi

    echo "$out_dir"
}

# Generate a single assembly (for tests that need specific assemblies)
# Usage: generate_assembly <assembly.dll> <out_dir> [extra_args...]
generate_assembly() {
    local assembly="$1"
    local out_dir="$2"
    shift 2
    local extra_args="$*"

    init_runtime

    mkdir -p "$out_dir"

    dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
        generate -a "$DOTNET_RUNTIME/$assembly" \
        --out-dir "$out_dir" \
        $extra_args
}

# ============================================================
# Test Utilities
# ============================================================

# Print test result
# Usage: test_result PASS|FAIL "message"
test_result() {
    local status="$1"
    local message="$2"

    if [ "$status" = "PASS" ]; then
        echo -e "  ${GREEN}[PASS]${NC} $message"
    else
        echo -e "  ${RED}[FAIL]${NC} $message"
    fi
}

# Assert that a pattern exists in a file
# Usage: assert_grep <pattern> <file> <description>
assert_grep() {
    local pattern="$1"
    local file="$2"
    local description="$3"

    if grep -Fq "$pattern" "$file" 2>/dev/null; then
        test_result PASS "$description"
        return 0
    else
        test_result FAIL "$description"
        echo "    Expected pattern: $pattern"
        echo "    In file: $file"
        return 1
    fi
}

# Assert that a pattern does NOT exist in a file
# Usage: assert_not_grep <pattern> <file> <description>
assert_not_grep() {
    local pattern="$1"
    local file="$2"
    local description="$3"

    if ! grep -Fq "$pattern" "$file" 2>/dev/null; then
        test_result PASS "$description"
        return 0
    else
        test_result FAIL "$description"
        echo "    Unexpected pattern found: $pattern"
        echo "    In file: $file"
        return 1
    fi
}

# Clean all BCL caches
clean_bcl_caches() {
    rm -rf "$BCL_DEFAULT_DIR" "$BCL_NAMING_JS_DIR"
}

# ============================================================
# Initialization
# ============================================================

# Create .tests directory if needed
mkdir -p "$TESTS_DIR"
