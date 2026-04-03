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
    if [ -n "${TSC:-}" ]; then
        if [ -x "$TSC" ]; then
            echo "$TSC"
            return 0
        fi

        if command -v "$TSC" &> /dev/null; then
            echo "$TSC"
            return 0
        fi
    fi

    local candidates=(
        "$PROJECT_ROOT/node_modules/.bin/tsc"
        "$PROJECT_ROOT/../tsonic/node_modules/.bin/tsc"
    )

    for candidate in "${candidates[@]}"; do
        if [ -x "$candidate" ]; then
            echo "$candidate"
            return 0
        fi
    done

    if command -v tsc &> /dev/null; then
        echo "tsc"
        return 0
    fi

    echo ""
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

compute_bcl_cache_key() {
    local mode="$1"

    (
        cd "$PROJECT_ROOT"

        {
            echo "mode=$mode"
            echo "runtime=$DOTNET_RUNTIME"
            find src/tsbindgen -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.json' \) -print0 \
                | sort -z \
                | xargs -0 sha256sum
        } | sha256sum | awk '{print $1}'
    )
}

# Ensure BCL is generated.
# Usage: ensure_bcl [default]
# Returns: path to the BCL output directory
ensure_bcl() {
    local mode="${1:-default}"
    local out_dir

    case "$mode" in
        default)
            out_dir="$BCL_DEFAULT_DIR"
            ;;
        *)
            echo "ERROR: Unknown BCL mode: $mode" >&2
            echo "Valid modes: default" >&2
            return 1
            ;;
    esac

    init_runtime

    local cache_key_file="$out_dir/.cache-key"
    local current_cache_key
    current_cache_key="$(compute_bcl_cache_key "$mode")"

    if [ -d "$out_dir" ] &&
        [ -f "$out_dir/System/internal/index.d.ts" ] &&
        [ -f "$cache_key_file" ] &&
        [ "$(cat "$cache_key_file")" = "$current_cache_key" ]; then
        echo "$out_dir"
        return 0
    fi

    echo "Generating BCL cache ($mode)..." >&2
    rm -rf "$out_dir"
    mkdir -p "$out_dir"

    if ! dotnet run --project "$PROJECT_ROOT/src/tsbindgen/tsbindgen.csproj" -- \
        generate -d "$DOTNET_RUNTIME" \
        --out-dir "$out_dir" \
        > /dev/null 2>&1; then
        echo -e "${RED}ERROR: BCL generation failed${NC}" >&2
        rm -rf "$out_dir"
        return 1
    fi

    printf '%s\n' "$current_cache_key" > "$cache_key_file"

    echo "$out_dir"
}

prepare_local_core_dependency() {
    local target_dir="$1"

    init_runtime

    local dotnet_major
    dotnet_major="$(basename "$DOTNET_RUNTIME" | cut -d. -f1)"
    local core_dir="$PROJECT_ROOT/../core/versions/$dotnet_major"

    if [ ! -d "$core_dir" ]; then
        echo -e "${RED}ERROR: Local @tsonic/core package not found: $core_dir${NC}" >&2
        return 1
    fi

    rm -rf "$target_dir/node_modules" "$target_dir/package-lock.json"

    cat > "$target_dir/package.json" <<EOF
{ "dependencies": { "@tsonic/core": "file:$core_dir" } }
EOF

    (
        cd "$target_dir"
        npm install --silent --legacy-peer-deps 2>/dev/null || npm install --legacy-peer-deps
    )
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
    rm -rf "$BCL_DEFAULT_DIR"
}

# ============================================================
# Initialization
# ============================================================

# Create .tests directory if needed
mkdir -p "$TESTS_DIR"
