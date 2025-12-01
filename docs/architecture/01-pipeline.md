# Pipeline Architecture

This document details the complete pipeline flow from CLI invocation to file output.

## Pipeline Entry Point

### CLI → Builder

```
dotnet run -- generate -d $RUNTIME -o ./output --naming js
                │
                ▼
        Program.cs (CLI parser)
                │
                ▼
        GenerateCommand.cs
                │
                ▼
        BuildContext (configuration)
                │
                ▼
        Builder.Build(ctx)
```

### Builder Orchestration

**Location:** `Builder.cs`

```csharp
public static void Build(BuildContext ctx)
{
    // ═══════════════════════════════════════════════════════════════
    // PHASE 1: LOAD
    // ═══════════════════════════════════════════════════════════════
    var graph = AssemblyLoader.Load(ctx);

    // ═══════════════════════════════════════════════════════════════
    // PHASE 2: NORMALIZE (Library filtering, indices)
    // ═══════════════════════════════════════════════════════════════
    graph = graph.WithIndices();
    if (ctx.Policy.LibraryPath != null)
    {
        graph = LibraryFilter.Filter(ctx, graph);
    }

    // ═══════════════════════════════════════════════════════════════
    // PHASE 3: SHAPE (23 transformation passes)
    // ═══════════════════════════════════════════════════════════════
    graph = RunShapePasses(ctx, graph);

    // ═══════════════════════════════════════════════════════════════
    // PHASE 3.5: NAME RESERVATION
    // ═══════════════════════════════════════════════════════════════
    graph = NameReservation.Reserve(ctx, graph);

    // ═══════════════════════════════════════════════════════════════
    // PHASE 4: PLAN
    // ═══════════════════════════════════════════════════════════════
    var plan = BuildEmissionPlan(ctx, graph);

    // ═══════════════════════════════════════════════════════════════
    // PHASEGATE: VALIDATION
    // ═══════════════════════════════════════════════════════════════
    PhaseGate.Validate(ctx, graph, plan);

    // ═══════════════════════════════════════════════════════════════
    // PHASE 5: EMIT
    // ═══════════════════════════════════════════════════════════════
    EmitFiles(ctx, plan);
}
```

## Phase Details

### Phase 1: Load

**Purpose:** Load .NET assemblies and build initial symbol graph

**Input:** Assembly file paths, runtime directory path
**Output:** `SymbolGraph` with raw CLR type information

```
AssemblyLoader.Load(ctx)
    │
    ├─► Create MetadataLoadContext
    │   └─► Resolve assembly references
    │
    ├─► For each assembly:
    │   ├─► Load via MetadataLoadContext
    │   ├─► Enumerate public types
    │   └─► Convert to TypeSymbol
    │
    └─► Build SymbolGraph
        ├─► Group types by namespace
        └─► Create NamespaceSymbol per namespace
```

**Key Files:**
- `Load/AssemblyLoader.cs`
- `Model/SymbolGraph.cs`
- `Model/Symbols/TypeSymbol.cs`

### Phase 2: Normalize (Pre-Shape)

**Purpose:** Build indices and apply library filtering

**Input:** Raw SymbolGraph
**Output:** SymbolGraph with TypeIndex, InterfaceIndex

```
graph.WithIndices()
    │
    ├─► Build TypeIndex: Dictionary<ClrFullName, TypeSymbol>
    ├─► Build InterfaceIndex: Dictionary<ClrFullName, List<TypeSymbol>>
    └─► Build AssemblyToNamespace mapping

LibraryFilter.Filter(ctx, graph)  [if --lib specified]
    │
    ├─► Load library package manifest
    ├─► Remove types that exist in library
    └─► Keep only user-defined types
```

### Phase 3: Shape (23 Passes)

**Purpose:** Transform the symbol graph for TypeScript emission

**Input:** SymbolGraph with indices
**Output:** Transformed SymbolGraph with EmitScope assigned

```
RunShapePasses(ctx, graph)
    │
    ├─► Pass 1: InterfaceInliner
    │   └─► Flatten interface inheritance hierarchies
    │
    ├─► Pass 2: DiamondResolver
    │   └─► Handle diamond inheritance patterns
    │
    ├─► Pass 3: BaseOverloadAdder
    │   └─► Add inherited overloads to derived types
    │
    ├─► Pass 4: ExplicitInterfaceMarker
    │   └─► Mark explicit interface implementations
    │
    ├─► Pass 5: HidingMemberResolver
    │   └─► Handle C# 'new' keyword hiding
    │
    ├─► Pass 6: StructuralConformance
    │   └─► Analyze TypeScript structural assignability
    │   └─► Synthesize ViewOnly members for non-conforming interfaces
    │
    ├─► Pass 7: ViewPlanner
    │   └─► Create ExplicitView entries for As_IInterface accessors
    │
    ├─► Pass 8: OverloadUnifier
    │   └─► Merge compatible method overloads
    │
    ├─► Pass 9: IndexerPlanner
    │   └─► Plan indexer emission (get_Item/set_Item methods)
    │
    ├─► Pass 10: StaticHierarchyFlattener
    │   └─► Flatten static member hierarchies
    │
    ├─► Pass 11: StaticConflictDetector
    │   └─► Detect static member name conflicts
    │
    ├─► Pass 12: OverrideConflictDetector
    │   └─► Detect override signature conflicts
    │
    ├─► Pass 13: PropertyOverrideUnifier
    │   └─► Unify property types across inheritance
    │
    ├─► Pass 14-23: Additional transformation passes
    │   └─► (EmitScopeAssigner, etc.)
    │
    └─► Final graph with all EmitScope values set
```

**Key Passes Explained:**

| Pass | Purpose | Example |
|------|---------|---------|
| InterfaceInliner | Flatten `IList<T> : ICollection<T> : IEnumerable<T>` | All methods visible on `IList<T>` |
| StructuralConformance | Detect when class can't satisfy interface in TS | `Decimal.ToInt32()` private → synthesize ViewOnly |
| ViewPlanner | Create view accessors | `As_IConvertible_1()` accessor |
| OverloadUnifier | Merge `Add(T)` overloads | Single `add(item: T)` in TS |

### Phase 3.5: Name Reservation

**Purpose:** Allocate TypeScript names and resolve conflicts

**Input:** Transformed SymbolGraph
**Output:** SymbolGraph with TsEmitName on all symbols

```
NameReservation.Reserve(ctx, graph)
    │
    ├─► For each namespace:
    │   ├─► Reserve type names in namespace scope
    │   │   └─► Apply type style transform (no-op for types)
    │   │   └─► Sanitize reserved words (Type → Type_)
    │   │   └─► Resolve conflicts with numeric suffix
    │   │
    │   └─► For each type:
    │       ├─► Reserve member names in type scope
    │       │   ├─► Separate static vs instance scopes
    │       │   └─► Apply member style transform (camelCase if --naming js)
    │       │
    │       └─► Reserve view member names in view scopes
    │
    └─► All TsEmitName fields populated
```

**Name Resolution Algorithm:**

```
ResolveNameWithConflicts(requested)
    │
    ├─► Check explicit overrides (user-specified renames)
    │
    ├─► Apply style transform (camelCase/PascalCase)
    │
    ├─► Sanitize TypeScript reserved words
    │   └─► class → class_, Type → Type_, etc.
    │
    ├─► Try to reserve in scope table
    │   └─► If available: return name
    │
    └─► Conflict detected:
        ├─► If explicit interface impl: add interface suffix
        │   └─► SyncRoot → SyncRoot_ICollection
        │
        └─► Otherwise: add numeric suffix
            └─► Add → Add2 → Add3 → ...
```

### Phase 4: Plan

**Purpose:** Build complete emission blueprint

**Input:** SymbolGraph with names
**Output:** `EmissionPlan` containing all generation decisions

```
BuildEmissionPlan(ctx, graph)
    │
    ├─► ImportGraphCollector.Collect(graph)
    │   └─► Track all cross-namespace type references
    │   └─► Categorize: BaseClass, Interface, TypeArgument, etc.
    │
    ├─► ImportPlanner.PlanImports(graph, importGraph)
    │   └─► Generate ImportStatement per namespace pair
    │   └─► Detect name collisions, create aliases
    │   └─► Mark value vs type-only imports
    │
    ├─► EmissionPlanner.Plan(graph, importPlan)
    │   └─► Topologically sort namespaces
    │   └─► Order types within namespaces
    │   └─► Collect export statements
    │
    ├─► SafeToExtendAnalyzer.Analyze(graph)
    │   └─► Determine which interfaces are safe to extend
    │   └─► Avoid TS2430/TS2320 errors
    │
    ├─► StaticFlatteningPlanner.Plan(graph)
    │   └─► Plan static member emission
    │
    └─► Build EmissionPlan
        ├─► Graph: SymbolGraph
        ├─► Imports: ImportPlan
        ├─► EmissionOrder: NamespaceEmitOrder[]
        ├─► SafeToExtend: Dictionary<TypeId, SafeToExtendResult>
        └─► ... (additional plans)
```

**EmissionPlan Structure:**

```csharp
public sealed record EmissionPlan
{
    public SymbolGraph Graph { get; init; }
    public ImportPlan Imports { get; init; }
    public EmissionOrder EmissionOrder { get; init; }
    public StaticFlatteningPlan StaticFlattening { get; init; }
    public Dictionary<string, StaticConflictResult> StaticConflicts { get; init; }
    public Dictionary<string, OverrideConflictResult> OverrideConflicts { get; init; }
    public Dictionary<string, PropertyOverridePlan> PropertyOverrides { get; init; }
    public Dictionary<string, SafeToExtendResult> SafeToExtend { get; init; }
    public HonestEmissionPlan HonestEmission { get; init; }
    public ExtensionMethodPlan ExtensionMethods { get; init; }
}
```

### PhaseGate Validation

**Purpose:** Ensure data integrity before emission

**Input:** SymbolGraph, EmissionPlan
**Output:** Pass (continue) or Fail (abort with errors)

```
PhaseGate.Validate(ctx, graph, plan)
    │
    ├─► PG_EMIT_001: All members have EmitScope set
    │   └─► Error if EmitScope == Unspecified
    │
    ├─► PG_NAME_001: All types have TsEmitName
    │   └─► Error if TsEmitName is empty
    │
    ├─► PG_NAME_002: All members have TsEmitName
    │   └─► Error if TsEmitName is empty (for non-omitted members)
    │
    ├─► PG_REF_001: All type references resolvable
    │   └─► Error if NamedTypeReference points to missing type
    │
    ├─► PG_SCOPE_001: ViewOnly members have SourceInterface
    │   └─► Error if ViewOnly but SourceInterface is null
    │
    ├─► PG_VIEW_001: All ViewOnly members in ExplicitViews
    │   └─► Error if ViewOnly member not in any view
    │
    └─► ... (50+ validation rules)
```

### Phase 5: Emit

**Purpose:** Generate TypeScript files

**Input:** EmissionPlan
**Output:** .d.ts files on disk

```
EmitFiles(ctx, plan)
    │
    ├─► FacadeEmitter.Emit(ctx, plan, outputDir)
    │   │
    │   └─► For each namespace in EmissionOrder:
    │       ├─► Generate facade header
    │       ├─► Emit import from internal/index.js
    │       ├─► Emit cross-namespace type imports
    │       ├─► Emit re-export from internal
    │       ├─► Emit friendly aliases (List = List_1)
    │       ├─► Emit Action/Func delegate helpers (System only)
    │       └─► Write to: output/{namespace}/index.d.ts
    │
    └─► InternalIndexEmitter.Emit(ctx, plan, outputDir)
        │
        └─► For each namespace in EmissionOrder:
            ├─► Generate header
            ├─► Emit branded primitive imports (@tsonic/types)
            ├─► Emit CLROf<T> utility
            ├─► Emit cross-namespace imports
            │
            └─► For each type in namespace:
                │
                ├─► If primitive CLR type:
                │   └─► Emit: export type Int32 = int;
                │
                ├─► If type has views:
                │   ├─► ClassPrinter.PrintInstance → interface T$instance
                │   ├─► ClassPrinter.PrintValueExport → const T
                │   ├─► EmitCompanionViewsInterface → interface __T$views
                │   ├─► EmitIntersectionTypeAlias → type T = T$instance & __T$views
                │   └─► EmitMergedInterfaceExtends → interface T$instance extends ...
                │
                └─► If type without views:
                    ├─► ClassPrinter.Print → interface/enum/type
                    └─► ClassPrinter.PrintValueExport → const (if class/struct)
```

## Data Flow Summary

```
┌────────────────────────────────────────────────────────────────────────────┐
│ CLI Arguments                                                              │
│ --assembly, --assembly-dir, --naming, --lib, --verbose, --strict           │
└────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ BuildContext                                                               │
│ ├── Policy: GenerationPolicy (naming, modules, library)                    │
│ ├── Renamer: SymbolRenamer (name allocation)                               │
│ └── Diagnostics: DiagnosticBag (errors/warnings)                           │
└────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ Phase 1-2: Load                                                            │
│ DLL files → MetadataLoadContext → SymbolGraph                              │
│ Output: SymbolGraph (raw CLR types)                                        │
└────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ Phase 3: Shape (23 passes)                                                 │
│ SymbolGraph → Transform → Transform → ... → SymbolGraph                    │
│ Output: SymbolGraph (with EmitScope assigned)                              │
└────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ Phase 3.5: Normalize                                                       │
│ SymbolGraph → NameReservation → SymbolGraph                                │
│ Output: SymbolGraph (with TsEmitName assigned)                             │
└────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ Phase 4: Plan                                                              │
│ SymbolGraph → ImportPlanner → EmissionPlanner → EmissionPlan               │
│ Output: EmissionPlan (complete generation blueprint)                       │
└────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ PhaseGate: Validation                                                      │
│ SymbolGraph + EmissionPlan → 50+ validation rules                          │
│ Output: Pass (continue) or Fail (abort)                                    │
└────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────────┐
│ Phase 5: Emit                                                              │
│ EmissionPlan → FacadeEmitter + InternalIndexEmitter → Files                │
│ Output: .d.ts files per namespace                                          │
└────────────────────────────────────────────────────────────────────────────┘
```
