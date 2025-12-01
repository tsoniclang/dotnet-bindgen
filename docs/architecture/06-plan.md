# Plan Phase

This document details the Plan phase, which builds the complete emission blueprint and validates before emission.

## Overview

**Purpose:** Build import/export plans, emission order, and validate the entire graph

**Input:** SymbolGraph with names reserved
**Output:** `EmissionPlan` containing all generation decisions

**Key Files:**
- `Plan/ImportPlanner.cs` - Import/export statement planning
- `Plan/ImportGraph.cs` - Cross-namespace dependency tracking
- `Plan/EmitOrderPlanner.cs` - Namespace emission ordering
- `Plan/PhaseGate.cs` - Comprehensive validation
- `Plan/SafeToExtendAnalyzer.cs` - Interface extends safety
- `Plan/SCCPlanner.cs` - Strongly connected component analysis

## EmissionPlan

The complete blueprint for file generation:

```csharp
public sealed record EmissionPlan
{
    public SymbolGraph Graph { get; init; }
    public ImportPlan Imports { get; init; }
    public EmitOrder EmissionOrder { get; init; }

    // Shape plans
    public StaticFlatteningPlan StaticFlattening { get; init; }
    public StaticConflictPlan StaticConflicts { get; init; }
    public OverrideConflictPlan OverrideConflicts { get; init; }
    public PropertyOverridePlan PropertyOverrides { get; init; }

    // Analysis results
    public ExtensionMethodsPlan ExtensionMethods { get; init; }
    public SCCPlan SCCBuckets { get; init; }
    public HonestEmissionPlan HonestEmission { get; init; }
    public Dictionary<string, SafeToExtendResult> SafeToExtend { get; init; }
}
```

## Import Planning

### ImportGraph

Tracks all cross-namespace type references:

```
ImportGraph.Build(ctx, graph)
    │
    └─► For each namespace:
        └─► For each type:
            └─► For each type reference:
                ├─► Track source → target namespace dependency
                ├─► Classify reference kind
                └─► Record CLR name for import generation
```

### ReferenceKind

```csharp
public enum ReferenceKind
{
    BaseClass,       // extends T$instance
    Interface,       // implements I$instance
    TypeArgument,    // Generic<T>
    ReturnType,      // method(): T
    Parameter,       // method(param: T)
    PropertyType,    // property: T
    Constraint       // T extends U
}
```

### ImportPlanner

Generates import statements:

```
PlanImports(ctx, graph, importGraph)
    │
    └─► For each namespace:
        │
        ├─► For each dependency namespace:
        │   ├─► Get referenced types
        │   ├─► Compute import path
        │   ├─► Check for name collisions
        │   ├─► Create aliases if needed
        │   └─► Generate ImportStatement
        │
        └─► For each local type:
            └─► Generate ExportStatement
```

### Import Path Computation

```csharp
PathPlanner.GetSpecifier(sourceNs, targetNs)
// "System" → "System.Collections.Generic" = "../System.Collections.Generic/internal/index.js"
// "A.B" → "A" = "../../A/internal/index.js"
```

### Import Aliasing

Aliases are generated to avoid collisions:

1. **TypeScript built-ins** - `Array` → `ClrArray`
2. **Local type collision** - `Type` → `Type_Reflection`
3. **Import collision** - `HashCode` → `HashCode_Cryptography`

```csharp
DetermineAlias(ctx, sourceNs, targetNs, typeName, existingAliases)
    │
    ├─► If TypeScript built-in: return "Clr" + typeName
    ├─► If collides with local type: return typeName + "_" + targetNsShort
    ├─► If collides with other import: return typeName + "_" + targetNsShort
    └─► Else: return null (no alias needed)
```

### Value vs Type-Only Imports

TypeScript has two import modes:

```typescript
// Type-only import (for type positions)
import type { IEnumerable_1 } from './internal/index.js';

// Value import (for extends/implements)
import { Exception$instance } from '../System/internal/index.js';
```

**Value imports** are required when:
- Type is used as base class (`extends`)
- Type is used as interface (`implements`)

### ImportPlan Structure

```csharp
public sealed class ImportPlan
{
    // Namespace → list of import statements
    public Dictionary<string, List<ImportStatement>> NamespaceImports;

    // Namespace → list of export statements
    public Dictionary<string, List<ExportStatement>> NamespaceExports;

    // Namespace → (type name → alias)
    public Dictionary<string, Dictionary<string, string>> ImportAliases;

    // (source ns, target CLR name) → qualified name for value imports
    public Dictionary<(string, string), string> ValueImportQualifiedNames;

    // (source ns, target CLR name) → alias for type imports
    public Dictionary<(string, string), string> TypeImportAliasNames;
}
```

## Emission Order

### EmitOrderPlanner

Determines stable namespace emission order:

```
PlanOrder(graph)
    │
    ├─► Build dependency graph
    │
    ├─► Topological sort
    │   └─► Respects import dependencies
    │
    └─► Within each SCC bucket:
        └─► Sort alphabetically for determinism
```

### SCC Analysis

Strongly Connected Components handle circular dependencies:

```
SCCPlanner.PlanSCCBuckets(ctx, imports)
    │
    ├─► Build namespace dependency digraph
    │
    ├─► Tarjan's algorithm for SCC detection
    │
    └─► Group namespaces by component
```

**Example:** `System` and `System.Runtime.InteropServices` have circular imports → same SCC bucket.

## SafeToExtend Analysis

Determines which interfaces are safe for TypeScript `extends`:

```
SafeToExtendAnalyzer.Analyze(ctx, graph, resolver)
    │
    └─► For each type with interfaces:
        │
        └─► For each candidate interface:
            ├─► Build type member signature map
            ├─► Build interface member signature map
            │
            ├─► Check TS2430 (interface incorrectly extends)
            │   └─► Compare member signatures
            │
            └─► Check TS2320 (multiple inheritance conflict)
                └─► Compare across accumulated bases
```

### TS2430: Interface Incorrectly Extends

Occurs when a type's member signature doesn't match the interface:

```typescript
// FAIL: TS2430
interface IList$instance<T> extends ICollection$instance<T> {
    add(item: T): boolean;  // ICollection has: add(item: T): void
}
```

### TS2320: Cannot Simultaneously Extend

Occurs when two base interfaces have conflicting members:

```typescript
// FAIL: TS2320
interface TypeInfo$instance extends Type$instance, IReflect {
    // Type has getField(name: string): FieldInfo | null
    // IReflect has getField(name: string, bindingAttr: BindingFlags): FieldInfo | null
    // Conflict!
}
```

## PhaseGate Validation

The final validation before emission. 50+ validation rules organized by category:

### Core Validations

| Code | Description |
|------|-------------|
| PG_NAME_001 | All types have TsEmitName |
| PG_NAME_002 | All members have TsEmitName |
| PG_NAME_003 | View member names don't collide with class surface |
| PG_NAME_004 | No duplicate names in same scope |
| PG_NAME_005 | Class surface uniqueness |

### Scope Validations

| Code | Description |
|------|-------------|
| PG_SCOPE_001 | ViewOnly members have SourceInterface |
| PG_SCOPE_002 | All ViewOnly members in ExplicitViews |
| PG_SCOPE_003 | EmitScope matches declaration site |
| PG_SCOPE_004 | Rename scope matches EmitScope |

### Import/Export Validations

| Code | Description |
|------|-------------|
| PG_IMPORT_001 | All cross-namespace types have imports |
| PG_IMPORT_002 | Heritage clauses use value imports |
| PG_EXPORT_001 | Imported types are exported by source |
| PG_EXPORT_002 | Qualified paths are valid |
| PG_EXT_IMPORT_001 | Extension bucket imports are resolvable |

### Type Reference Validations

| Code | Description |
|------|-------------|
| PG_REF_001 | All type references resolvable |
| PG_LOAD_001 | External types are in TypeIndex or built-in |
| PG_ARITY_001 | Generic arity matches across usage |
| PG_TYPEMAP_001 | No unsupported special forms |

### Finalization Validations

| Code | Description |
|------|-------------|
| PG_FIN_001 | All members have explicit EmitScope |
| PG_FIN_002 | No Unspecified EmitScope values |
| PG_FIN_003 | All rename decisions recorded |

### View Validations

| Code | Description |
|------|-------------|
| PG_VIEW_001 | All ViewOnly members are in views |
| PG_VIEW_002 | No orphan view members |
| PG_VIEW_003 | View interfaces exist in graph |

## Strict Mode

When `--strict` is enabled, PhaseGate enforces zero warnings:

```csharp
EnforceStrictMode(ctx, validationContext)
    │
    ├─► For each diagnostic code:
    │   └─► Check against StrictModePolicy whitelist
    │
    └─► If violations exist:
        └─► Convert to ERROR
```

### Whitelisted Diagnostics

Some diagnostics are expected and whitelisted:

- Property covariance (~12 TS2417)
- Generic static members (TypeScript limitation)
- Indexer duplication

## Additional Plans

### StaticFlatteningPlan

Tracks static hierarchy flattening decisions:

```csharp
public sealed record StaticFlatteningPlan(
    IReadOnlyDictionary<string, StaticFlatteningDecision> Decisions);
```

### StaticConflictPlan

Tracks static member conflict resolutions:

```csharp
public sealed record StaticConflictPlan(
    IReadOnlyDictionary<string, StaticConflictResult> Conflicts);
```

### OverrideConflictPlan

Tracks instance override conflict resolutions:

```csharp
public sealed record OverrideConflictPlan(
    IReadOnlyDictionary<string, OverrideConflictResult> Conflicts);
```

### PropertyOverridePlan

Tracks property type unification:

```csharp
public sealed record PropertyOverridePlan(
    IReadOnlyDictionary<string, PropertyUnification> Unifications);
```

### ExtensionMethodsPlan

Groups extension methods by target type:

```csharp
public sealed record ExtensionMethodsPlan(
    ImmutableArray<ExtensionBucket> Buckets);
```

## Call Graph

```
Builder.PlanPhase()
    │
    ├─► ImportGraph.Build()
    │   └─► Collect cross-namespace references
    │
    ├─► DeclaringAssemblyResolver.ResolveBatch()
    │   └─► Resolve unresolved type references
    │
    ├─► ImportPlanner.PlanImports()
    │   ├─► PlanNamespaceImports()
    │   └─► PlanNamespaceExports()
    │
    ├─► EmitOrderPlanner.PlanOrder()
    │
    ├─► OverloadUnifier.UnifyOverloads()
    │
    ├─► InterfaceConstraintAuditor.Audit()
    │
    ├─► StaticHierarchyFlattener.Plan()
    ├─► StaticConflictDetector.Plan()
    ├─► OverrideConflictDetector.Plan()
    ├─► PropertyOverrideUnifier.Build()
    │
    ├─► ExtensionMethodAnalyzer.Analyze()
    │
    ├─► SCCPlanner.PlanSCCBuckets()
    │
    ├─► InterfaceConformanceAnalyzer.AnalyzeConformance()
    ├─► HonestEmissionPlanner.PlanHonestEmission()
    │
    ├─► SafeToExtendAnalyzer.Analyze()
    │
    └─► PhaseGate.Validate()
        ├─► Core.ValidateTypeNames()
        ├─► Core.ValidateMemberNames()
        ├─► Core.ValidateGenericParameters()
        ├─► Core.ValidateInterfaceConformance()
        ├─► Core.ValidateInheritance()
        ├─► Core.ValidateEmitScopes()
        ├─► Core.ValidateImports()
        ├─► Views.Validate()
        ├─► Names.ValidateFinalNames()
        ├─► ImportExport.ValidateImportCompleteness()
        ├─► ImportExport.ValidateExportCompleteness()
        ├─► Types.ValidateTypeReferenceResolution()
        ├─► Finalization.Validate()
        └─► [if strict] EnforceStrictMode()
```
