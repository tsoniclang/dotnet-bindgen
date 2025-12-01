# Model

This document details the data structures used throughout the tsbindgen pipeline.

## Overview

The Model layer defines immutable records that represent .NET type information. All model types are:

- **Immutable** - Records with `init`-only properties
- **Structural equality** - Records with value-based comparison
- **Deterministic** - Same input always produces same output

**Key Files:**
- `Model/SymbolGraph.cs` - Central type registry
- `Model/Symbols/TypeSymbol.cs` - Type representation
- `Model/Symbols/NamespaceSymbol.cs` - Namespace representation
- `Model/Types/TypeReference.cs` - Type reference hierarchy
- `Model/Symbols/MemberSymbols/*.cs` - Member representations
- `Model/AssemblyKey.cs` - Assembly identity

## SymbolGraph

The central type registry containing all loaded namespaces and types.

```csharp
public sealed record SymbolGraph
{
    public required ImmutableArray<NamespaceSymbol> Namespaces { get; init; }
    public required ImmutableHashSet<string> SourceAssemblies { get; init; }

    // Lookup indices (built by WithIndices)
    public ImmutableDictionary<string, NamespaceSymbol> NamespaceIndex { get; init; }
    public ImmutableDictionary<string, TypeSymbol> TypeIndex { get; init; }
}
```

### Key Methods

| Method | Description |
|--------|-------------|
| `WithIndices()` | Build lookup indices (call after construction) |
| `TryGetNamespace(name)` | Find namespace by name |
| `TryGetType(clrFullName)` | Find type by CLR full name |
| `IsEmittableType(stableId)` | Check if type will be emitted |
| `WithUpdatedType(key, transform)` | Pure update - returns new graph |

### Hierarchy

```
SymbolGraph
└─► Namespaces: NamespaceSymbol[]
    └─► Types: TypeSymbol[]
        ├─► Members: TypeMembers
        │   ├─► Methods: MethodSymbol[]
        │   ├─► Properties: PropertySymbol[]
        │   ├─► Fields: FieldSymbol[]
        │   ├─► Events: EventSymbol[]
        │   └─► Constructors: ConstructorSymbol[]
        └─► NestedTypes: TypeSymbol[]
```

## NamespaceSymbol

Represents a CLR namespace containing types.

```csharp
public sealed record NamespaceSymbol
{
    public required string Name { get; init; }
    public required ImmutableArray<TypeSymbol> Types { get; init; }
    public required TypeStableId StableId { get; init; }
    public required ImmutableHashSet<string> ContributingAssemblies { get; init; }
}
```

**Note:** Multiple assemblies can contribute types to the same namespace. `ContributingAssemblies` tracks all sources.

## TypeSymbol

Represents a type (class, struct, interface, enum, delegate).

```csharp
public sealed record TypeSymbol
{
    // Identity
    public required TypeStableId StableId { get; init; }
    public required string ClrFullName { get; init; }
    public required string ClrName { get; init; }
    public string TsEmitName { get; init; }  // Set by NameApplication

    // Classification
    public required TypeKind Kind { get; init; }
    public Accessibility Accessibility { get; init; }
    public required string Namespace { get; init; }

    // Type modifiers
    public required bool IsValueType { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsSealed { get; init; }
    public bool IsStatic { get; init; }

    // Generics
    public required int Arity { get; init; }
    public required ImmutableArray<GenericParameterSymbol> GenericParameters { get; init; }

    // Inheritance
    public TypeReference? BaseType { get; init; }
    public required ImmutableArray<TypeReference> Interfaces { get; init; }

    // Contents
    public required TypeMembers Members { get; init; }
    public required ImmutableArray<TypeSymbol> NestedTypes { get; init; }

    // Views (populated by ViewPlanner)
    public ImmutableArray<ExplicitView> ExplicitViews { get; init; }
}
```

### TypeKind

```csharp
public enum TypeKind
{
    Class,           // Regular class
    Struct,          // Value type
    Interface,       // Interface
    Enum,            // Enumeration
    Delegate,        // Delegate type
    StaticNamespace  // Static class (C# static class)
}
```

### Wither Methods

TypeSymbol provides pure transformation methods:

```csharp
type.WithMembers(members)           // Replace all members
type.WithAddedMethods(methods)      // Add methods
type.WithRemovedMethods(predicate)  // Remove methods matching predicate
type.WithTsEmitName(name)           // Set TypeScript name
type.WithExplicitViews(views)       // Set explicit interface views
```

## Member Symbols

### TypeMembers

Container for all members of a type:

```csharp
public sealed record TypeMembers
{
    public required ImmutableArray<MethodSymbol> Methods { get; init; }
    public required ImmutableArray<PropertySymbol> Properties { get; init; }
    public required ImmutableArray<FieldSymbol> Fields { get; init; }
    public required ImmutableArray<EventSymbol> Events { get; init; }
    public required ImmutableArray<ConstructorSymbol> Constructors { get; init; }

    public static readonly TypeMembers Empty;
}
```

### MethodSymbol

```csharp
public sealed record MethodSymbol
{
    // Identity
    public required MemberStableId StableId { get; init; }
    public required string ClrName { get; init; }
    public string TsEmitName { get; init; }

    // Signature
    public required TypeReference ReturnType { get; init; }
    public required ImmutableArray<ParameterSymbol> Parameters { get; init; }
    public required ImmutableArray<GenericParameterSymbol> GenericParameters { get; init; }

    // Modifiers
    public required bool IsStatic { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsOverride { get; init; }
    public bool IsSealed { get; init; }
    public bool IsNew { get; init; }

    // Extension methods
    public bool IsExtensionMethod { get; init; }
    public TypeReference? ExtensionTarget { get; init; }

    // Classification
    public required Visibility Visibility { get; init; }
    public required MemberProvenance Provenance { get; init; }
    public EmitScope EmitScope { get; init; }
    public TypeReference? SourceInterface { get; init; }
}
```

### PropertySymbol

```csharp
public sealed record PropertySymbol
{
    // Identity
    public required MemberStableId StableId { get; init; }
    public required string ClrName { get; init; }
    public string TsEmitName { get; init; }

    // Type
    public required TypeReference PropertyType { get; init; }
    public required ImmutableArray<ParameterSymbol> IndexParameters { get; init; }

    // Accessors
    public required bool HasGetter { get; init; }
    public required bool HasSetter { get; init; }

    // Modifiers
    public required bool IsStatic { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsOverride { get; init; }
    public bool IsAbstract { get; init; }

    // Classification
    public required Visibility Visibility { get; init; }
    public required MemberProvenance Provenance { get; init; }
    public EmitScope EmitScope { get; init; }
    public TypeReference? SourceInterface { get; init; }
}
```

### FieldSymbol

```csharp
public sealed record FieldSymbol
{
    public required MemberStableId StableId { get; init; }
    public required string ClrName { get; init; }
    public string TsEmitName { get; init; }

    public required TypeReference FieldType { get; init; }

    public required bool IsStatic { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsConst { get; init; }
    public object? ConstValue { get; init; }

    public required Visibility Visibility { get; init; }
    public required MemberProvenance Provenance { get; init; }
    public EmitScope EmitScope { get; init; }
}
```

### ParameterSymbol

```csharp
public sealed record ParameterSymbol
{
    public required string Name { get; init; }
    public required TypeReference Type { get; init; }

    public bool IsRef { get; init; }      // ref parameter
    public bool IsOut { get; init; }      // out parameter
    public bool IsParams { get; init; }   // params array

    public bool HasDefaultValue { get; init; }
    public object? DefaultValue { get; init; }
}
```

## Member Classification

### MemberProvenance

Tracks where a member came from:

```csharp
public enum MemberProvenance
{
    Original,           // Declared in this type
    FromInterface,      // Copied from implemented interface
    Synthesized,        // Created by shaper
    HiddenNew,          // Added for C# 'new' hiding
    BaseOverload,       // Base class overload
    DiamondResolved,    // Diamond inheritance resolution
    IndexerNormalized,  // From indexer syntax
    ExplicitView,       // Explicit interface view
    OverloadReturnConflict  // ViewOnly due to return type conflict
}
```

### EmitScope

Determines where a member is emitted:

```csharp
public enum EmitScope
{
    Unspecified,    // Not yet decided (error if reaches emission)
    ClassSurface,   // Main interface body
    StaticSurface,  // Static class body
    ViewOnly,       // Only in explicit interface views
    Omitted         // Not emitted (unified away)
}
```

**Important:** `EmitScope.Unspecified` MUST be resolved before emission. PhaseGate validates this with PG_FIN_001.

## Type References

The `TypeReference` hierarchy represents references to types:

```
TypeReference (abstract)
├─► NamedTypeReference       // Class, struct, interface, etc.
├─► GenericParameterReference // T, TKey, TValue
├─► ArrayTypeReference       // T[]
├─► PointerTypeReference     // T*
├─► ByRefTypeReference       // ref T
├─► NestedTypeReference      // Outer.Inner
└─► PlaceholderTypeReference // Cycle breaker (internal)
```

### NamedTypeReference

Reference to a named type (most common):

```csharp
public sealed record NamedTypeReference : TypeReference
{
    public required string AssemblyName { get; init; }
    public required string FullName { get; init; }
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public required int Arity { get; init; }
    public required IReadOnlyList<TypeReference> TypeArguments { get; init; }
    public required bool IsValueType { get; init; }

    // For interfaces: pre-computed StableId
    public string? InterfaceStableId { get; init; }
}
```

### GenericParameterReference

Reference to a type parameter:

```csharp
public sealed record GenericParameterReference : TypeReference
{
    public required GenericParameterId Id { get; init; }
    public required string Name { get; init; }
    public required int Position { get; init; }
    public required IReadOnlyList<TypeReference> Constraints { get; init; }
}
```

### ArrayTypeReference

```csharp
public sealed record ArrayTypeReference : TypeReference
{
    public required TypeReference ElementType { get; init; }
    public required int Rank { get; init; }  // 1 for T[], 2 for T[,]
}
```

### PlaceholderTypeReference

Internal type used to break recursion cycles during type graph construction:

```csharp
public sealed record PlaceholderTypeReference : TypeReference
{
    public required string DebugName { get; init; }
}
```

**Note:** If this appears in final output, printers emit `any` with a diagnostic warning. Should never happen in well-formed graphs.

## Generic Parameters

### GenericParameterSymbol

Declared generic parameter on a type or method:

```csharp
public sealed record GenericParameterSymbol
{
    public required GenericParameterId Id { get; init; }
    public required string Name { get; init; }
    public required int Position { get; init; }
    public required ImmutableArray<TypeReference> Constraints { get; init; }

    // Raw constraints from reflection (resolved by ConstraintCloser)
    public System.Type[]? RawConstraintTypes { get; init; }

    public Variance Variance { get; init; }
    public GenericParameterConstraints SpecialConstraints { get; init; }
}
```

### Variance

```csharp
public enum Variance
{
    None,           // Invariant
    Covariant,      // out T
    Contravariant   // in T
}
```

### GenericParameterConstraints

```csharp
[Flags]
public enum GenericParameterConstraints
{
    None = 0,
    ReferenceType = 1,      // class constraint
    ValueType = 2,          // struct constraint
    DefaultConstructor = 4, // new() constraint
    NotNullable = 8         // notnull constraint
}
```

## Stable Identifiers

### TypeStableId

Stable identifier for types across transformations:

```csharp
public sealed record TypeStableId
{
    public required string AssemblyName { get; init; }
    public required string ClrFullName { get; init; }

    // Format: "AssemblyName:ClrFullName"
    public override string ToString() => $"{AssemblyName}:{ClrFullName}";
}
```

### MemberStableId

Stable identifier for members:

```csharp
public sealed record MemberStableId
{
    public required string AssemblyName { get; init; }
    public required string DeclaringClrFullName { get; init; }
    public required string MemberName { get; init; }
    public required string CanonicalSignature { get; init; }
    public int MetadataToken { get; init; }
}
```

## AssemblyKey

Used for assembly identity during loading:

```csharp
public sealed record AssemblyKey(
    string Name,
    string PublicKeyToken,
    string Culture,
    string Version)
{
    public static AssemblyKey From(AssemblyName name);
}
```

## Immutability Patterns

All model types use the C# record pattern for immutability:

```csharp
// Creating modified copies with 'with' expressions
var updated = original with { TsEmitName = "NewName" };

// Using wither methods
var withMethods = type.WithAddedMethods(newMethods);

// SymbolGraph pure updates
var newGraph = graph.WithUpdatedType(clrName, t => t with { ... });
```

## Graph Statistics

```csharp
public sealed record SymbolGraphStatistics
{
    public required int NamespaceCount { get; init; }
    public required int TypeCount { get; init; }
    public required int MethodCount { get; init; }
    public required int PropertyCount { get; init; }
    public required int FieldCount { get; init; }
    public required int EventCount { get; init; }

    public int TotalMembers => MethodCount + PropertyCount + FieldCount + EventCount;
}
```

Retrieve with `graph.GetStatistics()`.
