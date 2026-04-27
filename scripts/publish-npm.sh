#!/bin/bash
set -euo pipefail

# Publish @tsonic/tsbindgen and tsbindgen wrapper to npm
#
# Usage: ./scripts/publish-npm.sh [--ignore-branches-ahead] [--dangerously-skip-tests]
#
# Options:
#   --ignore-branches-ahead   Skip check for local branches ahead of main
#   --dangerously-skip-tests  Skip tests. This is intended only for an explicitly
#                             authorized release wave.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
WRAPPER_DIR="$ROOT_DIR/npm/tsbindgen"

# Parse arguments
IGNORE_BRANCHES_AHEAD=false
DANGEROUSLY_SKIP_TESTS=false
for arg in "$@"; do
    case $arg in
        --ignore-branches-ahead)
            IGNORE_BRANCHES_AHEAD=true
            ;;
        --dangerously-skip-tests)
            DANGEROUSLY_SKIP_TESTS=true
            ;;
        *)
            echo "Error: unknown argument '$arg'"
            exit 1
            ;;
    esac
done

cd "$ROOT_DIR"

# Helper: Compare semver versions
# Returns: 1 if v1 > v2, 0 if v1 == v2, -1 if v1 < v2
compare_versions() {
    local v1="$1" v2="$2"
    if [ "$v1" = "$v2" ]; then echo 0; return; fi

    IFS='.' read -r v1_major v1_minor v1_patch <<< "$v1"
    IFS='.' read -r v2_major v2_minor v2_patch <<< "$v2"

    if [ "$v1_major" -gt "$v2_major" ]; then echo 1; return; fi
    if [ "$v1_major" -lt "$v2_major" ]; then echo -1; return; fi
    if [ "$v1_minor" -gt "$v2_minor" ]; then echo 1; return; fi
    if [ "$v1_minor" -lt "$v2_minor" ]; then echo -1; return; fi
    if [ "$v1_patch" -gt "$v2_patch" ]; then echo 1; return; fi
    if [ "$v1_patch" -lt "$v2_patch" ]; then echo -1; return; fi
    echo 0
}

# ============================================================
# PRE-FLIGHT CHECKS (before any action)
# ============================================================

echo "=== Pre-flight checks ==="

# 1. Must be on main branch
CURRENT_BRANCH=$(git branch --show-current)
if [ "$CURRENT_BRANCH" != "main" ]; then
    echo "Error: Must be on main branch to publish."
    echo "Current branch: $CURRENT_BRANCH"
    exit 1
fi

# 2. Must be synced with origin (no bypass)
git fetch origin main
LOCAL_COMMIT=$(git rev-parse HEAD)
REMOTE_COMMIT=$(git rev-parse origin/main)

if [ "$LOCAL_COMMIT" != "$REMOTE_COMMIT" ]; then
    echo "Error: Local main is not synced with origin/main."
    echo "Please run: git pull"
    exit 1
fi

# 3. Check for local branches ahead of main (bypass with --ignore-branches-ahead)
echo "=== Checking for branches ahead of main ==="
BRANCHES_AHEAD=()

for branch in $(git for-each-ref --format='%(refname:short)' refs/heads/); do
    if [ "$branch" = "main" ]; then
        continue
    fi

    # Get ahead/behind counts relative to main
    COUNTS=$(git rev-list --left-right --count main..."$branch" 2>/dev/null || echo "0 0")
    BEHIND=$(echo "$COUNTS" | awk '{print $1}')
    AHEAD=$(echo "$COUNTS" | awk '{print $2}')

    # Only flag branches that are X ahead and 0 behind (unmerged work)
    if [ "$AHEAD" -gt 0 ] && [ "$BEHIND" -eq 0 ]; then
        BRANCHES_AHEAD+=("$branch ($AHEAD ahead)")
    fi
done

if [ ${#BRANCHES_AHEAD[@]} -gt 0 ]; then
    echo "Warning: The following branches are ahead of main:"
    for branch_info in "${BRANCHES_AHEAD[@]}"; do
        echo "  - $branch_info"
    done

    if [ "$IGNORE_BRANCHES_AHEAD" = true ]; then
        echo "Continuing anyway (--ignore-branches-ahead specified)"
    else
        echo ""
        echo "Error: Unmerged branches detected. Merge them first or use --ignore-branches-ahead"
        exit 1
    fi
else
    echo "  No branches ahead of main"
fi

# 4. No uncommitted changes
if [ -n "$(git status --porcelain)" ]; then
    echo "Error: Uncommitted changes detected."
    echo "Please commit or discard changes first."
    exit 1
fi

# 5. Ensure tsbindgen and wrapper have the same version
echo "=== Checking package version consistency ==="
LOCAL_VERSION=$(node -p "require('./package.json').version")
WRAPPER_VERSION=$(node -p "require('$WRAPPER_DIR/package.json').version")

if [ "$LOCAL_VERSION" != "$WRAPPER_VERSION" ]; then
    echo "Error: Package version mismatch!"
    echo "  @tsonic/tsbindgen: $LOCAL_VERSION"
    echo "  tsbindgen (wrapper): $WRAPPER_VERSION"
    echo "All packages must have the same version."
    exit 1
fi

echo "  All packages at version $LOCAL_VERSION"

# 6. Check versions against npm
echo "=== Checking versions against npm ==="
NEEDS_BUMP=false

NPM_VERSION=$(npm view @tsonic/tsbindgen version 2>/dev/null || echo "0.0.0")
CMP=$(compare_versions "$LOCAL_VERSION" "$NPM_VERSION")

echo "  @tsonic/tsbindgen: local=$LOCAL_VERSION npm=$NPM_VERSION"

if [ "$CMP" = "-1" ]; then
    echo "Error: Local version ($LOCAL_VERSION) is LESS than npm version ($NPM_VERSION)"
    echo "This should never happen. Please investigate."
    exit 1
elif [ "$CMP" = "0" ]; then
    NEEDS_BUMP=true
fi

# Also check wrapper
WRAPPER_NPM_VERSION=$(npm view tsbindgen version 2>/dev/null || echo "0.0.0")
WRAPPER_CMP=$(compare_versions "$WRAPPER_VERSION" "$WRAPPER_NPM_VERSION")

echo "  tsbindgen (wrapper): local=$WRAPPER_VERSION npm=$WRAPPER_NPM_VERSION"

if [ "$WRAPPER_CMP" = "-1" ]; then
    echo "Error: Local wrapper version ($WRAPPER_VERSION) is LESS than npm version ($WRAPPER_NPM_VERSION)"
    echo "This should never happen. Please investigate."
    exit 1
elif [ "$WRAPPER_CMP" = "0" ]; then
    NEEDS_BUMP=true
fi

echo ""

# ============================================================
# DETERMINE ACTION
# ============================================================

if [ "$NEEDS_BUMP" = true ]; then
    # Calculate new version
    IFS='.' read -r major minor patch <<< "$LOCAL_VERSION"
    NEW_VERSION="$major.$minor.$((patch + 1))"

    RELEASE_BRANCH="release/v$NEW_VERSION"
    echo "=== Creating release branch: $RELEASE_BRANCH ==="
    git checkout -b "$RELEASE_BRANCH"
    NEED_BRANCH=true
else
    echo "=== All local versions are greater than npm - publishing directly ==="
    NEED_BRANCH=false
    NEW_VERSION="$LOCAL_VERSION"
fi

# ============================================================
# BUILD AND TEST
# ============================================================

echo "=== Building tsbindgen ==="
dotnet publish "$ROOT_DIR/src/tsbindgen/tsbindgen.csproj" -c Release -o "$ROOT_DIR/lib/"

echo "=== Cleaning lib/ ==="
# Remove localization folders
for lang in cs de es fr it ja ko pl pt-BR ru tr zh-Hans zh-Hant; do
    rm -rf "$ROOT_DIR/lib/$lang"
done
# Remove pdb files
rm -f "$ROOT_DIR/lib/"*.pdb
# Remove native exe (if any)
rm -f "$ROOT_DIR/lib/tsbindgen"

if [ "$DANGEROUSLY_SKIP_TESTS" = true ]; then
    echo "=== DANGEROUSLY skipping tests (--dangerously-skip-tests) ==="
else
    echo "=== Running ALL tests ==="
    "$ROOT_DIR/test/scripts/run-all.sh"
    echo "All tests passed"
fi

# ============================================================
# VERSION BUMP (if needed)
# ============================================================

if [ "$NEED_BRANCH" = true ]; then
    echo "=== Bumping versions to $NEW_VERSION ==="

    # Update package.json
    node -e "
        const fs = require('fs');
        const pkg = JSON.parse(fs.readFileSync('$ROOT_DIR/package.json', 'utf8'));
        pkg.version = '$NEW_VERSION';
        fs.writeFileSync('$ROOT_DIR/package.json', JSON.stringify(pkg, null, 2) + '\n');
    "

    # Update wrapper package.json
    node -e "
        const fs = require('fs');
        const pkg = JSON.parse(fs.readFileSync('$WRAPPER_DIR/package.json', 'utf8'));
        pkg.version = '$NEW_VERSION';
        pkg.dependencies['@tsonic/tsbindgen'] = '$NEW_VERSION';
        fs.writeFileSync('$WRAPPER_DIR/package.json', JSON.stringify(pkg, null, 2) + '\n');
    "

    echo "=== Committing version changes ==="
    cd "$ROOT_DIR"
    git add package.json npm/tsbindgen/package.json
    git commit -m "chore: bump version to $NEW_VERSION"
    git push -u origin HEAD

    LOCAL_VERSION="$NEW_VERSION"
fi

# ============================================================
# PUBLISH PACKAGES
# ============================================================

echo "=== Publishing @tsonic/tsbindgen@$LOCAL_VERSION ==="
cd "$ROOT_DIR"
npm publish --access public --ignore-scripts

echo "=== Publishing tsbindgen@$LOCAL_VERSION ==="
cd "$WRAPPER_DIR"
npm publish --access public --ignore-scripts

# ============================================================
# DONE
# ============================================================

cd "$ROOT_DIR"

echo ""
echo "=== Done ==="
echo "Published:"
echo "  - @tsonic/tsbindgen@$LOCAL_VERSION"
echo "  - tsbindgen@$LOCAL_VERSION"

if [ "$NEED_BRANCH" = true ]; then
    echo ""
    echo "Note: Changes were made on branch '$RELEASE_BRANCH'"
    echo "Please create a PR to merge back to main."
fi
