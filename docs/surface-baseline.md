# Surface Baseline Management

This document describes how to manage the BCL surface manifest baseline, which protects the emitted TypeScript API surface from silent drift.

## Purpose

The surface manifest provides a cryptographic snapshot of all emitted TypeScript files:

- **What it protects**: Accidental changes to the public API surface (types, members, signatures, file structure)
- **How it works**: SHA256 hashes of all `.d.ts` and `.metadata.json` files
- **When it triggers**: Any file addition, removal, or content change

This is the final safety gate before emission - ensuring no unreviewed changes reach downstream consumers.

## Baseline File

Location: `test/baselines/bcl-surface-manifest.json`

Structure:
```json
{
  "dotnetVersion": "10.0.0-rc.1.25451.107",
  "capturedAt": "2025-11-22T12:05:06Z",
  "generation": {
    "namespaces": 130,
    "types": 4295,
    "members": 50720
  },
  "files": {
    "System/index.d.ts": "sha256:...",
    "System/internal/metadata.json": "sha256:...",
    ...
  }
}
```

**Important**: This file is committed to the repository and should only change when the API surface intentionally changes.

## When to Refresh Baseline

Refresh the baseline **only** when you have intentionally changed the emitted API surface:

### ✅ Valid Reasons to Refresh

1. **BCL version upgrade** - New .NET version with API changes
2. **Type mapping changes** - Modified how C# types map to TypeScript
3. **Generation logic changes** - Improved emission (better signatures, additional members)
4. **Bug fixes** - Corrections that change output (missing members, wrong types)

### ❌ Invalid Reasons to Refresh

1. **Tests are failing** - Investigate first, don't blindly refresh
2. **Unknown drift** - Find the root cause before updating baseline
3. **"Making CI green"** - Surface changes must be intentional and reviewed

## How to Refresh Baseline

### Step 1: Investigate the Change

Before refreshing, understand what changed:

```bash
# Run verification to see the diff
bash test/scripts/test-surface-manifest.sh
```

This will show:
- Added/removed files
- Changed files (hash mismatches)
- Generation count changes (namespaces, types, members)

### Step 2: Review the Changes

For changed files, inspect the actual diff:

```bash
# Generate to temp for comparison
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
    generate -d ~/dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.1.25451.107 \
    -o .tests/surface-verify --strict

# Compare specific file
diff .tests/surface-verify/System/index.d.ts \
     <(git show HEAD:.tests/determinism/run1/System/index.d.ts)
```

**Ask yourself**:
- Is this change intentional?
- Does it align with the PR's goals?
- Are there any unexpected consequences?
- Should this be documented in the commit message?

### Step 3: Update Baseline

Once you've reviewed and approved the changes:

```bash
# Capture new baseline
bash test/scripts/capture-baseline.sh

# Review what changed in the manifest
git diff test/baselines/bcl-surface-manifest.json
```

### Step 4: Commit the Update

Include the baseline update in your PR:

```bash
git add test/baselines/bcl-surface-manifest.json
git commit -m "feat: Update surface baseline for <reason>

<Detailed explanation of what changed and why>

Surface changes:
- Added: <list>
- Removed: <list>
- Modified: <list>
"
```

**Important**: The commit message should clearly explain why the surface changed.

## Verification Workflow

The surface manifest is verified in multiple places:

### 1. Local Development

```bash
# Full strict mode test (includes surface verification)
bash test/scripts/test-strict-mode.sh

# Surface verification only
bash test/scripts/test-surface-manifest.sh
```

### 2. CI/CD Pipeline

The strict mode test runs on every PR and ensures:
- Generation succeeds
- Zero errors / zero warnings
- INFO codes unchanged
- **Surface matches baseline**

If the surface verification fails in CI:
1. Review the CI logs to see what changed
2. Pull the branch locally
3. Run `bash test/scripts/test-surface-manifest.sh` to get detailed diff
4. Either fix the unintended change OR update baseline if intentional

## Common Scenarios

### Scenario 1: BCL Version Upgrade

```bash
# Update BCL path in scripts (if needed)
# Run generation manually to verify
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
    generate -d ~/dotnet/shared/Microsoft.NETCore.App/11.0.0 \
    -o .tests/bcl-upgrade --strict

# Verify no unexpected changes
# Update baseline
bash test/scripts/capture-baseline.sh

# Commit with clear message
git add test/baselines/bcl-surface-manifest.json
git commit -m "feat: Update baseline for .NET 11.0.0 BCL

Added 47 new types from System.Threading.RateLimiting
Removed 3 obsolete types from System.Net
Modified 12 signatures for improved nullability
"
```

### Scenario 2: Type Mapping Improvement

```bash
# After implementing type mapping change
# Verify expected impact
bash test/scripts/test-surface-manifest.sh

# Review changes carefully
# Update baseline
bash test/scripts/capture-baseline.sh

# Commit
git add test/baselines/bcl-surface-manifest.json
git commit -m "feat: Improve Task<T> mapping to Promise<T>

Changed all async method return types to use Promise<T>
instead of raw Task<T> generic.

Surface impact: ~1,200 method signatures updated
"
```

### Scenario 3: Unintended Drift Detected

```bash
# Surface verification fails unexpectedly
bash test/scripts/test-surface-manifest.sh

# Output shows unexpected changes:
# Changed Files (hash mismatch):
#   - System.Collections.Generic/index.d.ts

# Investigate the diff
# Find root cause (e.g., nondeterministic ordering bug)
# Fix the bug
# Re-run verification
bash test/scripts/test-surface-manifest.sh

# Should now pass without baseline update
```

## Best Practices

1. **Never blindly refresh** - Always understand what changed
2. **Review diffs manually** - Hash changes don't show what actually changed
3. **Test before updating** - Ensure changes are correct and intentional
4. **Document in commits** - Explain why the surface changed
5. **One baseline per PR** - Don't mix unrelated surface changes

## Troubleshooting

### "Surface regression detected" but I didn't change anything

Possible causes:
1. **Nondeterministic generation** - Run `bash test/scripts/test-determinism.sh`
2. **Environment differences** - Check .NET version, OS, etc.
3. **Uncommitted changes** - Ensure working directory is clean
4. **Baseline out of sync** - Pull latest from main

### Baseline file has merge conflicts

This means multiple PRs changed the surface. To resolve:

1. Accept incoming changes (from main)
2. Re-run your changes
3. Recapture baseline:
   ```bash
   bash test/scripts/capture-baseline.sh
   ```
4. Review and commit the merged baseline

### Want to see human-readable diff

The manifest only stores hashes. To see actual content changes:

```bash
# Generate to temp
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
    generate -d ~/dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.1.25451.107 \
    -o .tests/surface-compare --strict

# Use your preferred diff tool
diff -r .tests/baseline-good .tests/surface-compare
# or
meld .tests/baseline-good .tests/surface-compare
```

---

## Summary

The surface baseline is a **gatekeeper**, not a burden. It ensures:

- **No accidental API drift** - Every change is intentional
- **Reviewable changes** - Surface modifications are explicit in PRs
- **Safe refactoring** - Internal changes don't break public API
- **Audit trail** - Git history shows when and why surface changed

When the surface verification fails: **investigate first, refresh baseline second**.
