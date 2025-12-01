# tsbindgen Pipeline Architecture

## 1. System Overview

**tsbindgen** transforms .NET assemblies into TypeScript declaration files with metadata sidecars. Enables the **Tsonic compiler** (TypeScript-to-C# targeting NativeAOT) to import .NET BCL types from TypeScript with full IDE support.

### The Tsonic Compiler

**Tsonic** compiles TypeScript to C# NativeAOT executables:
- Write .NET apps using TypeScript syntax
- Direct .NET interop: `import { File } from "System.IO"`
- NativeAOT output: Single-file native executables
- Full TypeScript type checking against .NET APIs

**Example**:
```typescript
import { File } from "System.IO";
const lines = File.ReadAllLines("data.txt");  // ReadonlyArray<string>
```

### tsbindgen Output

Three companion artifacts per assembly:
1. **Type declarations** (`*.d.ts`) - IDE IntelliSense, type checking
2. **Metadata sidecars** (`*.metadata.json`) - CLR semantics (virtual/override, struct, ref)
3. **Binding manifests** (`*.bindings.json`) - TypeScript → CLR name mappings

### Purpose

- **Input**: .NET assembly DLLs
- **Process**: Reflection, analysis, naming conflict resolution, import planning
- **Output**: `.d.ts` files, JSON metadata, binding maps

Enables TypeScript devs to:
- Reference .NET types with IntelliSense
- Import BCL types (`List<T>`, `Dictionary<K,V>`, `File`, `Console`)
- Understand CLR semantics (virtual, static, ref, struct vs class)
- Get correct C# code generation

Manual maintenance of 4,000+ types across 130 namespaces would be infeasible. tsbindgen automates with 100% data integrity.

---

## 2. Architectural Principles

### Single-Pass Processing
Pipeline processes each assembly **once** through six sequential phases. No iteration. Immutable transformations.

### Immutable Data Structures
All data structures are **immutable records**:
- `SymbolGraph` → `NamespaceSymbol[]` → `TypeSymbol[]` → `MemberSymbol[]`
- Transformations return new graph instances via `with` expressions
- No in-place mutation

Enables: Pure functions, safe parallelization, precise change tracking, rollback capability.

### Pure Functions
All transformation logic in **static classes** with **pure functions**:
```csharp
public static class InterfaceInliner
{
    public static SymbolGraph Inline(BuildContext ctx, SymbolGraph graph) { ... }
}
```

### Centralized State (BuildContext)
All shared services in `BuildContext`:
- **Policy**: Configuration (name transforms, filters)
- **SymbolRenamer**: Centralized naming authority
- **DiagnosticBag**: Error/warning collection
- **Interner**: String deduplication
- **Logger**: Progress reporting

### StableId-Based Identity
Every type/member has a **StableId** (assembly-qualified identifier):
- **TypeStableId**: `"System.Private.CoreLib:System.Decimal"`
- **MemberStableId**: `"System.Private.CoreLib:System.Decimal::ToString(IFormatProvider):String"`

Properties: Immutable, unique, stable, semantic.

Used for: Rename keys, cross-assembly refs, binding metadata, duplicate detection.

### Scope-Based Naming
TypeScript names reserved in **scopes** to enforce uniqueness:
1. **Namespace Scope**: `ns:System.Collections.Generic:internal`
2. **Class Surface Scope**: `type:System.Decimal#instance`
3. **View Surface Scope**: `view:CoreLib:System.Decimal:CoreLib:IConvertible#instance`

Enables: Class/view member coexistence, static/instance separation, explicit interface implementations.

---

## 3. High-Level Architecture

```
CLI Entry (Program.cs)
    ↓
Builder.Build (Builder.cs)
    ↓
BuildContext (Policy, SymbolRenamer, DiagnosticBag, Interner, Logger, LibraryMode)
    ↓
┌─────────────────────────────────────────┐
│ PHASE 1: Load (Reflection)              │
│  - AssemblyLoader (transitive closure)  │
│  - ReflectionReader (SymbolGraph)       │
│  - InterfaceMemberSubstitution          │
│  Output: SymbolGraph (pure CLR data)    │
└────────────────┬────────────────────────┘
                 ▼
┌─────────────────────────────────────────┐
│ PHASE 2: Normalize (Build Indices)      │
│  - SymbolGraph.WithIndices              │
│  - [Library Mode] Contract loading      │
│  - [Library Mode] Graph filtering       │
│  - NamespaceIndex, TypeIndex            │
│  Output: SymbolGraph (with indices)     │
└────────────────┬────────────────────────┘
                 ▼
┌─────────────────────────────────────────┐
│ PHASE 3: Shape (22 Transformations)     │
│  1. GlobalInterfaceIndex.Build          │
│  2. InterfaceDeclIndex.Build            │
│  3. StructuralConformance.Analyze       │
│  4. InterfaceInliner.Inline             │
│  5. ExplicitImplSynthesizer             │
│  6. DiamondResolver.Resolve             │
│  7. BaseOverloadAdder                   │
│  8. StaticHierarchyFlattener            │
│  9. StaticConflictDetector              │
│  10. OverrideConflictDetector           │
│  11. StaticSideAnalyzer.Analyze         │
│  12. IndexerPlanner.Plan                │
│  13. HiddenMemberPlanner.Plan           │
│  14. FinalIndexersPass.Run              │
│  15. ClassSurfaceDeduplicator           │
│  16. ConstraintCloser.Close             │
│  17. OverloadReturnConflictResolver     │
│  18. PropertyOverrideUnifier            │
│  19. ViewPlanner.Plan                   │
│  20. MemberDeduplicator                 │
│  21. BaseUnifier                        │
│  22. InterfaceUnifier                   │
│  Output: SymbolGraph (shaped for TS)    │
└────────────────┬────────────────────────┘
                 ▼
┌─────────────────────────────────────────┐
│ PHASE 3.5: Name Reservation             │
│  - NameReservation.ReserveAllNames      │
│  - Reserve types, members               │
│  - Apply naming policy                  │
│  - Resolve conflicts                    │
│  Output: SymbolGraph + RenameDecisions  │
└────────────────┬────────────────────────┘
                 ▼
┌─────────────────────────────────────────┐
│ PHASE 4: Plan (Imports/Order)           │
│  - ImportGraph.Build                    │
│  - ImportPlanner.PlanImports            │
│  - EmitOrderPlanner.PlanOrder           │
│  - OverloadUnifier.UnifyOverloads       │
│  - InterfaceConstraintAuditor           │
│  - PhaseGate.Validate (50+ rules)       │
│  - [Library Mode] LIB002 validation     │
│  Output: EmissionPlan                   │
└────────────────┬────────────────────────┘
                 ▼
┌─────────────────────────────────────────┐
│ PHASE 5: Emit (File Generation)         │
│  - Support types from @tsonic/types pkg │
│  - InternalIndexEmitter (internal/)     │
│  - FacadeEmitter (facade/)              │
│  - MetadataEmitter (metadata.json)      │
│  - BindingEmitter (bindings.json)       │
│  - ModuleStubEmitter (index.js)         │
│  Output: Files written to disk          │
└────────────────┬────────────────────────┘
                 ▼
            BuildResult
```

---

## 4. Key Concepts

### StableId: Assembly-Qualified Identifiers

Immutable identity before name transformations. Permanent key for rename decisions and CLR binding.

**Format**:
- **TypeStableId**: `{AssemblyName}:{ClrFullName}`
  - `"System.Private.CoreLib:System.Collections.Generic.List\`1"`
- **MemberStableId**: `{AssemblyName}:{DeclaringType}::{MemberName}{CanonicalSignature}`
  - `"System.Private.CoreLib:System.Decimal::ToString(IFormatProvider):String"`

**Properties**: Immutable, unique, stable, semantic (not metadata token).

**Usage**: Rename keys, cross-assembly refs, binding metadata, duplicate detection, PhaseGate validation.

### EmitScope: Placement Decisions

Controls **where** a member is emitted:

```csharp
public enum EmitScope
{
    ClassSurface,  // On class/interface
    StaticSurface, // In static section
    ViewOnly,      // In As_IInterface view
    Omitted        // Not emitted (in metadata)
}
```

**ClassSurface**: Default for public members
**StaticSurface**: Static members
**ViewOnly**: Explicit interface implementations (As_IInterface property)
**Omitted**: Indexers, generic statics, internal/private (tracked in metadata)

**Decision process**: StructuralConformance → IndexerPlanner → HiddenMemberPlanner → ClassSurfaceDeduplicator → PhaseGate validation.

### ViewPlanner: Explicit Interface Implementation

TypeScript lacks explicit interface implementations. C# has them:

```csharp
// C# - explicit implementation
class Decimal : IConvertible
{
    public override string ToString => "...";  // Implicit
    bool IConvertible.ToBoolean(IFormatProvider? p) => ...;  // Explicit - only via cast
}
```

**ViewPlanner solution**:
```typescript
// TypeScript output
class Decimal {
    ToString: string;  // ClassSurface
    As_IConvertible: {  // ViewOnly members
        ToBoolean(provider: IFormatProvider | null): boolean;
        ToInt32(provider: IFormatProvider | null): int;
    };
}
```

**Process**:
1. StructuralConformance marks members `ViewOnly`
2. ViewPlanner groups by source interface
3. Creates `ExplicitView` with view property name and members
4. FacadeEmitter emits view properties

**Benefits**: Full CLR semantics, no data loss, type-safe casting, correct C# generation.

### Scope-Based Naming: Why Separate Scopes

**Problem 1: Class vs View**
```typescript
// Without scopes: ToString, ToString2, ToString3 (wrong!)
// With scopes: Each ToString in separate scope (correct)
class Decimal {
    ToString: string;  // Scope: "type:System.Decimal#instance"
    As_IConvertible: {
        ToString: string;  // Scope: "view:CoreLib:Decimal:CoreLib:IConvertible#instance"
    };
}
```

**Problem 2: Static vs Instance**
```csharp
class Array {
    int Length { get; }           // Instance
    static int Length(Array a);   // Static - different scope
}
```

**Scope formats**:
- Namespace: `ns:System.Collections.Generic:internal`
- Class Instance: `type:System.Decimal#instance`
- Class Static: `type:System.Decimal#static`
- View Instance: `view:CoreLib:System.Decimal:CoreLib:IConvertible#instance`

**Benefits**: Independent naming per context, no artificial suffixes, preserves original names, type-safe via ScopeFactory.

### PhaseGate: Validation Gatekeeper

Comprehensive validation after transformations, before emission. Enforces 50+ invariants.

**Purpose**: Fail fast, prevent malformed output, document invariants, enable safe refactoring.

**Categories**:
1. **Finalization** (PG_FIN_001-009) - Every symbol has final TS name
2. **Scope Integrity** (PG_SCOPE_001-004) - Well-formed scopes
3. **Name Uniqueness** (PG_NAME_001-005) - No duplicates in same scope
4. **View Integrity** (PG_INT_001-003) - ViewOnly members belong to views
5. **Import/Export** (PG_IMPORT_001, PG_EXPORT_001, PG_API_001-002) - Valid imports
6. **Type Resolution** (PG_LOAD_001, PG_TYPEMAP_001) - All types resolvable
7. **Overload Collision** (PG_OL_001-002) - No overload conflicts
8. **Constraint Integrity** (PG_CNSTR_001-004) - Constraints satisfied

**Error Severity**: ERROR (blocks), WARNING (logged), INFO (diagnostic).

**Output**: Console log, `.diagnostics.txt`, `validation-summary.json`.

**Integration**:
```csharp
PhaseGate.Validate(ctx, graph, imports, constraintFindings);
if (ctx.Diagnostics.HasErrors) return new BuildResult { Success = false };
```

### Library Mode: Filtered Package Generation

Excludes base library types when generating declarations for user assemblies (`--lib` flag).

**How it works**:
1. **Contract Loading** (Phase 2): Read base package `metadata.json` and `bindings.json` to extract type/member StableIds
2. **Graph Filtering** (Phase 2): Remove types whose StableId is in contract HashSet (O(1) lookup)
3. **Validation** (Phase 4): LIB002 checks no dangling references to filtered types

**Files**: `LibraryContractLoader.cs`, `LibraryFilter.cs`, `LibraryMode.cs` (validation)

**Codes**: LIB001 (contract files exist), LIB002 (no dangling references), LIB003 (disabled in --lib mode)

---

## 5. Directory Structure

```
src/tsbindgen/
├── Builder.cs                  # Main orchestrator
├── BuildContext.cs             # Shared services
├── Load/                       # Phase 1: Reflection
│   ├── AssemblyLoader.cs       # Transitive closure loading
│   ├── ReflectionReader.cs     # System.Reflection → SymbolGraph
│   ├── TypeReferenceFactory.cs # Build TypeReference objects
│   └── InterfaceMemberSubstitution.cs  # Closed generic substitution
├── Model/                      # Immutable data structures
│   ├── SymbolGraph.cs          # Root container
│   ├── Symbols/                # NamespaceSymbol, TypeSymbol, MemberSymbols
│   ├── Types/                  # TypeReference hierarchy
│   └── AssemblyKey.cs          # Assembly identity
├── Normalize/                  # Phase 2: Indices
│   ├── SignatureNormalization.cs  # Canonicalize signatures
│   ├── OverloadUnifier.cs         # Merge overloads
│   └── NameReservation.cs         # Reserve names
├── Shape/                      # Phase 3: 22 transformations
│   ├── GlobalInterfaceIndex.cs
│   ├── InterfaceDeclIndex.cs
│   ├── StructuralConformance.cs
│   ├── InterfaceInliner.cs
│   ├── ExplicitImplSynthesizer.cs
│   ├── DiamondResolver.cs
│   ├── BaseOverloadAdder.cs
│   ├── StaticHierarchyFlattener.cs
│   ├── StaticConflictDetector.cs
│   ├── OverrideConflictDetector.cs
│   ├── StaticSideAnalyzer.cs
│   ├── IndexerPlanner.cs
│   ├── HiddenMemberPlanner.cs
│   ├── FinalIndexersPass.cs
│   ├── ClassSurfaceDeduplicator.cs
│   ├── ConstraintCloser.cs
│   ├── OverloadReturnConflictResolver.cs
│   ├── PropertyOverrideUnifier.cs
│   ├── ViewPlanner.cs
│   ├── MemberDeduplicator.cs
│   ├── BaseUnifier.cs
│   └── InterfaceUnifier.cs
├── Renaming/                   # Phase 3.5: Naming service
│   ├── SymbolRenamer.cs        # Central naming authority
│   ├── StableId.cs             # Identity types
│   ├── RenameScope.cs          # Scope types
│   ├── ScopeFactory.cs         # Scope construction
│   ├── RenameDecision.cs       # Rename decision record
│   ├── NameReservationTable.cs # Per-scope name tracking
│   └── TypeScriptReservedWords.cs  # Keyword sanitization
├── Library/                    # Library mode filtering
│   ├── LibraryContract.cs      # Contract record (StableId sets)
│   ├── LibraryContractLoader.cs  # Load base package metadata
│   └── LibraryFilter.cs        # Filter graph by contract
├── Plan/                       # Phase 4: Import planning
│   ├── ImportGraph.cs          # Dependency graph
│   ├── ImportPlanner.cs        # Plan imports/aliases
│   ├── DetermineAlias.cs       # Auto-aliasing for collisions
│   ├── StaticOverloadPlan.cs   # Static overload planning
│   ├── OverrideConflictPlan.cs # Override conflict planning
│   ├── PropertyOverridePlan.cs # Property override unification
│   ├── EmissionPlan.cs         # Composite emission plan
│   ├── EmitOrderPlanner.cs     # Stable order
│   ├── InterfaceConstraintAuditor.cs  # Constraint audit
│   ├── PhaseGate.cs            # Pre-emission validation
│   ├── TsAssignability.cs      # TypeScript assignability
│   ├── TsErase.cs              # Type erasure
│   └── Validation/             # PhaseGate modules (19 files)
│       ├── Core.cs, Names.cs, Views.cs, Scopes.cs
│       ├── Types.cs, ImportExport.cs, Constraints.cs
│       ├── Finalization.cs, Context.cs, LibraryMode.cs
└── Emit/                       # Phase 5: File generation
    ├── InternalIndexEmitter.cs # internal/index.d.ts
    ├── FacadeEmitter.cs        # facade/index.d.ts
    ├── MetadataEmitter.cs      # metadata.json
    ├── BindingEmitter.cs       # bindings.json
    ├── ModuleStubEmitter.cs    # index.js stubs
    ├── TypeMap.cs              # TypeReference → TS string
    ├── TypeNameResolver.cs     # Resolve names with imports
    └── Printers/               # Code printers
        ├── ClassPrinter.cs     # Emit classes/interfaces
        ├── EnumPrinter.cs      # Emit enums
        └── DelegatePrinter.cs  # Emit delegates
```

**Design decisions**: Load (pure reflection), Model (shared data), Normalize (indices), Shape (22 transformations), Renaming (centralized naming), Plan (import analysis + validation), Emit (string generation only).

---

## 6. Build Context Services

`BuildContext` is the **immutable container** for all shared services. Created once, passed everywhere.

### Policy (Configuration)

```csharp
public sealed class GenerationPolicy
{
    // Naming transforms
    public NameTransformStrategy TypeNameTransform { get; init; }
    public NameTransformStrategy MemberNameTransform { get; init; }
    public Dictionary<string, string> ExplicitMap { get; init; }  // CLR → TS overrides

    // Filters
    public bool IncludeInternalTypes { get; init; }
    public bool EmitDocumentation { get; init; }
    public bool UseBrandedPrimitives { get; init; }  // int vs number
    public ImportStyle ImportStyle { get; init; }    // ES6 vs namespace
}
```

### SymbolRenamer (Naming Service)

Central naming authority for all TypeScript identifiers.

**Responsibilities**: Reserve names in scopes, apply transforms, resolve conflicts (numeric suffixes), sanitize reserved words, track rename decisions.

**Key Methods**:
```csharp
void ReserveTypeName(StableId id, string requested, NamespaceScope scope, string reason);
void ReserveMemberName(StableId id, string requested, RenameScope scope, string reason, bool isStatic);
string GetFinalTypeName(TypeSymbol type, NamespaceArea area);
string GetFinalMemberName(StableId id, RenameScope scope);
bool HasFinalTypeName(StableId id, NamespaceScope scope);
```

**Scope separation**: Class surface (`type:System.Decimal#instance`) vs View surface (`view:CoreLib:System.Decimal:CoreLib:IConvertible#instance`).

**Decision recording**:
```csharp
public sealed record RenameDecision
{
    public StableId Id { get; init; }
    public string Requested { get; init; }
    public string Final { get; init; }
    public string From { get; init; }
    public string Reason { get; init; }
    public string DecisionSource { get; init; }
    public string Strategy { get; init; }  // "None", "NumericSuffix", "Sanitize"
    public string ScopeKey { get; init; }
}
```

Decisions → `bindings.json` for runtime binding.

### DiagnosticBag (Error Collection)

```csharp
void Error(string code, string message);
void Warning(string code, string message);
void Info(string code, string message);
bool HasErrors;
IReadOnlyList<Diagnostic> GetAll;
```

**Diagnostic format**:
```csharp
public sealed record Diagnostic
{
    public DiagnosticSeverity Severity { get; init; }  // Error, Warning, Info
    public string Code { get; init; }                   // "PG_FIN_003", "TS2417"
    public string Message { get; init; }
    public string? Location { get; init; }
}
```

### Interner (String Deduplication)

Reduces memory usage by interning common strings:
```csharp
string Intern(string value);
```

Typical savings: 30-40% memory reduction for large BCL assemblies.

### Logger (Progress Reporting)

```csharp
void Log(string category, string message);
```

**Categories**: "Build", "Load", "Shape", "ViewPlanner", "PhaseGate", "Emit"

**Filtering**:
```csharp
var ctx = BuildContext.Create(policy, logger, verboseLogging: true);
// OR
var logCategories = new HashSet<string> { "PhaseGate", "ViewPlanner" };
var ctx = BuildContext.Create(policy, logger, verboseLogging: false, logCategories);
```

---

## Current Validation Metrics

Full BCL generation: **4,295 types, 130 namespaces, 50,720 members**

**Total TypeScript errors**: **0** (zero) ✓

**Error progression**:
- jumanji9: 91 errors
- jumanji10-13: Incremental reductions via Shape passes 4.7-4.9
- jumanji14: 0 errors via PropertyOverrideUnifier (Pass 4.10)

**Key fixes**:
1. **StaticHierarchyFlattener** (Pass 4.7) - Flatten static-only hierarchies
2. **StaticConflictDetector** (Pass 4.8) - Detect static conflicts
3. **OverrideConflictDetector** (Pass 4.9) - Suppress override conflicts
4. **PropertyOverrideUnifier** (Pass 4.10) - Unify property types via unions
5. **ExtensionMethodAnalyzer** (Pass 4.11) - Group extension methods by target type
6. **Assembly forwarding fix** - Lookup by ClrFullName instead of StableId
7. **Generic safety filter** - Skip properties with type parameters

**Data integrity**: **100%** - All reflected types/members accounted for (completeness verified).

**Result**: Zero TypeScript errors for first time in project history.

---

## Summary

The tsbindgen pipeline is a **deterministic, pure functional transformation** from .NET assemblies to TypeScript:

1. **Load**: System.Reflection → SymbolGraph (pure CLR)
2. **Normalize**: Build indices + [Library Mode] contract loading & filtering
3. **Shape**: 23 transformations for .NET/TypeScript impedance (including extension methods)
4. **Name Reservation**: Reserve all names via SymbolRenamer
5. **Plan**: Analyze dependencies, plan imports, validate (PhaseGate + LIB002)
6. **Emit**: Generate TypeScript + metadata + bindings + extension buckets

**Core Principles**:
- **Immutability**: All data structures are immutable records
- **Purity**: All transformations are pure functions
- **Centralization**: All shared state in BuildContext
- **Identity**: StableIds provide stable identity
- **Scoping**: Separate naming scopes for class/view surfaces
- **Validation**: PhaseGate enforces 50+ invariants

**Result**: 100% data integrity, zero data loss, type-safe TypeScript for entire .NET BCL (4,295 types, 130 namespaces, **0 TypeScript errors**).
