# Plan Phase

The Plan phase analyzes cross-namespace dependencies and plans all import/export statements.

## Components

| Component | File | Purpose |
|-----------|------|---------|
| ImportGraphBuilder | `ImportGraphBuilder.cs` | Build dependency graph |
| ImportPlanner | `ImportPlanner.cs` | Generate import statements |
| EmissionPlanner | `EmissionPlanner.cs` | Plan file generation order |
| SCCBucketer | `SCCBucketer.cs` | Handle circular dependencies |
| PhaseGate | `PhaseGate.cs` | Validate invariants |

## ImportGraphBuilder

Builds a graph of cross-namespace type references.

```csharp
public sealed record ImportGraphData
{
    // Namespace A depends on Namespace B
    public Dictionary<string, HashSet<string>> NamespaceDependencies { get; }

    // Detailed references: source ns, target ns, target type, reference kind
    public List<CrossNamespaceReference> CrossNamespaceReferences { get; }
}

public enum ReferenceKind
{
    BaseClass,      // extends clause (needs value import)
    Interface,      // implements clause (needs value import)
    ReturnType,     // method return type (type-only import)
    Parameter,      // method parameter (type-only import)
    PropertyType,   // property type (type-only import)
    Constraint      // generic constraint (type-only import)
}
```

## ImportPlanner

Generates import statements for each namespace. Uses `FacadeFamilyIndex` for drift-proof multi-arity family resolution.

```csharp
public sealed record ImportPlan
{
    // Per-namespace imports
    public Dictionary<string, List<ImportStatement>> NamespaceImports { get; }

    // Per-namespace exports
    public Dictionary<string, List<ExportStatement>> NamespaceExports { get; }

    // Qualified names for value imports (base classes)
    public Dictionary<(string, string), string> ValueImportQualifiedNames { get; }
}

public sealed record ImportStatement(
    string ImportPath,           // "../System/internal/index.js"
    string TargetNamespace,      // "System"
    List<TypeImport> TypeImports,
    string NamespaceAlias);      // "System_Internal"

public sealed record TypeImport(
    string TypeName,
    string? Alias,
    bool IsValueImport);         // true for extends/implements
```

## Import Classification

**Value imports** (need runtime value):
- Base classes in `extends` clause
- Interfaces used in `extends` (for merged interface pattern)

**Type-only imports**:
- Return types
- Parameter types
- Property types
- Generic constraints

```typescript
// Value import - needed for extends
import * as System_Internal from '../System/internal/index.js';

// Type-only import - just for type annotations
import type { IEnumerable_1 } from '../System.Collections/index.js';
```

## EmissionPlanner

Plans the order of file generation.

```csharp
public sealed record EmissionPlan
{
    public SymbolGraph Graph { get; }
    public ImportPlan Imports { get; }
    public EmissionOrder EmissionOrder { get; }

    // Shape pass results
    public StaticFlatteningPlan StaticFlattening { get; }
    public StaticConflictPlan StaticConflicts { get; }
    public OverrideConflictPlan OverrideConflicts { get; }
    public PropertyOverridePlan PropertyOverrides { get; }
    public SafeToExtendResult SafeToExtend { get; }
}

public sealed record EmissionOrder
{
    public ImmutableArray<NamespaceEmitOrder> Namespaces { get; }
}

public sealed record NamespaceEmitOrder
{
    public NamespaceSymbol Namespace { get; }
    public ImmutableArray<TypeEmitOrder> OrderedTypes { get; }
}
```

## PhaseGate Validation

Validates 50+ invariants before emission:

```csharp
public static void Validate(BuildContext ctx, EmissionPlan plan)
{
    // PG_TYPE_001: All types have final names
    // PG_MEMBER_001: All members have final names
    // PG_IMPORT_001: No circular imports
    // PG_SCOPE_001: Correct scope usage
    // ... 46 more rules
}
```

**Validation categories:**
- Type naming invariants
- Member naming invariants
- Import/export consistency
- Scope correctness
- Generic constraint validity

## Circular Dependency Handling

SCCBucketer handles circular namespace dependencies using Tarjan's algorithm:

```csharp
// If A imports B and B imports A:
// 1. Detect strongly connected component {A, B}
// 2. Emit types in topological order within SCC
// 3. Use forward declarations where needed
```
