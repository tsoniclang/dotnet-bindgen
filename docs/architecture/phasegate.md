# Phase Gate

## What is a Phase Gate?

A **phase gate** (also called stage gate) is a quality checkpoint between phases in a pipeline. The gate validates that all criteria are satisfied before allowing progression to the next phase. If validation fails, the gate blocks further progress until issues are resolved.

In tsbindgen, the Phase Gate sits between the Plan and Emit phases:

```
Load -> Model -> Shape -> Normalize -> Plan -> [PHASE GATE] -> Emit
```

The Phase Gate validates that the SymbolGraph satisfies all invariants required for correct TypeScript emission. If any validation fails with ERROR severity, emission is blocked.

## Why Phase Gate Matters

Without Phase Gate validation, invalid states could produce:
- Invalid TypeScript syntax (caught by tsc, but late)
- Missing imports (TS2304 "Cannot find name")
- Duplicate declarations (TS2300)
- Type mismatches (TS2322)
- Circular dependencies

Phase Gate catches these issues at planning time with clear diagnostics, before any files are written.

## Validation Modules

Phase Gate runs validation through multiple specialized modules:

| Module | Validates |
|--------|-----------|
| `Core` | Type names, member names, generics, inheritance |
| `Names` | Final names, aliases, identifiers, collisions |
| `Views` | View integrity, member scoping, As_ properties |
| `Types` | Type references, printer consistency, special forms |
| `Scopes` | EmitScope invariants, scope key validity |
| `Finalization` | All symbols have required metadata |
| `ImportExport` | Import completeness, export correctness |
| `Constraints` | Generic constraint compatibility |
| `PlanIntegrity` | Shape plans reference valid symbols |
| `Extensions` | Extension method bucket validity |
| `LibraryMode` | Contract validation (when --lib used) |

## Diagnostic Codes

All diagnostics use prefix `TBG` (tsbindgen) with 3-digit codes. Severity (ERROR/WARNING/INFO) is separate from the code.

### 0xx - Resolution / Binding

| Code | Name | Description |
|------|------|-------------|
| TBG001 | UnresolvedType | Type reference cannot be resolved |
| TBG002 | UnresolvedGenericParameter | Generic parameter not found |
| TBG003 | UnresolvedConstraint | Constraint type cannot be resolved |

### 1xx - Naming / Conflicts

| Code | Name | Description |
|------|------|-------------|
| TBG100 | NameConflictUnresolved | Name conflict not resolved by renaming |
| TBG101 | AmbiguousOverload | Multiple overloads with same erased signature |
| TBG102 | DuplicateMember | Duplicate member in same scope |
| TBG103 | ViewMemberCollisionInViewScope | View member collides within view |
| TBG104 | ViewMemberEqualsClassSurface | View member shadows class member |
| TBG105 | DuplicatePropertyNamePostDedup | Duplicate property after deduplication |
| TBG120 | ReservedWordUnsanitized | TypeScript reserved word not escaped |

### 2xx - Hierarchy / Conformance

| Code | Name | Description |
|------|------|-------------|
| TBG200 | DiamondInheritance | Diamond inheritance detected |
| TBG201 | CircularInheritance | Circular inheritance/dependency |
| TBG202 | InterfaceNotFound | Referenced interface not in graph |
| TBG203 | StructuralConformanceFailure | Type doesn't structurally conform to interface |
| TBG204 | StaticSideInheritanceIssue | Static side inheritance problem |
| TBG205 | InterfaceMethodNotAssignable | Method signature incompatible with interface |
| TBG211 | OverloadUnified | Overloads unified (INFO) |
| TBG212 | OverloadUnresolvable | Overload conflict unresolvable |
| TBG213 | DuplicateErasedSurfaceSignature | Duplicate signature after type erasure |

### 3xx - TypeScript Compatibility

| Code | Name | Description |
|------|------|-------------|
| TBG300 | PropertyCovarianceUnsupported | Property covariance not supported in TS |
| TBG301 | StaticSideVariance | Static side variance issue |
| TBG302 | IndexerConflict | Indexer overload conflict |
| TBG310 | CovarianceSummary | Covariance summary (INFO) |

### 4xx - Policy / Constraints

| Code | Name | Description |
|------|------|-------------|
| TBG400 | PolicyViolation | Generation policy violated |
| TBG401 | UnsatisfiableConstraint | Constraint cannot be satisfied |
| TBG402 | UnsupportedConstraintMerge | Constraints cannot be merged |
| TBG403 | IncompatibleConstraints | Constraints are incompatible |
| TBG404 | UnrepresentableConstraint | Constraint not representable in TS |
| TBG405 | ValidationFailed | Phase Gate validation failed |
| TBG406 | NonBenignConstraintLoss | Non-benign constraint loss |
| TBG407 | ConstructorConstraintLoss | Constructor constraint lost |
| TBG410 | ConstraintNarrowing | Constraint narrowed (WARNING) |

### 5xx - Renaming / Views

| Code | Name | Description |
|------|------|-------------|
| TBG500 | RenameConflict | Rename operation conflicted |
| TBG501 | ExplicitOverrideNotApplied | Explicit override not applied |
| TBG510 | ViewCoverageMismatch | View coverage doesn't match interface |
| TBG511 | EmptyView | View has no members |
| TBG512 | DuplicateViewForInterface | Duplicate view for same interface |
| TBG513 | InvalidViewPropertyName | Invalid As_ property name |
| TBG530 | TypeNamePrinterRenamerMismatch | Printer and Renamer disagree on name |

### 6xx - Metadata / Binding

| Code | Name | Description |
|------|------|-------------|
| TBG600 | MissingMetadataToken | CLR metadata token missing |
| TBG601 | BindingAmbiguity | Binding is ambiguous |

### 7xx - Finalization / Scopes

| Code | Name | Description |
|------|------|-------------|
| TBG702 | MemberInBothClassAndView | Member in both ClassSurface and ViewOnly |
| TBG703 | ClassSurfaceMemberHasSourceInterface | ClassSurface member has SourceInterface set |
| TBG710 | MissingEmitScopeOrIllegalCombo | EmitScope missing or invalid combination |
| TBG711 | ViewOnlyWithoutExactlyOneExplicitView | ViewOnly member not in exactly one view |
| TBG712 | EmittingMemberMissingFinalName | Member missing final name in scope |
| TBG713 | EmittingTypeMissingFinalName | Type missing final name in namespace |
| TBG714 | InvalidOrEmptyViewMembership | Invalid view membership |
| TBG715 | DuplicateViewMembership | Duplicate view membership |
| TBG716 | ClassViewDualRoleClash | Class/View dual-role conflict |
| TBG717 | RequiredViewMissingForInterface | Required view missing |
| TBG718 | PostSanitizerUnsanitizedIdentifier | Identifier not sanitized |
| TBG719 | PostSanitizerUnsanitizedReservedIdentifier | Reserved word not sanitized |
| TBG720 | MalformedScopeKey | Scope key is malformed |
| TBG721 | ScopeKindMismatchWithEmitScope | Scope kind doesn't match EmitScope |

### 8xx - Import/Export / TypeMap

| Code | Name | Description |
|------|------|-------------|
| TBG850 | MissingImportForForeignType | Foreign type used without import |
| TBG851 | ImportedTypeNotExported | Imported type not exported by source |
| TBG852 | InvalidImportModulePath | Invalid module path in import |
| TBG853 | FacadeImportsMustUseInternalIndex | Facade must import from internal |
| TBG854 | HeritageTypeOnlyImport | Heritage clause needs value import |
| TBG855 | QualifiedExportPathInvalid | Qualified export path invalid |
| TBG856 | TypeReferenceUnresolvable | Type reference cannot be resolved |
| TBG857 | GenericArityInconsistent | Generic arity mismatch |
| TBG860 | PublicApiReferencesNonEmittedType | Public API exposes non-emitted type |
| TBG861 | GenericConstraintReferencesNonEmittedType | Constraint references non-emitted type |
| TBG862 | PublicApiReferencesNonPublicType | Public API exposes non-public type |
| TBG870 | UnsupportedClrSpecialForm | Unsupported CLR form (pointer/byref/fnptr) |
| TBG8A1 | SurfaceNamePolicyMismatch | Surface name doesn't match policy |
| TBG8A2 | NumericSuffixOnSurface | Numeric suffix on surface member |
| TBG8P1 | PrimitiveGenericLiftMismatch | Primitive not covered by CLROf |

### 9xx - Assembly / Plans / Extensions

| Code | Name | Description |
|------|------|-------------|
| TBG880 | UnresolvedExternalType | External type not resolved |
| TBG881 | MixedPublicKeyTokenForSameName | Mixed PublicKeyToken for assembly |
| TBG882 | VersionDriftForSameIdentity | Version drift for same assembly |
| TBG883 | RetargetableOrContentTypeAssemblyRef | Retargetable assembly reference |
| TBG900 | StaticFlatteningPlanInvalid | Static flattening plan invalid |
| TBG901 | StaticConflictPlanInvalid | Static conflict plan invalid |
| TBG902 | OverrideConflictPlanInvalid | Override conflict plan invalid |
| TBG903 | PropertyOverridePlanInvalid | Property override plan invalid |
| TBG904 | ExtensionMethodsPlanInvalid | Extension methods plan invalid |
| TBG905 | ExtensionMethodErasedAnyType | Extension method has erased 'any' |
| TBG906 | ExtensionBucketNameInvalid | Extension bucket name invalid |
| TBG907 | ExtensionImportUnresolved | Extension import unresolved |

### Library Mode (LIB)

| Code | Name | Description |
|------|------|-------------|
| LIB001 | ContractLoadFailed | Failed to load library contract |
| LIB002 | DanglingReference | Reference to filtered type |
| LIB003 | SignatureMismatch | Signature differs from contract |

## Severity Levels

- **ERROR**: Blocks emission. Must be fixed before proceeding.
- **WARNING**: Emission continues but may cause TypeScript errors.
- **INFO**: Informational only. No action required.

## Strict Mode

When `--strict` is enabled, non-whitelisted warnings are promoted to errors:

```bash
dotnet run -- generate -d $DOTNET_RUNTIME -o ./out --strict
```

Strict mode ensures zero warnings in output. Certain warnings are whitelisted (documented, expected limitations like property covariance).

## Diagnostic Output

Phase Gate produces a summary table:

```
=== PhaseGate Validation ===
Errors: 0
Warnings: 2

Diagnostic Summary by Code:
-------------------------------------
  TBG310:    12 - Property covariance (TS limitation)
  TBG211:   847 - Overloads unified
-------------------------------------
```

## Debugging Failures

1. Find the diagnostic code in output (e.g., TBG712)
2. Look up the code in the table above
3. Find the type/member mentioned in the message
4. Trace back to the Shape/Normalize pass responsible
5. Fix the pass logic, re-run

**Common causes:**
- Missing Shape pass for edge case
- Normalize didn't reserve name in correct scope
- Type reference to assembly not in load set
- EmitScope not set during Shape passes

