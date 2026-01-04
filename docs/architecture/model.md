# Model (Data Structures)

The Model defines all data structures used throughout the pipeline. These are immutable records that flow from Load through Emit.

## SymbolGraph

**File:** `Model/SymbolGraph.cs`

Central registry of all types. The SymbolGraph is the primary data structure passed between phases.

```csharp
public sealed record SymbolGraph
{
    public ImmutableArray<NamespaceSymbol> Namespaces { get; }
    public IReadOnlyDictionary<string, TypeSymbol> TypeIndex { get; }

    public bool TryGetType(string clrFullName, out TypeSymbol? type);
    public SymbolGraph WithIndices();  // Rebuild lookup indices
}
```

**Key invariants:**
- TypeIndex is keyed by CLR full name (e.g., `System.Collections.Generic.List`1`)
- Each namespace appears once
- Types are unique by StableId

## NamespaceSymbol

**File:** `Model/Symbols/NamespaceSymbol.cs`

```csharp
public sealed record NamespaceSymbol
{
    public string Name { get; }                              // "System.Collections.Generic"
    public bool IsRoot { get; }                              // true for empty namespace
    public ImmutableArray<TypeSymbol> Types { get; }
    public ImmutableArray<string> ContributingAssemblies { get; }  // Which DLLs contributed types
}
```

## TypeSymbol

**File:** `Model/Symbols/TypeSymbol.cs`

Represents a CLR type (class, interface, struct, enum, delegate).

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
    public bool IsAbstract { get; }
    public bool IsSealed { get; }
    public bool IsStatic { get; }

    // Inheritance
    public TypeReference? BaseClass { get; }
    public ImmutableArray<TypeReference> Interfaces { get; }

    // Generics
    public ImmutableArray<GenericParameterSymbol> GenericParameters { get; }

    // Members
    public MemberCollection Members { get; }

    // Shape pass outputs
    public ImmutableArray<ExplicitView> ExplicitViews { get; }  // From ViewPlanner
}
```

## TypeKind

Classification of CLR types:

```csharp
public enum TypeKind
{
    Class,              // Reference type with implementation
    Interface,          // Contract only
    Struct,             // Value type
    Enum,               // Enumeration
    Delegate,           // Callable type
    StaticNamespace     // Static class (no instance members)
}
```

**TypeScript mapping:**

| TypeKind | TypeScript Pattern |
|----------|-------------------|
| Class | `interface + const` |
| Interface | `interface` |
| Struct | `interface + const` |
| Enum | `const enum` |
| Delegate | `type = (...) => R` |
| StaticNamespace | `abstract class` |

## MemberCollection

Container for all member types on a TypeSymbol:

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
    public bool IsExtensionMethod { get; }

    public EmitScope EmitScope { get; }
    public TypeReference? SourceInterface { get; }  // If from interface
}
```

## EmitScope

Determines where a member appears in TypeScript output:

```csharp
public enum EmitScope
{
    Unspecified,    // Not yet decided (invalid after Shape)
    ClassSurface,   // On the $instance interface
    StaticSurface,  // On the static const declaration
    ViewOnly,       // Only in __$views interface (explicit impl)
    Omitted         // Not emitted to .d.ts (tracked in metadata)
}
```

### EmitScope Assignment

Shape passes assign EmitScope based on analysis:

| Scenario | EmitScope |
|----------|-----------|
| Normal instance method | ClassSurface |
| Static method | StaticSurface |
| Explicit interface impl | ViewOnly |
| Indexer (conflicts) | Omitted |
| Generic static member | Omitted |

### EmitScope in Output

```typescript
// ClassSurface -> $instance interface
export interface List_1$instance<T> {
    add(item: T): void;        // EmitScope.ClassSurface
    readonly count: int;       // EmitScope.ClassSurface
}

// StaticSurface -> const declaration
export declare const List_1: {
    new <T>(): List_1<T>;     // Constructor
    empty<T>(): List_1<T>;    // EmitScope.StaticSurface
};

// ViewOnly -> __$views interface
export interface __List_1$views<T> {
    As_ICollection(): ICollection;  // EmitScope.ViewOnly
}
```

## TypeReference Hierarchy

**File:** `Model/Types/TypeReference.cs`

Abstract base for all type references. Used in signatures, constraints, and inheritance.

```csharp
public abstract record TypeReference;
```

### NamedTypeReference

Reference to a named type (class, interface, struct, enum, delegate):

```csharp
public sealed record NamedTypeReference(
    string AssemblyName,                         // "System.Private.CoreLib"
    string FullName,                             // "System.Collections.Generic.List`1"
    string Name,                                 // "List`1"
    ImmutableArray<TypeReference> TypeArguments  // Generic arguments
) : TypeReference;
```

**Examples:**
- `List<string>` -> FullName: `System.Collections.Generic.List`1`, TypeArguments: [`string`]
- `Dictionary<int, string>` -> FullName: `...Dictionary`2`, TypeArguments: [`int`, `string`]

### GenericParameterReference

Reference to a generic type parameter (T, TKey, etc.):

```csharp
public sealed record GenericParameterReference(
    string Name,              // "T", "TKey", "TResult"
    int Position,             // 0-based position
    bool IsMethodParameter    // true if method-level, false if type-level
) : TypeReference;
```

### ArrayTypeReference

Reference to an array type:

```csharp
public sealed record ArrayTypeReference(
    TypeReference ElementType,
    int Rank                   // 1 for T[], 2 for T[,], etc.
) : TypeReference;
```

### PointerTypeReference

Reference to a pointer type (unsafe):

```csharp
public sealed record PointerTypeReference(
    TypeReference ElementType  // The pointed-to type
) : TypeReference;
```

Maps to `ptr<T>` in TypeScript (from `@tsonic/core/types.js`).

### ByRefTypeReference

Reference to a by-reference type (ref/out/in parameters):

```csharp
public sealed record ByRefTypeReference(
    TypeReference ElementType,
    RefKind Kind               // Ref, Out, In
) : TypeReference;
```

Tracked in `metadata.json` via `parameterModifiers` (ref/out/in). The emitted `.d.ts` uses the element type directly.

### NullableTypeReference

Reference to a nullable type:

```csharp
public sealed record NullableTypeReference(
    TypeReference UnderlyingType
) : TypeReference;
```

Maps to `T | null` in TypeScript.

## StableId

Unique identifier for symbols that survives transformations. Used as dictionary keys and for tracking across phases.

### TypeStableId

```csharp
public sealed record TypeStableId
{
    public string AssemblyName { get; }   // "System.Private.CoreLib"
    public string ClrFullName { get; }    // "System.Collections.Generic.List`1"

    // Format: "AssemblyName:ClrFullName"
    public override string ToString() => $"{AssemblyName}:{ClrFullName}";
}
```

### MemberStableId

```csharp
public sealed record MemberStableId
{
    public string DeclaringClrType { get; }    // "System.Collections.Generic.List`1"
    public string CanonicalSignature { get; }  // "Add(T):void"
    public int MetadataToken { get; }          // CLR metadata token

    // Format: "DeclaringType::Signature (Token)"
}
```

**Canonical Signature Format:**
- Methods: `MethodName(ParamType1,ParamType2):ReturnType`
- Properties: `PropertyName|PropertyType`
- Fields: `FieldName|FieldType`

## ExplicitView

Output from ViewPlanner - represents an `As_IInterface` property:

```csharp
public sealed record ExplicitView(
    TypeReference InterfaceReference,  // The interface being viewed
    string ViewPropertyName,           // "As_IEnumerable_1"
    ImmutableArray<MemberStableId> Members  // Members in this view
);
```

## GenericParameterSymbol

Represents a generic type parameter with constraints:

```csharp
public sealed record GenericParameterSymbol
{
    public string Name { get; }                              // "T", "TKey"
    public int Position { get; }                             // 0-based
    public GenericParameterConstraints Constraints { get; }
}

public sealed record GenericParameterConstraints
{
    public bool HasReferenceTypeConstraint { get; }    // where T : class
    public bool HasValueTypeConstraint { get; }        // where T : struct
    public bool HasDefaultConstructorConstraint { get } // where T : new()
    public ImmutableArray<TypeReference> TypeConstraints { get; }  // where T : IFoo
}
```
