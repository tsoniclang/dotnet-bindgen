# Validation Requirements

This document defines the validation criteria for tsbindgen output.

## Success Criteria

### Zero Syntax Errors (TS1xxx)
All generated TypeScript must be syntactically valid.

**Command**: `tsc --noEmit <output-dir>/**/*.d.ts`

**Requirement**: Zero TS1xxx errors.

**Examples of what must pass**:
- All declarations parse correctly
- Generic syntax is valid
- Import statements are well-formed
- Namespace nesting is correct

### Semantic Errors (TS2xxx)
Semantic errors are acceptable for known .NET/TypeScript impedance mismatches.

**Documented limitations**:
- **TS2417**: Property covariance (~12 errors in BCL)
  - C# allows covariant property overrides
  - TypeScript doesn't support property overloads
  - Status: Expected, documented

- **Generic static members**: Intentionally omitted
  - TypeScript doesn't support `static T DefaultValue` in `class List<T>`
  - Status: Tracked in metadata sidecars

- **Indexers**: Intentionally omitted when overloads conflict
  - Status: Tracked in metadata sidecars

**Requirement**: No unexpected semantic errors. All TS2xxx errors must be documented as known limitations.

### PhaseGate Validation (Single-Phase Pipeline)
PhaseGate guards must pass with zero errors.

**Guards** (all ERROR severity):
- **PG_TYPEMAP_001**: No unmapped unsafe types
- **PG_LOAD_001**: All external type references resolved
- **PG_LOAD_002**: No mixed PublicKeyToken for same assembly name
- **PG_LOAD_003**: Version drift detection
- **PG_PRINT_001**: Printer/Renamer consistency
- **PG_EXPORT_001**: All imports reference exported types
- **PG_API_001/002**: Public API correctness
- **PG_FIN_***: Finalization correctness
- **PG_VIEW_***: View correctness
- **PG_NAME_***: Name collision detection

**Requirement**: Zero PhaseGate errors.

### Completeness Verification
All reflected types must appear in emitted output.

**Command**: `node test/validate/verify-completeness.js`

**Process**:
1. Loads `snapshot.json` (what was reflected/transformed)
2. Loads `typelist.json` (what was emitted)
3. Compares types and members using `tsEmitName` as key
4. Filters intentional omissions (indexers, generic static members)
5. Reports any data loss

**Requirement**: 100% type coverage. Zero unintentional omissions.

## Validation Commands

### Full BCL Validation
```bash
node test/validate/validate.js
```

**Steps**:
1. Cleans `.tests/validation/` directory
2. Generates all BCL namespaces
3. Creates root `index.d.ts` with triple-slash references
4. Creates `tsconfig.json`
5. Runs TypeScript compiler
6. Reports error breakdown

**Output**:
- Generation stats (namespaces, types, members)
- PhaseGate diagnostics
- TypeScript error counts by category
- Success/failure status

### Completeness Check
```bash
node test/validate/verify-completeness.js
```

**Output**:
- Types reflected vs types emitted
- Members reflected vs members emitted
- Intentional omissions summary
- Data loss report (should be zero)

### Unit Tests
```bash
dotnet test
```

**Coverage**: Mapping logic, name transforms, utilities.

## Error Categories

| Code Pattern | Category | Requirement |
|--------------|----------|-------------|
| TS1xxx | Syntax errors | **Must be zero** |
| TS2xxx | Semantic errors | Must be documented |
| TS6200 | Duplicate identifiers | Acceptable (branded types) |
| PG_* | PhaseGate errors | **Must be zero** |

## Validation Output Example

### Successful Run
```
=== Validation Report ===
Namespaces: 132
Types: 4,295
Members: 50,720

PhaseGate: 0 errors ✓
TypeScript: 0 syntax errors ✓
Semantic: 12 errors (TS2417 - documented) ✓
Completeness: 100% coverage ✓

Status: PASSED
```

### Failed Run
```
=== Validation Report ===
Namespaces: 132
Types: 4,295
Members: 50,720

PhaseGate: 3 errors ✗
  PG_TYPEMAP_001: Unmapped pointer type in System.IO.File.ReadAllBytes
  PG_EXPORT_001: Import references non-exported type in System.Linq
  PG_LOAD_001: Missing external type System.Private.CoreLib.String

TypeScript: 5 syntax errors ✗
  TS1005: ';' expected at System/internal/index.d.ts:42
  ...

Status: FAILED
```

## CI Integration

Validation must pass before merging:

```bash
# In CI pipeline
dotnet build
dotnet test
node test/validate/validate.js
node test/validate/verify-completeness.js
```

Exit code 0 = all validation passed
Exit code non-zero = validation failed

## Debugging Failed Validation

### PhaseGate Errors
1. Check error message for diagnostic code (e.g., PG_TYPEMAP_001)
2. Find error context (which type/member/namespace)
3. Examine source assembly for root cause
4. Fix in code generator, not workaround in validation

### TypeScript Syntax Errors
1. Open `.tests/validation/<namespace>/internal/index.d.ts`
2. Find line number from error message
3. Check generated TypeScript syntax
4. Trace back to emitter code
5. Fix emitter, re-run validation

### Completeness Failures
1. Check `verify-completeness.js` output for missing types
2. Compare `snapshot.json` vs `typelist.json`
3. Determine if omission is intentional (indexer, etc.)
4. If intentional: Add to metadata `intentionalOmissions`
5. If unintentional: Fix emitter to include type

## Baseline Metrics

As of last greenfield implementation:

- **Namespaces**: 132
- **Types**: 4,295
- **Members**: 50,720
- **PhaseGate errors**: 0
- **TS syntax errors**: 0
- **TS semantic errors**: 12 (TS2417 - property covariance)
- **Completeness**: 100%
- **Intentional omissions**: 241 indexers

Any deviation from baseline requires explanation and documentation.
