#!/bin/bash
set -euo pipefail

# Publish @tsonic/tsbindgen and tsbindgen wrapper to npm

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
WRAPPER_DIR="$(cd "$ROOT_DIR/../tsbindgen-wrapper" && pwd)"

cd "$ROOT_DIR"

# Check if on main branch - can't push directly to main due to branch rules
CURRENT_BRANCH=$(git branch --show-current)
if [ "$CURRENT_BRANCH" = "main" ]; then
    echo "Error: Cannot run publish script on main branch."
    echo "Create a feature branch first: git checkout -b release/vX.Y.Z"
    exit 1
fi

echo "=== Building tsbindgen ==="
dotnet publish src/tsbindgen/tsbindgen.csproj -c Release -o lib/

echo "=== Cleaning lib/ ==="
# Remove localization folders
for lang in cs de es fr it ja ko pl pt-BR ru tr zh-Hans zh-Hant; do
    rm -rf "lib/$lang"
done
# Remove pdb files
rm -f lib/*.pdb
# Remove native exe (if any)
rm -f lib/tsbindgen

echo "=== Checking versions ==="
LOCAL_VERSION=$(node -p "require('./package.json').version")
PUBLISHED_VERSION=$(npm view @tsonic/tsbindgen version 2>/dev/null || echo "0.0.0")

echo "Local version: $LOCAL_VERSION"
echo "Published version: $PUBLISHED_VERSION"

if [ "$LOCAL_VERSION" = "$PUBLISHED_VERSION" ]; then
    echo "=== Auto-bumping patch version ==="
    # Bump patch version
    IFS='.' read -r major minor patch <<< "$LOCAL_VERSION"
    NEW_VERSION="$major.$minor.$((patch + 1))"
    echo "New version: $NEW_VERSION"

    # Update package.json
    node -e "
        const fs = require('fs');
        const pkg = JSON.parse(fs.readFileSync('package.json', 'utf8'));
        pkg.version = '$NEW_VERSION';
        fs.writeFileSync('package.json', JSON.stringify(pkg, null, 2) + '\n');
    "
    LOCAL_VERSION="$NEW_VERSION"
fi

echo "=== Committing tsbindgen changes ==="
git add package.json
git commit -m "chore: bump version to $LOCAL_VERSION" || echo "No changes to commit"
git push

echo "=== Publishing @tsonic/tsbindgen@$LOCAL_VERSION ==="
npm publish --access public

echo "=== Updating tsbindgen-wrapper ==="
cd "$WRAPPER_DIR"

# Update wrapper package.json
node -e "
    const fs = require('fs');
    const pkg = JSON.parse(fs.readFileSync('package.json', 'utf8'));
    pkg.version = '$LOCAL_VERSION';
    pkg.dependencies['@tsonic/tsbindgen'] = '$LOCAL_VERSION';
    fs.writeFileSync('package.json', JSON.stringify(pkg, null, 2) + '\n');
"

echo "=== Committing wrapper changes ==="
git add package.json
git commit -m "chore: bump version to $LOCAL_VERSION" || echo "No changes to commit"
git push

echo "=== Publishing tsbindgen@$LOCAL_VERSION ==="
npm publish --access public

echo "=== Done ==="
echo "Published:"
echo "  - @tsonic/tsbindgen@$LOCAL_VERSION"
echo "  - tsbindgen@$LOCAL_VERSION"
