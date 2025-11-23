# Phase 7: PhaseGate (Validation)

## Overview

**PhaseGate** is comprehensive validation layer running **after all transformations** and **before emission**. Enforces 50+ invariants to catch bugs early. Fails fast on invalid state.

**Purpose:**
- Fail fast on errors
- Prevent malformed output
- Document architectural invariants
- Enable safe refactoring

**Integration:** Runs at end of Plan phase, blocks Emit if errors found.

---

## Validation Categories

### 1. Finalization (PG_FIN_001-009)

**Purpose:** Ensure every symbol has final TypeScript name.

**Rules:**
- **PG_FIN_001:** Every type has non-empty `TsEmitName`
- **PG_FIN_002:** Every emitted member has non-empty `TsEmitName`
- **PG_FIN_003:** No member with `EmitScope != Omitted` has empty `TsEmitName`
- **PG_FIN_004:** Every type has rename decision in Renamer
- **PG_FIN_005:** Every emitted member has rename decision in Renamer
- **PG_FIN_006-009:** Reserved for future finalization checks

**Files:** `Plan/Validation/Finalization.cs`

---

### 2. Scope Integrity (PG_SCOPE_001-004)

**Purpose:** Validate scope strings are well-formed.

**Rules:**
- **PG_SCOPE_001:** All scopes have valid format (ns:*, type:*, view:*)
- **PG_SCOPE_002:** Scope kind matches `EmitScope`
  - ClassSurface → type:* scope
  - ViewOnly → view:* scope
- **PG_SCOPE_003:** View scopes reference valid interfaces
- **PG_SCOPE_004:** Type scopes reference valid types

**Files:** `Plan/Validation/Scopes.cs`

---

### 3. Name Uniqueness (PG_NAME_001-005)

**Purpose:** Ensure no duplicate names in same scope.

**Rules:**
- **PG_NAME_001:** No duplicate type names in same namespace
- **PG_NAME_002:** No duplicate ClassSurface member names (same scope)
- **PG_NAME_003:** No duplicate ViewOnly member names (same view)
- **PG_NAME_004:** Static and instance scopes are separate (can have same name)
- **PG_NAME_005:** View scopes are separate from class scope (can have same name)

**Files:** `Plan/Validation/Names.cs`

---

### 4. View Integrity (PG_INT_001-003)

**Purpose:** Validate explicit interface views.

**Rules:**
- **PG_INT_001:** Every `ViewOnly` member belongs to an `ExplicitView`
- **PG_INT_002:** Every `ExplicitView` has at least one member
- **PG_INT_003:** No orphaned views (view with no members)

**Files:** `Plan/Validation/Views.cs`

---

### 5. Import/Export (PG_IMPORT_001, PG_EXPORT_001, PG_API_001-002)

**Purpose:** Validate import/export consistency.

**Rules:**
- **PG_IMPORT_001:** Every foreign type reference has import in ImportPlan
- **PG_EXPORT_001:** Every import references an emitted type (in TypeIndex or built-in)
- **PG_API_001:** Public APIs don't expose internal types
- **PG_API_002:** Exported types are accessible from importing namespace

**Files:** `Plan/Validation/ImportExport.cs`

---

### 6. Type Resolution (PG_LOAD_001, PG_TYPEMAP_001)

**Purpose:** Validate all types can be resolved.

**Rules:**
- **PG_LOAD_001:** All external types are in TypeIndex or built-in primitives
- **PG_TYPEMAP_001:** No unsupported special forms (pointers, byrefs in signatures)

**Files:** `Plan/Validation/Types.cs`

---

### 7. Overload Collision (PG_OL_001-002)

**Purpose:** Detect overload signature collisions.

**Rules:**
- **PG_OL_001:** Overloads with same name and arity don't have conflicting signatures
- **PG_OL_002:** Overloads with different return types are compatible (unified or separate)

**Files:** `Plan/Validation/Core.cs`

---

### 8. Constraint Integrity (PG_CNSTR_001-004)

**Purpose:** Validate generic constraints.

**Rules:**
- **PG_CNSTR_001:** Generic constraints are satisfied by type arguments
- **PG_CNSTR_002:** No impossible constraints (e.g., both struct and class)
- **PG_CNSTR_003:** Constructor constraints match type capabilities
- **PG_CNSTR_004:** Constraint types are resolvable

**Files:** `Plan/Validation/Constraints.cs`

---

### 9. Library Mode (LIB001-003)

**Purpose:** Validate library mode filtering and contract integrity.

**Rules:**
- **LIB001:** Contract directory exists and contains metadata.json and bindings.json files (validated during contract loading)
- **LIB002:** No dangling references - user types don't reference filtered-out types
- **LIB003:** Disabled in library mode (binding consistency validated separately post-emission)

**Files:** `Plan/Validation/LibraryMode.cs`

**Algorithm (LIB002):**
1. Skip if not in library mode
2. For each namespace → type → walk all type references recursively
3. Check base types, interfaces, method params/returns, property/field/event types, generics
4. If reference StableId is in contract but type is not (was filtered) → ERROR

---

## Diagnostic Codes

### Load Phase
- **PG_LOAD_001:** External reference not in candidate set
- **PG_LOAD_002:** Mixed PublicKeyToken for same assembly name
- **PG_LOAD_003:** Version drift for same identity
- **PG_LOAD_004:** Retargetable/ContentType mismatch

### Finalization
- **PG_FIN_001-009:** Finalization checks (names, decisions)

### Scope
- **PG_SCOPE_001-004:** Scope integrity (format, kind, references)

### Name
- **PG_NAME_001-005:** Name uniqueness (namespace, class, view scopes)

### View
- **PG_INT_001-003:** View integrity (membership, non-empty, no orphans)

### Import/Export
- **PG_IMPORT_001:** Missing import
- **PG_EXPORT_001:** Invalid import reference
- **PG_API_001-002:** API visibility

### Type
- **PG_TYPEMAP_001:** Unsupported type form

### Overload
- **PG_OL_001-002:** Overload collisions

### Constraint
- **PG_CNSTR_001-004:** Constraint integrity

### Library Mode
- **LIB001:** Contract files missing/invalid
- **LIB002:** Dangling reference (user type → filtered type)
- **LIB003:** Disabled in library mode

**Total:** 50 diagnostic codes (47 TBG codes + 3 LIB codes)

---

## Error Severity

### ERROR
- Blocks emission
- Sets `BuildResult.Success = false`
- Pipeline stops immediately
- Examples: Missing TsEmitName, duplicate names, orphaned views

### WARNING
- Logged but doesn't block
- Pipeline continues
- Examples: Version drift (non-strict mode), minor inconsistencies

### INFO
- Diagnostic information only
- No impact on build
- Examples: Statistics, progress updates

---

## Output

### Console Log
```
PhaseGate Validation Summary:
  ✓ Finalization: 0 errors
  ✓ Scope Integrity: 0 errors
  ✓ Name Uniqueness: 0 errors
  ✓ View Integrity: 0 errors
  ✓ Import/Export: 0 errors
  ✓ Type Resolution: 0 errors
  ✓ Overload Collision: 0 errors
  ✓ Constraint Integrity: 0 errors

Total: 0 errors, 0 warnings
```

### .diagnostics.txt
Full details of all diagnostics:
```
PG_FIN_001 [ERROR] Type System.String has empty TsEmitName
  Location: System.String
  Reason: Finalization incomplete

PG_NAME_002 [ERROR] Duplicate member name 'ToString' in scope type:System.Decimal#instance
  Location: System.Decimal::ToString
  Conflict: System.Decimal::ToString(IFormatProvider)
```

### validation-summary.json
Machine-readable summary for CI:
```json
{
  "totalErrors": 0,
  "totalWarnings": 0,
  "errorsByCategory": {
    "Finalization": 0,
    "ScopeIntegrity": 0,
    "NameUniqueness": 0,
    "ViewIntegrity": 0,
    "ImportExport": 0,
    "TypeResolution": 0,
    "OverloadCollision": 0,
    "ConstraintIntegrity": 0
  }
}
```

---

## Integration

```csharp
// In Builder.Build (Plan phase)
var constraintFindings = InterfaceConstraintAuditor.Audit(ctx, graph);
PhaseGate.Validate(ctx, graph, importPlan, constraintFindings, libraryContract);

if (ctx.Diagnostics.HasErrors)
{
    ctx.Log("PhaseGate", "Validation failed - aborting emission");
    return new BuildResult
    {
        Success = false,
        Diagnostics = ctx.Diagnostics.GetAll,
        Statistics = graph.GetStatistics
    };
}

// Only reach Emit phase if PhaseGate passes
ctx.Log("PhaseGate", "Validation passed - proceeding to emission");
```

---

## Summary

PhaseGate enforces 50+ validation rules across 9 categories (19 validation modules):
1. **Finalization:** Every symbol has valid TypeScript name
2. **Scope Integrity:** Scopes are well-formed and consistent
3. **Name Uniqueness:** No collisions within same scope
4. **View Integrity:** Explicit views are complete and valid
5. **Import/Export:** Cross-namespace references are valid
6. **Type Resolution:** All type references can be resolved
7. **Overload Collision:** No conflicting overload signatures
8. **Constraint Integrity:** Generic constraints are satisfiable
9. **Library Mode:** Contract integrity and no dangling references

**Benefits:**
- Fail fast on errors (before emission)
- Clear error messages with locations
- Document architectural invariants
- Enable safe refactoring (rules catch breaking changes)
- Machine-readable output for CI

**Critical Rule:** Any ERROR-level diagnostic blocks Emit phase. Build returns Success = false immediately.
