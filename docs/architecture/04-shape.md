# Shape Phase

This document details the Shape phase, which transforms the symbol graph for TypeScript emission through a series of passes.

## Overview

**Purpose:** Transform the raw CLR symbol graph into a form suitable for TypeScript emission

**Input:** SymbolGraph with indices
**Output:** Transformed SymbolGraph with EmitScope assigned

**Key Files:**
- `Shape/InterfaceInliner.cs` - Flatten interface hierarchies
- `Shape/StructuralConformance.cs` - Analyze TypeScript structural typing
- `Shape/ViewPlanner.cs` - Plan explicit interface views
- `Shape/DiamondResolver.cs` - Handle diamond inheritance
- `Shape/BaseOverloadAdder.cs` - Add inherited overloads
- `Shape/*.cs` - 14+ transformation passes

## Pass Order

The Shape phase runs passes in a specific order. Dependencies between passes require this ordering:

```
1.  GlobalInterfaceIndex.Build()      # Build lookup indices
2.  InterfaceDeclIndex.Build()
3.  StructuralConformance.Analyze()   # Synthesize ViewOnly members
4.  InterfaceInliner.Inline()         # Flatten interface hierarchies
5.  InternalInterfaceFilter.Filter()  # Remove BCL internal interfaces
6.  ExplicitImplSynthesizer.Synthesize()
7.  EnumeratorConformancePass.Run()
8.  DiamondResolver.Resolve()
9.  BaseOverloadAdder.AddOverloads()
10. StaticSideAnalyzer.Analyze()
11. IndexerPlanner.Plan()
12. HiddenMemberPlanner.Plan()
13. FinalIndexersPass.Run()
14. ClassSurfaceDeduplicator.Deduplicate()
15. ConstraintCloser.Close()
16. OverloadReturnConflictResolver.Resolve()
17. ViewPlanner.Plan()
18. MemberDeduplicator.Deduplicate()
```

## Key Passes

### InterfaceInliner

**Purpose:** Flatten interface inheritance hierarchies

**Problem:** TypeScript interfaces with `extends` must satisfy the LSP (Liskov Substitution Principle). C# interface implementation is more flexible.

**Solution:** Inline all inherited members directly into each interface, removing the need for `extends` chains.

```
IList<T> : ICollection<T> : IEnumerable<T>
    │
    ▼ After InterfaceInliner

IList<T> (contains all members from ICollection + IEnumerable)
```

**Algorithm:**
1. Walk interface inheritance hierarchy via BFS
2. Build generic substitution maps for closed generics
3. Substitute type parameters in member signatures
4. Deduplicate members by canonical signature
5. Update interface with flattened members

**Example - Generic Substitution:**
```csharp
// C#: IDictionary<TKey, TValue> : ICollection<KeyValuePair<TKey, TValue>>
// Interface: ICollection<T> with T → KeyValuePair<TKey, TValue>

// Original: Add(T item)
// Substituted: Add(KeyValuePair<TKey, TValue> item)
```

### StructuralConformance

**Purpose:** Analyze TypeScript structural typing constraints and synthesize ViewOnly members

**Problem:** C# classes can implement interfaces with explicit implementations (private implementations). TypeScript uses structural typing, so a class must have all interface members visible.

**Solution:** For members that can't be structurally satisfied, synthesize ViewOnly members that appear only in explicit interface views.

```
Analyze(ctx, graph)
    │
    ├─► For each class/struct:
    │   ├─► Build class surface (visible members)
    │   │
    │   └─► For each implemented interface:
    │       ├─► Build interface surface (after substitution)
    │       │
    │       └─► For each interface member:
    │           ├─► Check TypeScript assignability
    │           │
    │           └─► If NOT satisfied:
    │               └─► Synthesize ViewOnly member
```

**TypeScript Assignability Check:**
- Method names match (case-insensitive for camelCase)
- Parameter count compatible
- Return type compatible
- Instance vs static match

**Output:** ViewOnly members with `EmitScope.ViewOnly` and `SourceInterface` set

### ViewPlanner

**Purpose:** Create explicit interface views (`As_IInterface` properties)

**Problem:** ViewOnly members need to be exposed via a mechanism that allows TypeScript code to access them when needed.

**Solution:** Create `As_IInterface` accessor properties that return a view type containing only the interface's members.

```
Plan(ctx, graph)
    │
    └─► For each class/struct with ViewOnly members:
        │
        └─► Group ViewOnly members by SourceInterface
            │
            └─► Create ExplicitView:
                ├─► InterfaceReference
                ├─► ViewPropertyName (e.g., "As_IConvertible")
                └─► ViewMembers (list of member references)
```

**ExplicitView Structure:**
```csharp
public sealed record ExplicitView(
    TypeReference InterfaceReference,
    string ViewPropertyName,
    ImmutableArray<ViewMember> ViewMembers);
```

**Emitted Pattern:**
```typescript
interface Decimal$instance {
    // Regular members on class surface
    // ...

    // Explicit interface view accessor
    readonly As_IConvertible: __Decimal$As_IConvertible;
}

interface __Decimal$As_IConvertible {
    toBoolean(provider: IFormatProvider): boolean;
    toByte(provider: IFormatProvider): byte;
    // ... ViewOnly members
}
```

### DiamondResolver

**Purpose:** Handle diamond inheritance patterns

**Problem:** When a class implements multiple interfaces that share a common base, member conflicts can occur.

```
        IBase
       /      \
   ILeft     IRight
       \      /
        Derived
```

**Solution:** Detect diamond patterns and deduplicate members to prevent duplicate declarations.

### BaseOverloadAdder

**Purpose:** Add inherited method overloads from base classes

**Problem:** TypeScript allows method overloads, and derived classes should expose all inherited overloads.

**Solution:** Walk the inheritance hierarchy and add any base class overloads that aren't already declared in the derived class.

### IndexerPlanner

**Purpose:** Plan indexer emission

**Problem:** C# indexers (`this[int index]`) can have multiple overloads with different parameter types, which TypeScript doesn't support well.

**Solution:** Track indexers separately and emit as methods (`get_Item`/`set_Item`) or signature overloads.

### ConstraintCloser

**Purpose:** Resolve generic parameter constraints

**Problem:** Generic constraints reference types that may be circular (`T : IComparable<T>`).

**Solution:** Deferred constraint resolution that breaks cycles and produces valid TypeScript constraints.

## EmitScope Assignment

Every member MUST have its `EmitScope` explicitly set before emission:

| EmitScope | Meaning |
|-----------|---------|
| `ClassSurface` | Emit on main interface body |
| `StaticSurface` | Emit on static class body |
| `ViewOnly` | Only emit in explicit interface views |
| `Omitted` | Don't emit (unified away) |
| `Unspecified` | **ERROR** - not yet decided |

**Pass Responsibilities:**

| Pass | EmitScope Assignment |
|------|---------------------|
| `ReflectionReader` | Sets `ClassSurface` for all original members |
| `StructuralConformance` | Sets `ViewOnly` for synthesized view members |
| `OverloadUnifier` | Sets `Omitted` for unified overloads |
| `ClassSurfaceDeduplicator` | Demotes duplicates to `ViewOnly` |

## Structural Typing

### TypeScript Structural Compatibility

TypeScript uses structural typing. For a class to satisfy an interface:

```typescript
// TypeScript structural typing
interface IComparable<T> {
    compareTo(other: T): int;
}

// Class must have a compatible method (same name, compatible signature)
class MyClass implements IComparable<MyClass> {
    compareTo(other: MyClass): int { ... }  // ✓ Structurally compatible
}
```

### C# Explicit Interface Implementation

C# allows explicit interface implementations that are NOT visible on the class surface:

```csharp
// C# explicit implementation
class Decimal : IConvertible {
    // Explicit - NOT visible as Decimal.ToBoolean()
    bool IConvertible.ToBoolean(IFormatProvider provider) { ... }
}
```

In TypeScript, this would fail structural typing because `Decimal` doesn't have a visible `toBoolean` method.

### tsbindgen Solution

1. **StructuralConformance** detects non-satisfied interface members
2. **ViewOnly members** are synthesized with `EmitScope.ViewOnly`
3. **ViewPlanner** creates explicit view accessors
4. **Emission** generates view interfaces and accessor properties

## Index Building

### GlobalInterfaceIndex

Built before flattening to preserve original interface hierarchy:

```csharp
// Maps: TypeStableId → List of direct interface references
Dictionary<string, List<TypeReference>> DirectInterfaces;

// Maps: TypeStableId → List of all transitive interface references
Dictionary<string, List<TypeReference>> AllInterfaces;
```

### InterfaceDeclIndex

Maps interface members to their declaring interface for SourceInterface tracking:

```csharp
// Maps: (InterfaceStableId, MemberSignature) → DeclaringInterfaceRef
Dictionary<(string, string), TypeReference> MemberDeclarations;
```

## Static Analysis Passes

### StaticHierarchyFlattener

**Problem:** Static members in derived classes that shadow base class members cause TS2417 errors.

**Solution:** Plan static member flattening to prevent shadowing.

### StaticConflictDetector

**Problem:** Different static members with same name in base/derived classes.

**Solution:** Detect conflicts and mark for suppression or renaming.

### OverrideConflictDetector

**Problem:** Instance member overrides with incompatible signatures cause TS2416 errors.

**Solution:** Detect conflicts for property override unification.

### PropertyOverrideUnifier

**Problem:** Property covariance (derived property returns more specific type) causes TS2416.

**Solution:** Unify property types to union of all types in hierarchy.

## Deduplication Passes

### ClassSurfaceDeduplicator

Picks a single "winner" when multiple members would have the same TypeScript name:

```
Deduplicate(ctx, graph)
    │
    └─► For each type:
        │
        └─► For each duplicate name group:
            ├─► Pick winner (by provenance priority)
            └─► Demote others to ViewOnly
```

**Priority order:**
1. Original declarations
2. Non-interface provenance
3. First by stable ordering

### MemberDeduplicator

Final safety pass to remove any duplicate StableIds introduced by Shape passes:

```
Deduplicate(ctx, graph)
    │
    └─► For each type:
        └─► Group members by StableId
            └─► Keep first occurrence, remove duplicates
```

## Pure Transformations

All Shape passes are PURE functions - they return new `SymbolGraph` instances:

```csharp
// Each pass signature
public static SymbolGraph PassName(BuildContext ctx, SymbolGraph graph)
{
    // Process...
    return graph with { Namespaces = updatedNamespaces };
}
```

The original graph is never mutated. This ensures:
- Deterministic output
- Easy debugging (can snapshot at any point)
- Parallel processing potential

## Call Graph

```
Builder.ShapePhase()
    │
    ├─► GlobalInterfaceIndex.Build()
    ├─► InterfaceDeclIndex.Build()
    │
    ├─► StructuralConformance.Analyze()
    │   └─► For each type: AnalyzeType()
    │       ├─► BuildClassSurface()
    │       ├─► BuildInterfaceSurface()
    │       └─► SynthesizeViewOnlyMethod/Property()
    │
    ├─► InterfaceInliner.Inline()
    │   └─► For each interface: InlineInterface()
    │       ├─► BuildSubstitutionMapForInterface()
    │       └─► SubstituteMethodMembers()
    │
    ├─► InternalInterfaceFilter.FilterGraph()
    ├─► ExplicitImplSynthesizer.Synthesize()
    ├─► EnumeratorConformancePass.Run()
    ├─► DiamondResolver.Resolve()
    ├─► BaseOverloadAdder.AddOverloads()
    ├─► StaticSideAnalyzer.Analyze()
    ├─► IndexerPlanner.Plan()
    ├─► HiddenMemberPlanner.Plan()
    ├─► FinalIndexersPass.Run()
    ├─► ClassSurfaceDeduplicator.Deduplicate()
    ├─► ConstraintCloser.Close()
    ├─► OverloadReturnConflictResolver.Resolve()
    │
    ├─► ViewPlanner.Plan()
    │   └─► For each type: PlanViewsForType()
    │       ├─► Group ViewOnly by SourceInterface
    │       └─► Create ExplicitView records
    │
    └─► MemberDeduplicator.Deduplicate()
```
