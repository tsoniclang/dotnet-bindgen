# Model (Data Structures)

The Model defines all data structures used throughout the pipeline.

## SymbolGraph

**File:** `Model/SymbolGraph.cs`

Central registry of all types:

```csharp
public sealed record SymbolGraph
{
    public ImmutableArray<NamespaceSymbol> Namespaces { get; }
    public IReadOnlyDictionary<string, TypeSymbol> TypeIndex { get; }

    public bool TryGetType(string clrFullName, out TypeSymbol? type);
}
```

## NamespaceSymbol

**File:** `Model/Symbols/NamespaceSymbol.cs`

```csharp
public sealed record NamespaceSymbol
{
    public string Name { get; }                              // "System.Collections.Generic"
    public bool IsRoot { get; }                              // true for empty namespace
    public ImmutableArray<TypeSymbol> Types { get; }
    public ImmutableArray<string> ContributingAssemblies { get; }
}
```

## TypeSymbol

**File:** `Model/Symbols/TypeSymbol.cs`

```csharp
public sealed record TypeSymbol
{
    // Identity
    public TypeStableId StableId { get; }
    public string ClrFullName { get; }                       // "System.Collections.Generic.List`1"
    public string TsEmitName { get; }                        // "List_1"
    public string Namespace { get; }
    public int Arity { get; }                                // Generic parameter count

    // Classification
    public TypeKind Kind { get; }                            // Class, Interface, Struct, Enum, Delegate
    public Accessibility Accessibility { get; }

    // Inheritance
    public TypeReference? BaseClass { get; }
    public ImmutableArray<TypeReference> Interfaces { get; }

    // Generics
    public ImmutableArray<GenericParameterSymbol> GenericParameters { get; }

    // Members
    public MemberCollection Members { get; }

    // Shape pass outputs
    public ImmutableArray<ExplicitView> ExplicitViews { get; }
}
```

## TypeKind

```csharp
public enum TypeKind
{
    Class,
    Interface,
    Struct,
    Enum,
    Delegate,
    StaticNamespace    // Static class (no instance)
}
```

## MemberCollection

```csharp
public sealed record MemberCollection
{
    public ImmutableArray<MethodSymbol> Methods { get; }
    public ImmutableArray<PropertySymbol> Properties { get; }
    public ImmutableArray<FieldSymbol> Fields { get; }
    public ImmutableArray<EventSymbol> Events { get; }
    public ImmutableArray<ConstructorSymbol> Constructors { get; }
}
```

## MethodSymbol

**File:** `Model/Symbols/MemberSymbols/MethodSymbol.cs`

```csharp
public sealed record MethodSymbol
{
    public MemberStableId StableId { get; }
    public string ClrName { get; }
    public string TsEmitName { get; }

    public TypeReference ReturnType { get; }
    public ImmutableArray<ParameterSymbol> Parameters { get; }
    public ImmutableArray<GenericParameterSymbol> GenericParameters { get; }

    public bool IsStatic { get; }
    public bool IsVirtual { get; }
    public bool IsAbstract { get; }
    public bool IsOverride { get; }

    public EmitScope EmitScope { get; }                      // ClassSurface, StaticSurface, ViewOnly
}
```

## EmitScope

Determines where a member appears in output:

```csharp
public enum EmitScope
{
    ClassSurface,    // On the $instance interface
    StaticSurface,   // On the $static interface / const
    ViewOnly,        // Only in explicit view interface
    Omitted          // Not emitted (indexers, etc.)
}
```

## TypeReference Hierarchy

**File:** `Model/Types/TypeReference.cs`

```csharp
public abstract record TypeReference;

public sealed record NamedTypeReference(
    string FullName,
    string Name,
    ImmutableArray<TypeReference> TypeArguments
) : TypeReference;

public sealed record GenericParameterReference(
    string Name
) : TypeReference;

public sealed record ArrayTypeReference(
    TypeReference ElementType,
    int Rank
) : TypeReference;

public sealed record PointerTypeReference(
    TypeReference ElementType
) : TypeReference;

public sealed record ByRefTypeReference(
    TypeReference ElementType
) : TypeReference;

public sealed record NestedTypeReference(
    NamedTypeReference FullReference,
    string NestedName
) : TypeReference;
```

## StableId

Unique identifier for symbols that survives transformations:

```csharp
public abstract record StableId;

public sealed record TypeStableId : StableId
{
    public string AssemblyName { get; }
    public string ClrFullName { get; }
}

public sealed record MemberStableId : StableId
{
    public TypeStableId DeclaringType { get; }
    public string MemberName { get; }
    public string Signature { get; }    // For overload disambiguation
}
```
