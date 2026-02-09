# Shape Phase

The Shape phase transforms the symbol graph for TypeScript emission. It runs multiple passes that analyze, synthesize, and modify the graph to handle CLR/TypeScript differences.

## Pass Overview

Shape passes run in strict order. Each pass may depend on results from earlier passes.

### Index Building (Setup)

| # | Pass | Purpose |
|---|------|---------|
| 1 | GlobalInterfaceIndex | Build global index of all interfaces |
| 2 | InterfaceDeclIndex | Build interface declaration index |

### Structural Analysis

| # | Pass | Purpose |
|---|------|---------|
| 3 | StructuralConformance | Analyze which members satisfy interfaces |
| 4 | InterfaceInliner | Flatten interface inheritance |
| 5 | InternalInterfaceFilter | Remove BCL internal interfaces |
| 6 | ExplicitImplSynthesizer | Synthesize explicit implementation members |
| 7 | EnumeratorConformancePass | Promote Reset() for IEnumerator conformance |

### Hierarchy Resolution

| # | Pass | Purpose |
|---|------|---------|
| 8 | DiamondResolver | Resolve diamond inheritance conflicts |
| 9 | BaseOverloadAdder | Copy inherited overloads to derived types |
| 10 | StaticSideAnalyzer | Analyze static member inheritance |

### Member Planning

| # | Pass | Purpose |
|---|------|---------|
| 11 | IndexerPlanner | Plan indexer property handling |
| 12 | HiddenMemberPlanner | Handle C# 'new' keyword hiding |
| 13 | FinalIndexersPass | Final cleanup of indexer properties |
| 14 | ClassSurfaceDeduplicator | Deduplicate class surface members |

### Constraint & Conflict Resolution

| # | Pass | Purpose |
|---|------|---------|
| 15 | ConstraintCloser | Close generic constraint hierarchies |
| 16 | OverloadReturnConflictResolver | Resolve overload return type conflicts |

### View Planning

| # | Pass | Purpose |
|---|------|---------|
| 17 | ViewPlanner | Plan explicit interface views |
| 18 | MemberDeduplicator | Final member deduplication |

### Plan Phase Passes (Post-Shape)

| # | Pass | Purpose |
|---|------|---------|
| 19 | OverloadUnifier | Unify method overloads |
| 20 | InterfaceConstraintAuditor | Audit generic constraints per interface |
| 21 | StaticHierarchyFlattener | Plan static hierarchy flattening |
| 22 | StaticConflictDetector | Detect static member conflicts |
| 23 | OverrideConflictDetector | Detect override signature conflicts |
| 24 | PropertyOverrideUnifier | Unify covariant property types |
| 25 | ExtensionMethodAnalyzer | Analyze and bucket extension methods |
| 26 | SCCPlanner | Plan SCC buckets for circular deps |
| 27 | InterfaceConformanceAnalyzer | Analyze interface conformance |
| 28 | HonestEmissionPlanner | Plan honest emission (safe implements) |
| 29 | SafeToExtendAnalyzer | Determine safe interfaces to extend |

## Key Passes Explained

### StructuralConformance

Analyzes which interface members a type structurally satisfies.

**Problem:** TypeScript uses structural typing. A class method `add(item: T)` might structurally match `ICollection<T>.Add(T)` even if names differ.

**Solution:** Track conformance relationships. Members that don't structurally match are marked `ViewOnly`.

```
class List<T> implements ICollection<T>
  - add(item: T) structurally matches ICollection.Add(T) -> ClassSurface
  - copyTo(array: T[]) doesn't match ICollection.CopyTo -> ViewOnly
```

### InterfaceInliner

Flattens interface inheritance into a single member list.

**Problem:** Interface `IList<T>` extends `ICollection<T>` extends `IEnumerable<T>`. TypeScript needs all members visible.

**Solution:** Copy all inherited interface members to each interface's member list.

### ExplicitImplSynthesizer

Creates members for C# explicit interface implementations.

**C# Pattern:**
```csharp
class MyCollection : ICollection {
    void ICollection.CopyTo(Array array, int index) { }
}
```

**TypeScript Output:**
```typescript
interface __MyCollection$views {
    As_ICollection(): ICollection;
}
```

### DiamondResolver

Resolves diamond inheritance member conflicts.

**Problem:**
```
    IFoo
   /    \
IBar    IBaz
   \    /
  MyClass
```
Both `IBar` and `IBaz` may define conflicting members inherited from `IFoo`.

**Solution:** Pick one canonical declaration, mark others as ViewOnly.

### BaseOverloadAdder

Copies inherited method overloads to derived types.

**Problem:** TypeScript requires all overloads at the same declaration site. C# spreads them across hierarchy.

**Solution:**
```typescript
// All ToString overloads together
interface MyClass$instance {
    toString(): string;              // From Object
    toString(format: string): string; // From IFormattable
}
```

### IndexerPlanner

Plans handling of C# indexer properties.

**Problem:** C# indexers with different parameter types create TypeScript overload conflicts.

**Solution:** Track indexers in metadata, emit only when safe.

### ClassSurfaceDeduplicator

Deduplicates members on the class surface.

**Problem:** After inheritance flattening, multiple versions of same member may exist.

**Solution:** Pick winner based on specificity, mark others as duplicate.

### ViewPlanner

Plans explicit interface views (the `As_IInterface` pattern).

**Output:**
```typescript
interface __List_1$views<T> {
    As_IEnumerable_1(): IEnumerable_1<T>;
    As_ICollection(): ICollection;
}
```

Views provide access to interface members that don't structurally appear on the class surface.

### PropertyOverrideUnifier

Handles covariant property return types.

**Problem:**
```csharp
class Base { public virtual object Value { get; } }
class Derived : Base { public override string Value { get; } }
```

TypeScript doesn't support property overloading.

**Solution:** Emit union type:
```typescript
interface Derived$instance extends Base$instance {
    readonly value: string | object;
}
```

### ExtensionMethodAnalyzer

Buckets extension methods for C#-style "using" semantics.

**Input:** LINQ extension methods scattered across `Enumerable`, `Queryable`, etc.

**Output:** Emitted into `__internal/extensions/index.d.ts` as helper types:
```typescript
// __internal/extensions/index.d.ts (excerpt)
export interface __Ext_System_Linq_IEnumerable_1<T> {
    where(predicate: System.Func_2<T, boolean>): Rewrap<this, System_Collections_Generic.IEnumerable_1<T>>;
}

export type ExtensionMethods_System_Linq<TShape> =
    TShape & (TShape extends System_Collections_Generic.IEnumerable_1<infer T0> ? __Ext_System_Linq_IEnumerable_1<T0> : {});
```

Bucket method return types use `Rewrap<this, ReturnShape>` so extension scopes stay “sticky”
across fluent chains (similar to C# `using` semantics).

### SafeToExtendAnalyzer

Determines which interfaces can safely appear in `extends` clauses.

**Problem:** Some interface combinations cause TS2430 "Interface cannot simultaneously extend types".

**Solution:** Analyze structural compatibility. Safe interfaces go in `extends`, others become views.

## EmitScope Assignment

Shape passes assign `EmitScope` to each member:

| EmitScope | Meaning |
|-----------|---------|
| `ClassSurface` | Emit on instance interface |
| `StaticSurface` | Emit on static const |
| `ViewOnly` | Emit only in view interface |
| `Omitted` | Don't emit (tracked in metadata) |

## Transformation Flow

```
Raw SymbolGraph (from Load)
    │
    ▼ StructuralConformance
    │  └─ Members get SourceInterface, IsStructuralMatch
    ▼ InterfaceInliner
    │  └─ Interfaces flattened
    ▼ ExplicitImplSynthesizer
    │  └─ Explicit impl members synthesized
    ▼ DiamondResolver
    │  └─ Conflicts resolved, canonical members chosen
    ▼ BaseOverloadAdder
    │  └─ Inherited overloads copied
    ▼ ViewPlanner
    │  └─ ExplicitViews attached to types
    ▼
Shaped SymbolGraph (ready for naming)
```
