# Pipeline Flow: Sequential Phase Execution

## Overview

The tsbindgen pipeline executes in **strict sequential order** through 5 main phases with 8 sub-phases. Each phase is **pure** (returns new immutable data) except Phase 5 (Emit) which has file I/O side effects.

**Entry Point**: `Builder.Build` in `src/tsbindgen/Builder.cs`

**Execution Order**:
```
1. BuildContext.Create
2. PHASE 1: LOAD
3. PHASE 2: NORMALIZE (Build Indices)
4. PHASE 3: SHAPE (23 transformation passes)
5. PHASE 3.5: NAME RESERVATION
6. PHASE 4: PLAN
7. PHASE 4.5: OVERLOAD UNIFICATION
8. PHASE 4.6: INTERFACE CONSTRAINT AUDIT
9. PHASE 4.7: PHASEGATE VALIDATION
10. PHASE 5: EMIT (if no errors)
```

---

## Phase 1: LOAD

**Purpose**: Reflect over .NET assemblies to build initial SymbolGraph

**Input**: `string[]` assemblyPaths
**Output**: `SymbolGraph` (pure CLR facts, no TypeScript concepts)
**Mutability**: Pure (immutable SymbolGraph)

**Operations**:
1. Create `MetadataLoadContext` with reference paths
2. Load transitive closure (seed + dependencies)
3. Reflect all types/members via `ReflectionReader.ReadAssemblies`
4. Substitute closed generic interface members (`InterfaceMemberSubstitution`)
5. Build SymbolGraph: Namespaces → Types → Members + TypeReferences

**Files**: `Load/AssemblyLoader.cs`, `Load/ReflectionReader.cs`, `Load/InterfaceMemberSubstitution.cs`

**Output State**: Pure CLR metadata, `TsEmitName = null`, `EmitScope` undetermined, interfaces not flattened.

---

## Phase 2: NORMALIZE (Build Indices)

**Purpose**: Build lookup tables for efficient cross-reference resolution + optional library mode filtering

**Input**: `SymbolGraph` (from Phase 1)
**Output**: `SymbolGraph` (with indices, optionally filtered)
**Mutability**: Pure (new SymbolGraph with indices)

**Operations**:
1. `graph.WithIndices` → `NamespaceIndex`, `TypeIndex` (includes nested types)
2. **[Library Mode Only]** Load contract: `LibraryContractLoader.Load(libraryPath)` → `LibraryContract` (HashSet of StableIds from base package)
3. **[Library Mode Only]** Filter graph: `LibraryFilter.Filter(graph, contract)` → Remove types in contract
4. Build `GlobalInterfaceIndex` (interface inheritance)
5. Build `InterfaceDeclIndex` (interface member declarations)

**Files**: `Model/SymbolGraph.cs` (`WithIndices`), `Library/LibraryContractLoader.cs`, `Library/LibraryFilter.cs`, `Shape/GlobalInterfaceIndex.cs`, `Shape/InterfaceDeclIndex.cs`

**Library Mode**: Validates LIB001 (contract files exist) during contract loading.

---

## Phase 3: SHAPE (23 Transformation Passes)

**Purpose**: Transform CLR semantics → TypeScript semantics + create compatibility plans

**Input**: `SymbolGraph` (from Phase 2, with indices)
**Output**:
- `SymbolGraph` (TypeScript-ready, `TsEmitName` still null)
- `StaticFlatteningPlan` (pass 4.7)
- `StaticConflictPlan` (pass 4.8)
- `OverrideConflictPlan` (pass 4.9)
- `PropertyOverridePlan` (pass 4.10)

**Mutability**: Pure (each pass returns new SymbolGraph or plan)

**Transformations**: Interface flattening, explicit interface synthesis, diamond resolution, overload handling, deduplication, EmitScope determination, static hierarchy flattening, conflict detection, property type unification.

### 23 Shape Passes (Sequential Order)

#### Pass 1: GlobalInterfaceIndex.Build
- **Purpose**: Global interface inheritance lookup
- **Output**: Side effect in BuildContext
- **Files**: `Shape/GlobalInterfaceIndex.cs`

#### Pass 2: InterfaceDeclIndex.Build
- **Purpose**: Interface member declaration lookup
- **Output**: Side effect in BuildContext
- **Files**: `Shape/InterfaceDeclIndex.cs`

#### Pass 3: StructuralConformance.Analyze
- **Purpose**: Synthesize ViewOnly members for structural conformance
- **Output**: SymbolGraph (with ViewOnly members)
- **Files**: `Shape/StructuralConformance.cs`
- **Key**: BEFORE InterfaceInliner (needs original hierarchy)

#### Pass 4: InterfaceInliner.Inline
- **Purpose**: Flatten interface hierarchies (copy inherited members)
- **Output**: SymbolGraph (flattened interfaces)
- **Files**: `Shape/InterfaceInliner.cs`
- **Key**: AFTER indices and conformance

#### Pass 5: ExplicitImplSynthesizer.Synthesize
- **Purpose**: Synthesize ViewOnly for explicit interface impls
- **Output**: SymbolGraph (with explicit impl ViewOnly)
- **Files**: `Shape/ExplicitImplSynthesizer.cs`

#### Pass 6: DiamondResolver.Resolve
- **Purpose**: Resolve diamond inheritance
- **Output**: SymbolGraph (diamond conflicts resolved)
- **Files**: `Shape/DiamondResolver.cs`

#### Pass 7: BaseOverloadAdder.AddOverloads
- **Purpose**: Add base class method overloads for interface compatibility
- **Output**: SymbolGraph (with base overloads)
- **Files**: `Shape/BaseOverloadAdder.cs`
- **Note**: Updated to use topological sort, walk full hierarchy

#### Pass 8: StaticSideAnalyzer.Analyze
- **Purpose**: Analyze static members/constructors
- **Output**: Side effect in BuildContext
- **Files**: `Shape/StaticSideAnalyzer.cs`

#### Pass 9: IndexerPlanner.Plan
- **Purpose**: Mark indexers for omission (TypeScript limitation)
- **Output**: SymbolGraph (indexers marked)
- **Files**: `Shape/IndexerPlanner.cs`

#### Pass 10: HiddenMemberPlanner.Plan
- **Purpose**: Handle C# 'new' keyword hiding (rename hidden members)
- **Output**: Side effect (rename decisions)
- **Files**: `Shape/HiddenMemberPlanner.cs`

#### Pass 11: FinalIndexersPass.Run
- **Purpose**: Remove leaked indexer properties
- **Output**: SymbolGraph (indexers removed)
- **Files**: `Shape/FinalIndexersPass.cs`

#### Pass 12: ClassSurfaceDeduplicator.Deduplicate
- **Purpose**: Resolve name collisions on class surface (demote to ViewOnly)
- **Output**: SymbolGraph (duplicates demoted)
- **Files**: `Shape/ClassSurfaceDeduplicator.cs`

#### Pass 13: ConstraintCloser.Close
- **Purpose**: Complete generic constraint closures
- **Output**: SymbolGraph (constraints closed)
- **Files**: `Shape/ConstraintCloser.cs`

#### Pass 14: OverloadReturnConflictResolver.Resolve
- **Purpose**: Resolve method overloads with conflicting return types
- **Output**: SymbolGraph (return conflicts resolved)
- **Files**: `Shape/OverloadReturnConflictResolver.cs`

#### Pass 15: ViewPlanner.Plan
- **Purpose**: Plan explicit interface views (one interface per view)
- **Output**: SymbolGraph (views planned)
- **Files**: `Shape/ViewPlanner.cs`

#### Pass 16: MemberDeduplicator.Deduplicate
- **Purpose**: Remove duplicate members from Shape passes
- **Output**: SymbolGraph (final deduplication)
- **Files**: `Shape/MemberDeduplicator.cs`

#### Pass 17: StaticSideAnalyzer.Analyze (Legacy)
- **Purpose**: Analyze static conflicts (superseded by 4.7-4.8)
- **Output**: SymbolGraph (unchanged)
- **Note**: Kept for compatibility

#### Pass 18: ConstraintCloser.Close
- **Purpose**: Complete generic constraint closures
- **Output**: SymbolGraph (constraints closed)
- **Files**: `Shape/ConstraintCloser.cs`

#### Pass 4.7 (19): StaticHierarchyFlattener.Build
- **Purpose**: Plan flattening for static-only inheritance hierarchies
- **Input**: SymbolGraph
- **Output**: `(SymbolGraph, StaticFlatteningPlan)`
- **Files**: `Shape/StaticHierarchyFlattener.cs`
- **Algorithm**:
  1. Identify static-only types (no instance members, no base with instance)
  2. Collect inherited static members from full hierarchy (recursive walk)
  3. Create flattening plan: Type → List of inherited static members
- **Impact**: Eliminates TS2417 for SIMD intrinsics (~50 types)
- **Example**: `Vector128<T>` inherits static members from base `Vector128`

#### Pass 4.8 (20): StaticConflictDetector.Build
- **Purpose**: Detect static member conflicts in hybrid types (static + instance)
- **Input**: SymbolGraph
- **Output**: `StaticConflictPlan`
- **Files**: `Shape/StaticConflictDetector.cs`
- **Algorithm**:
  1. Identify hybrid types (both static and instance members)
  2. Find static properties/methods/fields shadowing base class statics
  3. Create conflict plan: Type → Set of conflicting static member names
- **Impact**: Eliminates TS2417 for Task<T> (~4 types)
- **Example**: `Task<T>.Factory` shadows `Task.Factory`

#### Pass 4.9 (21): OverrideConflictDetector.Build
- **Purpose**: Detect instance member override conflicts (same-assembly only)
- **Input**: SymbolGraph
- **Output**: `OverrideConflictPlan`
- **Files**: `Shape/OverrideConflictDetector.cs`
- **Algorithm**:
  1. For each type, walk base hierarchy (same assembly only)
  2. Find properties/methods with incompatible signatures vs base
  3. Create conflict plan: Type → Set of conflicting instance member names
- **Impact**: Reduced TS2416 by 44% (same-assembly cases)
- **Limitation**: Only detects when both base/derived in same SymbolGraph

#### Pass 4.10 (22): PropertyOverrideUnifier.Build
- **Purpose**: Unify property types across inheritance hierarchies via union types
- **Input**: SymbolGraph
- **Output**: `PropertyOverridePlan`
- **Files**: `Shape/PropertyOverrideUnifier.cs`
- **Algorithm**:
  1. For each type, walk full inheritance chain (cross-assembly via lookup by `ClrFullName`)
  2. Group properties by name across hierarchy
  3. For properties with type variance, compute union type: `Type1 | Type2 | ...`
  4. Filter out properties with generic type parameters (T, TKey, TValue, etc. → causes TS2304)
  5. Create plan: Property → unified union type string
- **Impact**: Eliminated final TS2416 error → **zero TypeScript errors**
- **Statistics**: 222 property chains unified, 444 union entries created
- **Safety**: Generic filter prevents TS2304 errors from leaked type parameters

#### Pass 4.11 (23): ExtensionMethodAnalyzer.Analyze
- **Purpose**: Group C# extension methods by target type for emission as TypeScript bucket interfaces
- **Input**: SymbolGraph
- **Output**: `ExtensionMethodsPlan`
- **Files**: `Analysis/ExtensionMethodAnalyzer.cs`, `Analysis/ExtensionBucketPlan.cs`
- **Algorithm**:
  1. Collect all methods with `IsExtensionMethod = true`
  2. Group by `(ExtensionTarget.FullName, ExtensionTarget.Arity)` key
  3. Build bucket plans with target type and methods
- **Impact**: 122 bucket interfaces, 1,759 extension methods (BCL .NET 9)
- **Emission**: ExtensionsEmitter generates `internal/extensions/index.d.ts` with bucket interfaces and `ExtensionMethods<TShape>` helper type

**Output State After Shape**:
- All members have `EmitScope` (ClassSurface or ViewOnly)
- All transformations complete
- `TsEmitName` still null (assigned in Phase 3.5)
- **Plans created**:
  - `StaticFlatteningPlan`: Static-only types → inherited members
  - `StaticConflictPlan`: Hybrid types → conflicting statics
  - `OverrideConflictPlan`: Derived types → incompatible overrides
  - `PropertyOverridePlan`: Properties → unified union types
  - `ExtensionMethodsPlan`: Extension methods → bucket interfaces by target type

---

## Phase 3.5: NAME RESERVATION

**Purpose**: Assign all TypeScript names via central Renamer

**Input**: `SymbolGraph` (from Phase 3, TypeScript-ready, unnamed)
**Output**: `SymbolGraph` (with `TsEmitName` assigned)
**Mutability**: Side effect (Renamer) + pure (new SymbolGraph)

**Operations**:
1. For each type: Apply transforms, reserve via `Renamer.ReserveTypeName`
2. For each member:
   - Skip already renamed (HiddenMemberPlanner, IndexerPlanner)
   - Apply syntax transforms (`` ` `` → `_`, `+` → `_`)
   - Sanitize reserved words (add `_` suffix)
   - Reserve via `Renamer.ReserveMemberName` with correct scope
3. Audit completeness (fail if any emitted member lacks rename decision)
4. Apply names to graph (`Application.ApplyNamesToGraph`)

**Files**: `Normalize/NameReservation.cs`, `Normalize/Naming/*`

**Scopes**:
- Types: `ScopeFactory.Namespace(ns, NamespaceArea.Internal)`
- Class members: `ScopeFactory.ClassSurface(type, isStatic)`
- View members: `ScopeFactory.View(type, interfaceStableId)`

**State Change**: `TsEmitName = null` → `TsEmitName` assigned for all emitted symbols

---

## Phase 4: PLAN

**Purpose**: Build import graph, plan emission order, combine all plans

**Input**:
- `SymbolGraph` (from Phase 3.5, fully named)
- `StaticFlatteningPlan`, `StaticConflictPlan`, `OverrideConflictPlan`, `PropertyOverridePlan` (from Shape)

**Output**: `EmissionPlan` containing:
- `SymbolGraph` (unchanged)
- `ImportPlan` (imports, exports, aliases)
- `EmitOrder` (deterministic order)
- **Shape plans** (passed through for emission):
  - `StaticFlatteningPlan`
  - `StaticConflictPlan`
  - `OverrideConflictPlan`
  - `PropertyOverridePlan`

**Mutability**: Pure (immutable EmissionPlan)

**Operations**:
1. Build `ImportGraph` (cross-namespace dependencies)
2. Plan imports/exports via `ImportPlanner.PlanImports`
   - **Auto-alias detection**: Check local type collisions, cross-import collisions
   - **Alias format**: `TypeName_NamespaceShortName` (e.g., `AssemblyHashAlgorithm_Assemblies`)
   - **DetermineAlias.cs**: Implements collision detection and alias generation
3. Determine stable emission order via `EmitOrderPlanner.PlanOrder`
4. Combine all plans into `EmissionPlan`

**Files**: `Plan/ImportGraph.cs`, `Plan/ImportPlanner.cs`, `Plan/DetermineAlias.cs`, `Plan/EmitOrderPlanner.cs`, `Builder.cs` (EmissionPlan record)

---

## Phase 4.5: OVERLOAD UNIFICATION

**Purpose**: Unify method overloads (merge signatures)

**Input**: `SymbolGraph` (from Phase 4)
**Output**: `SymbolGraph` (overloads unified)
**Mutability**: Pure

**Operations**: Group methods by name/emit scope, merge overloads with compatible return types.

**Files**: `Plan/OverloadUnifier.cs`

---

## Phase 4.6: INTERFACE CONSTRAINT AUDIT

**Purpose**: Audit constructor constraints per (Type, Interface) pair

**Input**: `SymbolGraph` (from Phase 4.5)
**Output**: `ConstraintFindings` (audit results)
**Mutability**: Pure

**Operations**: Check each (Type, Interface) pair for constructor constraints, record findings for PhaseGate.

**Files**: `Plan/InterfaceConstraintAuditor.cs`

---

## Phase 4.7: PHASEGATE VALIDATION

**Purpose**: Validate entire pipeline output before emission

**Input**: `SymbolGraph`, `ImportPlan`, `ConstraintFindings`, `LibraryContract?`
**Output**: Side effect (records diagnostics in `BuildContext.Diagnostics`)
**Mutability**: Side effect only

**Operations**: Run 50+ validation checks (Finalization, Scopes, Names, Views, Imports, Types, Overloads, Constraints, Library Mode), record ERROR/WARNING/INFO diagnostics, fail fast on ERROR.

**Files**: `Plan/PhaseGate.cs`, `Plan/Validation/*.cs` (19 validators including LibraryMode.cs)

**Library Mode Validation**: LIB002 (dangling references - user types don't reference filtered types), LIB003 (disabled in library mode).

**Critical Rule**: Any ERROR blocks Phase 5 (Emit). Build returns `Success = false`.

---

## Phase 5: EMIT

**Purpose**: Generate all output files

**Input**: `EmissionPlan` (from Phase 4, validated)
**Output**: File I/O (*.d.ts, *.json, *.js)
**Mutability**: Side effects (file system)

**Operations**:
1. Support types from `@tsonic/types` package
2. For each namespace (in emission order):
   - `<ns>/internal/index.d.ts` (internal declarations)
   - `<ns>/index.d.ts` (public facade)
   - `<ns>/metadata.json` (CLR-specific info)
   - `<ns>/bindings.json` (CLR → TS name mappings)
   - `<ns>/index.js` (ES module stub)

**Plan-Based Emission**:
- **ClassPrinter** uses Shape plans during emission:
  - `StaticFlatteningPlan`: Emit inherited static members for static-only types
  - `StaticConflictPlan`: Suppress conflicting static members
  - `OverrideConflictPlan`: Suppress conflicting instance overrides
  - `PropertyOverridePlan`: Emit unified union types for properties

**Files**: `Emit/SupportTypesEmitter.cs`, `Emit/InternalIndexEmitter.cs`, `Emit/FacadeEmitter.cs`, `Emit/MetadataEmitter.cs`, `Emit/BindingEmitter.cs`, `Emit/ModuleStubEmitter.cs`, `Emit/Printers/ClassPrinter.cs`

**Critical Rule**: Only executes if `ctx.Diagnostics.HasErrors == false`. If errors, Build returns `Success = false` immediately.

---

## Data Transformations Table

| Phase | Input Type | Output Type | Mutability | Key Transformations |
|-------|-----------|-------------|------------|---------------------|
| **1. LOAD** | `string[]` | `SymbolGraph` | Immutable | Reflection → SymbolGraph |
| **2. NORMALIZE** | `SymbolGraph` | `SymbolGraph` | Immutable | Build indices + [Library Mode] load contract & filter graph |
| **3. SHAPE** | `SymbolGraph` | `SymbolGraph` + Plans | Immutable | 23 passes: flatten, synthesize, determine EmitScope, create plans (including extension methods) |
| **3.5. NAME RESERVATION** | `SymbolGraph` | `SymbolGraph` | Side effect + pure | Reserve names, set TsEmitName |
| **4. PLAN** | `SymbolGraph` + Plans | `EmissionPlan` | Immutable | Build imports, plan order, combine plans |
| **4.5. OVERLOAD UNIFICATION** | `SymbolGraph` | `SymbolGraph` | Immutable | Merge overloads |
| **4.6. CONSTRAINT AUDIT** | `SymbolGraph` | `ConstraintFindings` | Immutable | Audit constraints |
| **4.7. PHASEGATE** | `SymbolGraph` + `ImportPlan` + `ConstraintFindings` + `LibraryContract?` | Side effect | Side effect | 50+ validation checks (19 modules, including LIB002) |
| **5. EMIT** | `EmissionPlan` | File I/O | Side effects | Generate .d.ts, .json, .js using Shape plans |

---

## Critical Sequencing Rules

### Shape Pass Dependencies
- **StructuralConformance BEFORE InterfaceInliner**: Needs original hierarchy to walk
- **InterfaceInliner BEFORE ExplicitImplSynthesizer**: Synthesis needs flattened interfaces
- **IndexerPlanner BEFORE FinalIndexersPass**: Mark before removal
- **ClassSurfaceDeduplicator BEFORE ConstraintCloser**: Dedup may affect constraints
- **OverloadReturnConflictResolver BEFORE ViewPlanner**: Resolve conflicts before views
- **ViewPlanner BEFORE MemberDeduplicator**: Plan views before final dedup
- **Passes 4.7-4.10**: Must run LAST to create plans based on all prior transformations

### Name Reservation Timing
**CRITICAL**: Phase 3.5 MUST occur:
- **AFTER** all Shape passes (EmitScope must be determined)
- **BEFORE** Plan phase (PhaseGate needs TsEmitName)

### PhaseGate Position
**CRITICAL**: Phase 4.7 MUST occur:
- **AFTER** all transformations complete
- **AFTER** names assigned
- **BEFORE** Emit phase

### Emit Phase Gating
**CRITICAL**: Phase 5 ONLY executes if:
- `ctx.Diagnostics.HasErrors == false`
- If errors, Build returns `Success = false` immediately

---

## Immutability Guarantees

Every phase (except Emit) follows pure functional pattern:

```csharp
public static TOutput PhaseFunction(BuildContext ctx, TInput input)
{
    // input is immutable - read only
    var transformed = ApplyTransformation(input);
    return transformed;  // new immutable output
}
```

**Example: Shape pass**
```csharp
public static SymbolGraph Inline(BuildContext ctx, SymbolGraph graph)
{
    var newNamespaces = graph.Namespaces
        .Select(ns => ns with { Types = FlattenTypes(ns.Types) })
        .ToImmutableArray;
    return graph with { Namespaces = newNamespaces };  // original unchanged
}
```

**Benefits**: No hidden mutations, safe to parallelize, easy debugging, clear data flow.
