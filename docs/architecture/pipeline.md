# Pipeline Flow

The tsbindgen pipeline transforms .NET assemblies through six distinct phases.

## Phase 1: Load

**File:** `Load/AssemblyLoader.cs`

Loads .NET assemblies using `MetadataLoadContext` and reflects all type information.

**Input:** Assembly file paths
**Output:** Initial `SymbolGraph` with raw CLR data

**Operations:**
1. Create `MetadataLoadContext` for isolated assembly inspection
2. Load each assembly from disk
3. Reflect all public types and their members
4. Build `TypeSymbol` for each type
5. Create `NamespaceSymbol` groupings

## Phase 2: Model (Index Building)

**File:** `Builder.cs` (inline)

Builds indices for fast type lookup and applies library mode filtering.

**Operations:**
1. Build `TypeIndex` dictionary (CLR full name to TypeSymbol)
2. Apply `--lib` filtering (exclude types from library package)
3. Resolve forward references between types

## Phase 3: Shape (23 Transformation Passes)

**File:** `Shape/*.cs`

Transforms the symbol graph for TypeScript emission. See [shape.md](shape.md) for all passes.

## Phase 3.5: Normalize

**File:** `Normalize/NameReservation.cs`

Reserves all TypeScript identifiers and resolves conflicts. See [normalize.md](normalize.md).

## Phase 4: Plan

**File:** `Plan/*.cs`

Plans all import/export statements and validates invariants. See [plan.md](plan.md).

## Phase 5: Emit

**File:** `Emit/*.cs`

Generates TypeScript declaration files. See [emit.md](emit.md).

## Pipeline Orchestration

**File:** `Builder.cs`

```csharp
public static void Build(BuildContext ctx, GenerateOptions options)
{
    // Phase 1: Load
    var graph = AssemblyLoader.Load(ctx, options.Assemblies);

    // Phase 2: Model
    graph = BuildIndices(graph);

    // Phase 3: Shape (23 passes)
    graph = StructuralConformance.Analyze(ctx, graph);
    graph = ViewPlanner.Plan(ctx, graph);
    // ... more passes ...

    // Phase 3.5: Normalize
    NameReservation.ReserveAll(ctx, graph);

    // Phase 4: Plan
    var emissionPlan = EmissionPlanner.Plan(ctx, graph, ...);
    PhaseGate.Validate(ctx, emissionPlan);

    // Phase 5: Emit
    InternalIndexEmitter.Emit(ctx, emissionPlan, options.OutDir);
    FacadeEmitter.Emit(ctx, emissionPlan, options.OutDir);
    FamilyIndexEmitter.Emit(ctx, families, options.OutDir);  // families.json
}
```
