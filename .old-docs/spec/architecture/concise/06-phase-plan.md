# Phase 6: Plan (Import Planning & Validation)

## Overview

The **Plan phase** builds import graph, plans emission order, combines all plans for emission, and validates everything via PhaseGate.

**Input:**
- SymbolGraph (from Normalize, fully named)
- StaticFlatteningPlan, StaticConflictPlan, OverrideConflictPlan, PropertyOverridePlan, ExtensionMethodsPlan (from Shape)

**Output:** EmissionPlan containing:
- SymbolGraph (unchanged)
- ImportPlan (imports, exports, aliases)
- EmitOrder (deterministic emission order)
- **Shape plans** (passed through for emission)

**Mutability:** Pure (immutable EmissionPlan)

---

## File: ImportGraph.cs

### Purpose
Builds cross-namespace dependency graph. Analyzes TypeReferences to determine which namespaces depend on which other namespaces.

### Method: Build

**Algorithm:**
1. **Initialize collections:**
   - Imports: Namespace → Set<ImportedNamespace>
   - Exports: Namespace → Set<ExportedType>
   - UnresolvedClrKeys: Set<CLR keys not found in graph>

2. **For each namespace:**
   - For each type:
     - Analyze BaseType → add import if cross-namespace
     - Analyze Interfaces → add imports
     - For each member:
       - Analyze ReturnType, ParameterTypes, PropertyType, FieldType → add imports
       - Recursively analyze TypeArguments in generic types

3. **Unresolved tracking:**
   - If TypeReference not found in TypeIndex:
     - Extract CLR key (AssemblyName:FullName)
     - Add to UnresolvedClrKeys
   - Later: DeclaringAssemblyResolver resolves to declaring assembly

4. **Return ImportGraph:**
   - Imports per namespace
   - Exports per namespace
   - UnresolvedClrKeys for cross-assembly resolution

**Files:** `Plan/ImportGraph.cs`

---

## File: ImportPlanner.cs

### Purpose
Plans imports, exports, and aliases for each namespace. Handles auto-aliasing for collision cases.

### Method: PlanImports

**Algorithm:**
1. **For each namespace:**
   - Collect all imports from ImportGraph
   - Determine which need aliases (via DetermineAlias)

2. **Auto-alias detection (via DetermineAlias.cs):**
   - **Local type collision:** If imported type name collides with local type
   - **Cross-import collision:** If same type name imported from multiple namespaces
   - **Alias format:** `TypeName_NamespaceShortName`
     - Example: `AssemblyHashAlgorithm_Assemblies` (type from System.Reflection.Assemblies)

3. **Create ImportPlan:**
   - Map: Namespace → List<ImportDeclaration>
   - Each ImportDeclaration: (SourceNamespace, TypeName, Alias?)

**Files:** `Plan/ImportPlanner.cs`, `Plan/DetermineAlias.cs`

---

## File: DetermineAlias.cs

### Purpose
Implements collision detection and alias generation for imports.

**Algorithm:**

**Step 1: Detect collisions**
1. Build local type name set for current namespace
2. Build imported type name multimap (name → list of source namespaces)
3. For each import:
   - If name in local type set → collision (local type shadowing)
   - If name appears in multiple source namespaces → collision (cross-import)

**Step 2: Generate alias**
- If collision detected:
  - Extract namespace short name (last segment)
  - Format: `{TypeName}_{NamespaceShortName}`
  - Example: `HttpRequestCachePolicy` from `System.Net.Cache` → `HttpRequestCachePolicy_Cache`
- If no collision:
  - No alias needed (use original name)

**Files:** `Plan/DetermineAlias.cs`

---

## File: EmitOrderPlanner.cs

### Purpose
Determines stable emission order for namespaces.

**Algorithm:**
1. Sort namespaces alphabetically by name
2. Ensures deterministic output order
3. Returns ordered list of namespace names

**Files:** `Plan/EmitOrderPlanner.cs`

---

## File: StaticOverloadPlan.cs

### Purpose
Plan structure for static overload handling (from Shape pass 4.7).

### Record: StaticFlatteningPlan

**Purpose:** Maps static-only types to inherited static members.

**Structure:**
```csharp
Map: StaticOnlyTypeStableId → List<(MemberStableId, DeclaringTypeStableId)>
```

**Usage in Emit:**
- ClassPrinter checks if type in plan
- If yes: Emit inherited static members from plan
- Each member tracks declaring type for source comments

**Example:**
- `Vector128<T>` → List of static members from base `Vector128`

**Files:** `Plan/StaticOverloadPlan.cs`

---

## File: OverrideConflictPlan.cs

### Purpose
Plan structures for override conflict suppression (from Shape passes 4.8-4.9).

### Record: StaticConflictPlan

**Purpose:** Maps hybrid types to conflicting static member names.

**Structure:**
```csharp
Map: HybridTypeStableId → Set<ConflictingStaticMemberName>
```

**Usage in Emit:**
- ClassPrinter checks if member name in conflict set for type
- If yes: Suppress emission (avoid TS2417 error)

**Example:**
- `Task<T>` → Set containing "Factory" (shadows Task.Factory)

### Record: OverrideConflictPlan

**Purpose:** Maps derived types to conflicting instance member names.

**Structure:**
```csharp
Map: DerivedTypeStableId → Set<ConflictingInstanceMemberName>
```

**Usage in Emit:**
- ClassPrinter checks if member name in conflict set for type
- If yes: Suppress emission (avoid TS2416 error)

**Files:** `Plan/OverrideConflictPlan.cs`

---

## File: PropertyOverridePlan.cs

### Purpose
Plan structure for property type unification (from Shape pass 4.10).

### Record: PropertyOverridePlan

**Purpose:** Maps properties to unified union type strings.

**Structure:**
```csharp
Map: PropertyStableId → UnifiedUnionTypeString
```

**Entry format:**
- Key: Property StableId (e.g., `"System.Net.HttpRequestCachePolicy::level"`)
- Value: Union type string (e.g., `"HttpRequestCacheLevel | HttpCacheAgeControl"`)

**Usage in Emit:**
- ClassPrinter checks if property in plan
- If yes: Emit union type instead of original property type
- Eliminates TS2416 errors from property type variance

**Example:**
- Property `level` with varying types across hierarchy
- Emitted as: `level: HttpRequestCacheLevel | HttpCacheAgeControl`

**Files:** `Plan/PropertyOverridePlan.cs`

---

## File: EmissionPlan.cs

### Purpose
Composite plan containing all data needed for emission.

### Record: EmissionPlan

**Properties:**
- **`Graph: SymbolGraph`** - Fully named symbol graph
- **`ImportPlan: ImportPlan`** - Imports, exports, aliases
- **`EmitOrder: List<string>`** - Deterministic namespace order
- **`StaticFlatteningPlan: StaticFlatteningPlan`** - Static hierarchy flattening (Pass 4.7)
- **`StaticConflictPlan: StaticConflictPlan`** - Static conflict suppression (Pass 4.8)
- **`OverrideConflictPlan: OverrideConflictPlan`** - Override conflict suppression (Pass 4.9)
- **`PropertyOverridePlan: PropertyOverridePlan`** - Property type unification (Pass 4.10)
- **`ExtensionMethodsPlan: ExtensionMethodsPlan`** - Extension method bucket grouping (Pass 4.11)

**Created by:** Builder.PlanPhase

**Used by:** Builder.EmitPhase (passed to all emitters)

**Files:** `Builder.cs` (EmissionPlan record)

---

## File: OverloadUnifier.cs

### Purpose
Unifies method overloads (merges signatures into single overloaded declaration).

**Algorithm:**
1. For each type
2. Group methods by name and emit scope
3. Merge overloads with compatible return types
4. Preserve distinct overloads for different return types

**Files:** `Plan/OverloadUnifier.cs`

---

## File: InterfaceConstraintAuditor.cs

### Purpose
Audits constructor constraints per (Type, Interface) pair.

**Algorithm:**
1. For each (Type, Interface) pair
2. Check if interface has constructor constraint (new T())
3. Check if type has public parameterless constructor
4. Record findings for PhaseGate validation

**Output:** ConstraintFindings (audit results)

**Files:** `Plan/InterfaceConstraintAuditor.cs`

---

## File: PhaseGate.cs

### Purpose
Pre-emission validation. Enforces 50+ invariants. Fails fast on errors.

**Validation Categories:**
1. **Finalization** (PG_FIN_001-009): Every symbol has final TS name
2. **Scope Integrity** (PG_SCOPE_001-004): Well-formed scopes
3. **Name Uniqueness** (PG_NAME_001-005): No duplicates in same scope
4. **View Integrity** (PG_INT_001-003): ViewOnly members belong to views
5. **Import/Export** (PG_IMPORT_001, PG_EXPORT_001, PG_API_001-002): Valid imports
6. **Type Resolution** (PG_LOAD_001, PG_TYPEMAP_001): All types resolvable
7. **Overload Collision** (PG_OL_001-002): No overload conflicts
8. **Constraint Integrity** (PG_CNSTR_001-004): Constraints satisfied

**Error Severity:**
- **ERROR:** Blocks emission, BuildResult.Success = false
- **WARNING:** Logged but doesn't block
- **INFO:** Diagnostic information

**Output:**
- Console log with error summary
- `.diagnostics.txt` file with full details
- `validation-summary.json` for CI comparison

**Integration:**
```csharp
PhaseGate.Validate(ctx, graph, imports, constraintFindings);
if (ctx.Diagnostics.HasErrors)
    return new BuildResult { Success = false };
```

**Files:** `Plan/PhaseGate.cs`, `Plan/Validation/*.cs` (26 validators)

---

## Summary

The Plan phase prepares everything for emission:
1. **ImportGraph:** Analyzes cross-namespace dependencies
2. **ImportPlanner:** Plans imports with auto-aliasing for collisions
3. **DetermineAlias:** Detects collisions, generates aliases
4. **EmitOrderPlanner:** Stable deterministic order
5. **OverloadUnifier:** Merges method overloads
6. **InterfaceConstraintAuditor:** Audits constructor constraints
7. **PhaseGate:** Validates entire pipeline output (50+ rules)
8. **EmissionPlan:** Combines all data for Emit phase

**Shape Plans Integration:**
- StaticFlatteningPlan (Pass 4.7)
- StaticConflictPlan (Pass 4.8)
- OverrideConflictPlan (Pass 4.9)
- PropertyOverridePlan (Pass 4.10)

All plans passed through EmissionPlan to Emit phase for plan-based emission.

**Critical Rule:** Any ERROR-level diagnostic blocks Emit phase. Build returns Success = false.
