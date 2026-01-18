# Testing

tsbindgen includes validation and regression testing to ensure correct TypeScript output.

## Validation

### Running Validation

```bash
# Full BCL validation (2-3 minutes)
node test/validate/validate.js --strict

# Capture output for analysis
node test/validate/validate.js --strict 2>&1 | tee .tests/validation-$(date +%s).txt
```

### What Validation Does

1. Cleans `.tests/validate/`
2. Creates `tsconfig.json` in `.tests/validate/`
3. Runs `tsbindgen generate` against the local .NET runtime
4. Verifies each namespace has `internal/index.d.ts` and `bindings.json`
5. Installs `@tsonic/core` into `.tests/validate/` (requires npm network access)
6. Runs `npx tsc` and writes output to `.tests/tsc-validation.txt`

### Success Criteria

| Criteria | Status |
|----------|--------|
| Zero TypeScript errors | Required when using `--strict` |
| Zero syntax errors (TS1xxx) | Required (always) |
| All assemblies generate | Required |
| All bindings manifests present | Required |

## Regression Tests

Individual test scripts verify specific behaviors.

### Running All Tests

```bash
./test/scripts/run-all.sh
```

### Individual Tests

| Test | Purpose |
|------|---------|
| `test-lib.sh` | Library mode functionality |
| `test-naming-js.sh` | Naming invariants (no casing transforms) |
| `test-strict-mode.sh` | Strict mode validation |
| `test-delegate-callable.sh` | Delegate callable signatures |
| `test-delegate-typescript.sh` | Delegate typing in TS |
| `test-primitive-identity.sh` | Primitive type mappings |
| `test-primitive-lifting.sh` | Primitive type lifting in generic args |
| `test-primitive-constraints.sh` | Primitive constraint invariants |
| `test-multiarity-import.sh` | Multi-arity types import correctly |
| `test-multiarity-no-wrong-export.sh` | Facades don't export wrong arity |
| `test-facade-constraint-invariants.sh` | Constraint invariants validation |
| `test-facade-clean-exports.sh` | Facades don't leak internal types |
| `test-facade-value-exports.sh` | Facade value exports correctness |
| `test-params-rest.sh` | C# params → TS rest parameters |
| `test-cross-module-alias.sh` | Cross-module type aliases |
| `test-surface-manifest.sh` | Surface regression guard |

### Running a Single Test

```bash
./test/scripts/test-lib.sh
```

## Analyzing Errors

### Error Categories

```
TS1xxx - Syntax errors (CRITICAL - must be zero)
TS2xxx - Semantic errors (FIXED in v0.7.4 - now zero)
TS6200 - Duplicate type aliases (expected for primitive type aliases)
```

### Finding Specific Errors

```bash
# Run validation with capture
node test/validate/validate.js 2>&1 | tee .tests/run.txt

# Count errors by type
grep "error TS" .tests/run.txt | sed 's/.*error \(TS[0-9]*\).*/\1/' | sort | uniq -c | sort -rn

# Find specific error examples
grep "TS2417" .tests/run.txt | head -20

# Errors for specific namespace
grep "System.Collections.Generic" .tests/run.txt
```

## Known Semantic Errors

**As of v0.7.4**: The BCL compiles with **zero semantic errors**. All previously known issues have been fixed.

### TS2417 - Property Covariance (FIXED)

C# allows properties to return more specific types than interfaces require. TypeScript doesn't support property overloads.

**Status**: **FIXED** in v0.7.4 - PropertyOverrideUnifier uses union types for covariant properties.

### TS2430 - Interface Method Conflicts (FIXED)

Method signature mismatch between class and inherited interface (e.g., different method overloads).

**Status**: **FIXED** in v0.7.4 - ClassPrinter emits inherited method overloads.

### TS2344 - Constraint Violations (FIXED)

Multi-arity facade type parameters passed to constrained internal types without verification.

**Status**: **FIXED** in v0.7.4 - MultiArityAliasEmit uses nested constraint guards.

### TS6200 - Duplicate Type Aliases

Expected for primitive type aliases that may be declared in multiple namespaces.

**Status**: Harmless, declaration merging handles it.

## Test Output Directories

| Directory | Purpose |
|-----------|---------|
| `.tests/` | Captured validation output (gitignored) |
| `.tests/validate/` | Generated declarations for `validate.js` |
| `test/baselines/` | Surface manifest baselines |

## Surface Manifest Testing

Tracks the public API surface for regression detection.

```bash
# Capture baseline (writes `test/baselines/bcl-surface-manifest.json`)
bash test/scripts/capture-baseline.sh

# Verify current output matches baseline
bash test/scripts/test-surface-manifest.sh
```
