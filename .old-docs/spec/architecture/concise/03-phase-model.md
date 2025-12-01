# Phase 3: Model - Data Structures

## Overview

The **Model** defines immutable data structures representing the complete symbol graph for all loaded assemblies. Created during **Load** phase, transformed during **Shape** phase.

Provides:
1. Type-safe representation of all CLR types and members
2. Stable identities for tracking through transformations
3. Type references capturing full type system (generics, arrays, pointers, etc.)
4. Provenance tracking for member origins
5. Emit scope control for placement decisions

All Model structures are **immutable records** with structural equality, supporting pure functional transformations via `with` expressions.

---

## File: SymbolGraph.cs

### Record: SymbolGraph

**Properties:**
- **`Namespaces: ImmutableArray<NamespaceSymbol>`** - All namespaces with types (primary hierarchical structure)
- **`SourceAssemblies: ImmutableHashSet<string>`** - Source assembly paths (tracking/diagnostics)
- **`NamespaceIndex: ImmutableDictionary<string, NamespaceSymbol>`** - O(1) namespace lookups by name
- **`TypeIndex: ImmutableDictionary<string, TypeSymbol>`** - O(1) type lookups by CLR full name (includes nested types)

**Key Methods:**
- **`WithIndices: SymbolGraph`** - Returns new graph with populated indices (MUST call after creation)
- **`TryGetNamespace(name, out ns): bool`** - Safe namespace lookup
- **`TryGetType(clrFullName, out type): bool`** - Safe type lookup (works for nested)
- **`WithUpdatedType(keyOrStableId, transform): SymbolGraph`** - Pure function to update single type, automatically rebuilds indices
- **`GetStatistics: SymbolGraphStatistics`** - Calculate namespace/type/member counts

---

## File: AssemblyKey.cs

### Record Struct: AssemblyKey

```csharp
public readonly record struct AssemblyKey(
    string Name,              // Simple name (e.g., "System.Private.CoreLib")
    string PublicKeyToken,    // Hex string or "null" if unsigned
    string Culture,           // Culture name or "neutral"
    string Version)           // "Major.Minor.Build.Revision"
```

**Methods:**
- **`static From(AssemblyName): AssemblyKey`** - Creates normalized key with proper defaults
- **`ToString: string`** - Full GAC format identity string

**Extension:**
- **`ToHexString(byte[]): string`** - Converts PublicKeyToken bytes to lowercase hex (returns "null" for empty)

---

## File: Symbols/NamespaceSymbol.cs

### Record: NamespaceSymbol

**Properties:**
- **`Name: string`** - Namespace name (empty for root/global)
- **`Types: ImmutableArray<TypeSymbol>`** - All types in this namespace (not from nested namespaces)
- **`StableId: StableId`** - Stable identifier
- **`ContributingAssemblies: ImmutableHashSet<string>`** - Assembly names contributing types

**Computed:**
- **`IsRoot: bool`** - True if root/global namespace
- **`SafeNameOrNull: string?`** - Namespace name or null if root

---

## File: Symbols/TypeSymbol.cs

### Record: TypeSymbol

**Identity:**
- **`StableId: TypeStableId`** - `"AssemblyName:ClrFullName"` (e.g., `"CoreLib:System.Collections.Generic.List\`1"`)
- **`ClrFullName: string`** - Full CLR name with namespace (e.g., `"System.Collections.Generic.List\`1"`)
- **`ClrName: string`** - Simple name without namespace (e.g., `"List\`1"`)
- **`TsEmitName: string`** - TypeScript emit name (set by NameApplication, e.g., `"List_1"`)

**Classification:**
- **`Namespace: string`** - Containing namespace
- **`Kind: TypeKind`** - Class, Struct, Interface, Enum, Delegate, StaticNamespace
- **`Accessibility: Accessibility`** - Public, Protected, Internal, etc.

**Generics:**
- **`Arity: int`** - Generic arity (0 for non-generic)
- **`GenericParameters: ImmutableArray<GenericParameterSymbol>`** - Generic parameters declared by this type

**Hierarchy:**
- **`BaseType: TypeReference?`** - Base type (null for interfaces, Object, ValueType)
- **`Interfaces: ImmutableArray<TypeReference>`** - Directly implemented interfaces (Shape phase may add more)

**Members:**
- **`Members: TypeMembers`** - All members (methods, properties, fields, events, constructors)
- **`NestedTypes: ImmutableArray<TypeSymbol>`** - Nested types (recursive)

**Characteristics:**
- **`IsValueType, IsAbstract, IsSealed, IsStatic: bool`**
- **`DeclaringType: TypeSymbol?`** - Declaring type for nested (null for top-level)

**Shape Phase:**
- **`ExplicitViews: ImmutableArray<ExplicitView>`** - Explicit interface views (As_IInterface properties)

**Wither Methods:**
- `WithMembers`, `WithAddedMethods`, `WithRemovedMethods`, `WithAddedProperties`, `WithRemovedProperties`, `WithAddedFields`, `WithTsEmitName`, `WithExplicitViews`

### Enum: TypeKind
- `Class, Struct, Interface, Enum, Delegate, StaticNamespace`

### Record: GenericParameterSymbol

**Properties:**
- **`Id: GenericParameterId`** - Unique identifier (declaring type + position)
- **`Name: string`** - Parameter name (e.g., "T", "TKey")
- **`Position: int`** - Zero-based position in parameter list
- **`Constraints: ImmutableArray<TypeReference>`** - Type constraints (resolved by ConstraintCloser)
- **`RawConstraintTypes: System.Type[]?`** - Raw CLR constraints from reflection (null after resolution)
- **`Variance: Variance`** - None, Covariant (out T), Contravariant (in T)
- **`SpecialConstraints: GenericParameterConstraints`** - Flags (struct, class, new, notnull)

### Record: TypeMembers

**Properties:**
- `Methods, Properties, Fields, Events, Constructors: ImmutableArray<*Symbol>`

**Static:**
- `Empty: TypeMembers` - Pre-constructed empty instance

---

## File: Symbols/MemberSymbols/MethodSymbol.cs

### Record: MethodSymbol

**Identity:**
- **`StableId: MemberStableId`** - Assembly-qualified signature
- **`ClrName: string`** - CLR method name (may be qualified for explicit interface impls)
- **`TsEmitName: string`** - TypeScript emit name

**Signature:**
- **`ReturnType: TypeReference`** - Return type
- **`Parameters: ImmutableArray<ParameterSymbol>`** - Parameters
- **`GenericParameters: ImmutableArray<GenericParameterSymbol>`** - Method generic parameters
- **`Arity: int`** - Generic arity (0 for non-generic)

**Characteristics:**
- **`IsStatic, IsAbstract, IsVirtual, IsSealed, IsOverride: bool`**
- **`Visibility: Visibility`** - Public, Protected, Internal, etc.
- **`IsExtensionMethod: bool`** - True if method has ExtensionAttribute
- **`ExtensionTarget: TypeReference?`** - Type of first 'this' parameter (for extension methods)

**Metadata:**
- **`Provenance: MemberProvenance`** - Original, InterfaceInlined, ExplicitImpl, BaseOverload, Synthesized
- **`EmitScope: EmitScope`** - ClassSurface, ViewOnly, Omitted
- **`InterfaceSource: TypeReference?`** - Source interface for interface-copied members
- **`Documentation: string?`** - XML doc comment

### Enum: MemberProvenance
- `Original, InterfaceInlined, ExplicitImpl, BaseOverload, Synthesized`

### Enum: EmitScope
- `ClassSurface` - On class/interface body
- `ViewOnly` - In As_IInterface view property
- `Omitted` - Not emitted (tracked in metadata)

---

## File: Symbols/MemberSymbols/PropertySymbol.cs

### Record: PropertySymbol

**Identity:**
- `StableId, ClrName, TsEmitName` (same as MethodSymbol)

**Signature:**
- **`PropertyType: TypeReference`** - Property type
- **`IndexParameters: ImmutableArray<ParameterSymbol>`** - Index parameters (for indexers)

**Accessors:**
- **`HasGetter, HasSetter: bool`** - Accessor presence

**Characteristics:**
- **`IsStatic, IsVirtual, IsAbstract, IsOverride: bool`**
- **`Visibility: Visibility`**

**Metadata:**
- `Provenance, EmitScope, InterfaceSource, Documentation` (same as MethodSymbol)

---

## File: Symbols/MemberSymbols/FieldSymbol.cs

### Record: FieldSymbol

**Identity:**
- `StableId, ClrName, TsEmitName`

**Signature:**
- **`FieldType: TypeReference`** - Field type

**Characteristics:**
- **`IsStatic, IsReadOnly, IsConst: bool`**
- **`ConstValue: object?`** - Constant value (if IsConst)
- **`Visibility: Visibility`**

**Metadata:**
- `Provenance, EmitScope, Documentation`

---

## File: Symbols/MemberSymbols/EventSymbol.cs

### Record: EventSymbol

**Identity:**
- `StableId, ClrName, TsEmitName`

**Signature:**
- **`EventHandlerType: TypeReference`** - Delegate type

**Characteristics:**
- **`IsStatic, IsVirtual, IsOverride: bool`**
- **`Visibility: Visibility`**

**Metadata:**
- `Provenance, EmitScope, InterfaceSource, Documentation`

---

## File: Symbols/MemberSymbols/ConstructorSymbol.cs

### Record: ConstructorSymbol

**Identity:**
- **`StableId: MemberStableId`** - Member name is always ".ctor"

**Signature:**
- **`Parameters: ImmutableArray<ParameterSymbol>`** - Constructor parameters

**Characteristics:**
- **`IsStatic: bool`** - True for static constructors
- **`Visibility: Visibility`**

**Metadata:**
- `Documentation`

---

## File: Symbols/MemberSymbols/ParameterSymbol.cs

### Record: ParameterSymbol

**Properties:**
- **`Name: string`** - Parameter name (sanitized for TypeScript reserved words)
- **`Type: TypeReference`** - Parameter type
- **`IsRef, IsOut, IsParams: bool`** - Parameter modifiers
- **`HasDefaultValue: bool`** - Has default value
- **`DefaultValue: object?`** - Default value (if HasDefaultValue)

---

## File: Types/TypeReference.cs

### Abstract Record: TypeReference

Base class for all type references. Represents type usage in signatures.

**Subclasses:**
- **`NamedTypeReference`** - Class, struct, interface, enum, delegate
- **`GenericParameterReference`** - Generic parameter (T, TKey, etc.)
- **`ArrayTypeReference`** - Array type (T[], T[,], etc.)
- **`PointerTypeReference`** - Pointer type (T*, T**, etc.)
- **`ByRefTypeReference`** - By-reference type (ref T, out T)
- **`PlaceholderTypeReference`** - Placeholder for cycle-breaking

---

## File: Types/NamedTypeReference.cs

### Record: NamedTypeReference (inherits TypeReference)

**Properties:**
- **`AssemblyName: string`** - Assembly declaring this type
- **`Namespace: string`** - Type namespace
- **`Name: string`** - Simple name (e.g., "List\`1")
- **`FullName: string`** - Full CLR name (e.g., "System.Collections.Generic.List\`1")
- **`Arity: int`** - Generic arity
- **`TypeArguments: ImmutableArray<TypeReference>`** - Type arguments (for constructed generics)
- **`IsValueType: bool`** - True if value type
- **`InterfaceStableId: string?`** - Stamped StableId for interfaces (optimization)

**Methods:**
- **`GetOpenForm(): NamedTypeReference`** - Returns open generic form (strips type arguments)
- **`GetClrKey(): string`** - Returns CLR key for lookups (AssemblyName:FullName)

---

## File: Types/GenericParameterReference.cs

### Record: GenericParameterReference (inherits TypeReference)

**Properties:**
- **`Id: GenericParameterId`** - Unique identifier
- **`Name: string`** - Parameter name (e.g., "T")
- **`Position: int`** - Zero-based position
- **`Constraints: ImmutableArray<TypeReference>`** - Type constraints (empty until ConstraintCloser)

---

## File: Types/ArrayTypeReference.cs

### Record: ArrayTypeReference (inherits TypeReference)

**Properties:**
- **`ElementType: TypeReference`** - Element type (recursive)
- **`Rank: int`** - Array rank (1 for T[], 2 for T[,], etc.)

---

## File: Types/PointerTypeReference.cs

### Record: PointerTypeReference (inherits TypeReference)

**Properties:**
- **`PointeeType: TypeReference`** - Pointee type (recursive)
- **`Depth: int`** - Pointer depth (1 for T*, 2 for T**, etc.)

---

## File: Types/ByRefTypeReference.cs

### Record: ByRefTypeReference (inherits TypeReference)

**Properties:**
- **`ReferencedType: TypeReference`** - Referenced type (recursive)

---

## File: Renaming/StableId.cs

### Record Struct: TypeStableId

**Format:** `"{AssemblyName}:{ClrFullName}"`

**Example:** `"System.Private.CoreLib:System.Collections.Generic.List\`1"`

**Properties:**
- `AssemblyName: string`
- `ClrFullName: string`

**Methods:**
- `ToString(): string` - Returns full identity string
- `static Parse(string): TypeStableId` - Parses from string

---

### Record Struct: MemberStableId

**Format:** `"{AssemblyName}:{DeclaringClrFullName}::{MemberName}{CanonicalSignature}"`

**Example:** `"System.Private.CoreLib:System.Decimal::ToString(IFormatProvider):String"`

**Properties:**
- `AssemblyName: string`
- `DeclaringClrFullName: string`
- `MemberName: string`
- `CanonicalSignature: string`
- `MetadataToken: int` (excluded from equality)

**Methods:**
- `ToString(): string` - Returns full identity string
- `static Parse(string): MemberStableId` - Parses from string

**Key:** MetadataToken excluded from equality comparisons (semantic identity only).

---

## Summary

The Model provides:
1. **Immutable data structures** for entire symbol graph (namespaces → types → members)
2. **Stable identities** (TypeStableId, MemberStableId) for tracking through transformations
3. **Type references** capturing full CLR type system (named, generic, array, pointer, byref)
4. **Provenance tracking** (Original, InterfaceInlined, ExplicitImpl, BaseOverload, Synthesized)
5. **Emit scope control** (ClassSurface, ViewOnly, Omitted)
6. **Pure functional transformations** via wither methods and `with` expressions
7. **Fast lookups** via NamespaceIndex and TypeIndex

All structures are **immutable records** with structural equality, enabling safe transformations and change tracking throughout the pipeline.
