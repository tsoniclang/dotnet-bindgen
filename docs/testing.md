# Testing

tsbindgen includes validation and regression testing to ensure correct TypeScript output.

## Validation

### Running Validation

```bash
# Full BCL validation (2-3 minutes)
node test/validate/validate.js

# Capture output for analysis
node test/validate/validate.js | tee .tests/validation-$(date +%s).txt
```

### What Validation Does

1. Cleans `.tests/validation/` directory
2. Generates all 130 BCL namespaces
3. Creates `index.d.ts` with triple-slash references
4. Creates `tsconfig.json`
5. Runs TypeScript compiler (`tsc`)
6. Reports error breakdown

### Success Criteria

| Criteria | Status |
|----------|--------|
| Zero syntax errors (TS1xxx) | Required |
| Zero semantic errors (TS2xxx) | Required (v0.7.4+) |
| All assemblies generate | Required |
| All metadata files present | Required |

## Completeness Verification

Ensures no types are lost through the pipeline.

```bash
node test/validate/verify-completeness.js
```

### How It Works

1. Loads `snapshot.json` from each namespace (what was reflected)
2. Loads `typelist.json` from each namespace (what was emitted)
3. Compares types and members using `tsEmitName` as key
4. Filters intentional omissions (indexers, etc.)
5. Reports any data loss

### Expected Output

```
VERIFICATION PASSED - ALL REFLECTED DATA ACCOUNTED FOR
- Types in snapshots: 4,047
- Types in typelists: 4,047
- Members verified: 37,863
- Intentional omissions: 241 indexers
```

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
| `test-naming-js.sh` | JavaScript naming convention |
| `test-strict-mode.sh` | Strict mode validation |
| `test-delegate-callable.sh` | Delegate callable signatures |
| `test-primitive-identity.sh` | Primitive type mappings |
| `test-clrof-regression.sh` | CLROf utility type |
| `test-camelcase-regression.sh` | CamelCase conversion |
| `test-multiarity-import.sh` | Multi-arity types import correctly |
| `test-multiarity-no-wrong-export.sh` | Facades don't export wrong arity |
| `test-facade-constraint-invariants.sh` | Constraint invariants validation |
| `test-params-rest.sh` | C# params → TS rest parameters |
| `test-cross-module-alias.sh` | Cross-module type aliases |

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

Method signature mismatch between class and inherited interface (e.g., `char` vs `CLROf<char>`).

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
| `.tests/validation/` | Generated test declarations |
| `test/baselines/` | Surface manifest baselines |

## Surface Manifest Testing

Tracks the public API surface for regression detection.

```bash
# Capture current surface
./test/scripts/capture-surface-manifest.sh

# Compare against baseline
diff test/baselines/surface-manifest.txt .tests/surface-manifest.txt
```

