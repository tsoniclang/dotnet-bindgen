# Shape Phase

The Shape phase runs 23 transformation passes to prepare the symbol graph for TypeScript emission.

## Pass Overview

| Pass | File | Purpose |
|------|------|---------|
| StructuralConformance | `StructuralConformance.cs` | Analyze interface member conformance |
| ViewPlanner | `ViewPlanner.cs` | Plan explicit interface views |
| BaseOverloadAdder | `BaseOverloadAdder.cs` | Insert inherited method overloads |
| StaticHierarchyFlattener | `StaticHierarchyFlattener.cs` | Flatten static inheritance |
| StaticConflictDetector | `StaticConflictDetector.cs` | Detect static member conflicts |
| OverrideConflictDetector | `OverrideConflictDetector.cs` | Detect override signature conflicts |
| PropertyOverrideUnifier | `PropertyOverrideUnifier.cs` | Unify covariant property types |
| SafeToExtendAnalyzer | `SafeToExtendAnalyzer.cs` | LINQ assignability analysis |
| HonestImplementsAnalyzer | `HonestImplementsAnalyzer.cs` | TypeScript-safe implements |

## StructuralConformance

Analyzes which interface members a type structurally conforms to.

**Problem:** TypeScript uses structural typing. A class might have a method `add(item: T)` that matches `ICollection<T>.Add(T)`, but the names differ after camelCase transform.

**Solution:** Track which interface members each class member satisfies. Mark non-conforming members as `ViewOnly`.

```csharp
public static SymbolGraph Analyze(BuildContext ctx, SymbolGraph graph)
{
    foreach (var type in graph.AllTypes)
    {
        foreach (var iface in type.Interfaces)
        {
            // Check each interface member
            foreach (var ifaceMember in iface.Members)
            {
                var matching = FindMatchingMember(type, ifaceMember);
                if (matching == null)
                {
                    // No structural match - needs explicit view
                    MarkAsViewOnly(ifaceMember);
                }
            }
        }
    }
}
```

## ViewPlanner

Plans explicit interface views for members that don't structurally conform.

**Output:** `ExplicitView` records attached to each type:

```csharp
public sealed record ExplicitView(
    TypeReference InterfaceReference,
    string ViewPropertyName    // e.g., "As_IEnumerable_1"
);
```

**Example output:**

```typescript
interface __List_1$views<T> {
    As_IEnumerable_1(): IEnumerable_1<T>;
    As_ICollection(): ICollection;
}
```

## BaseOverloadAdder

Inserts inherited method overloads for TypeScript function overloading.

**Problem:** TypeScript function overloading requires all overloads at the same declaration site. C# inheritance spreads overloads across the hierarchy.

**Solution:** Copy base class overloads to derived class declarations.

```csharp
// C#: ToString() defined in Object, overloads in derived classes
// TypeScript needs all overloads together:
interface MyClass$instance {
    toString(): string;           // From Object
    toString(format: string): string;  // Added in MyClass
}
```

## StaticHierarchyFlattener

Flattens static member inheritance for TypeScript.

**Problem:** TypeScript abstract classes don't inherit static members the same way C# does.

**Solution:** Copy inherited static members to derived class static surfaces.

## SafeToExtendAnalyzer

Determines which interfaces a type can safely extend in TypeScript.

**Problem:** `extends` in TypeScript requires structural compatibility. Some interface combinations cause TS2430 errors due to conflicting member signatures.

**Solution:** Analyze each interface and determine if extending it would cause conflicts. Safe interfaces go in `extends`, others go in views.

```csharp
public sealed record SafeToExtendResult
{
    public IReadOnlyList<TypeReference> AssignableInterfaces { get; }
    public IReadOnlyList<TypeReference> ViewOnlyInterfaces { get; }
}
```

## PropertyOverrideUnifier

Handles covariant property return types.

**Problem:** C# allows `override` properties to return more specific types. TypeScript doesn't support property overloading.

**Example:**
```csharp
// C#
class Base { public virtual object Value { get; } }
class Derived : Base { public override string Value { get; } }
```

**Solution:** Emit union type for the property:

```typescript
interface Derived$instance extends Base$instance {
    readonly value: string | object;  // Union of all types in hierarchy
}
```

## Pass Execution Order

Passes run in dependency order:

1. StructuralConformance (analyzes raw graph)
2. ViewPlanner (uses conformance data)
3. BaseOverloadAdder (modifies members)
4. StaticHierarchyFlattener (modifies static surfaces)
5. StaticConflictDetector (validates static flattening)
6. OverrideConflictDetector (validates overrides)
7. PropertyOverrideUnifier (fixes property conflicts)
8. SafeToExtendAnalyzer (plans extends clauses)
9. HonestImplementsAnalyzer (validates implements)
