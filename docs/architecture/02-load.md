# Load Phase

This document details the Load phase, which reads .NET assemblies via reflection and builds the initial SymbolGraph.

## Overview

**Purpose:** Load .NET assemblies and convert CLR metadata to our internal model

**Input:** Assembly file paths, runtime directory
**Output:** `SymbolGraph` with raw CLR type information

**Key Files:**
- `Load/AssemblyLoader.cs` - Assembly loading and resolution
- `Load/ReflectionReader.cs` - Type/member conversion
- `Load/TypeReferenceFactory.cs` - Type reference creation
- `Load/DeclaringAssemblyResolver.cs` - Cross-assembly resolution
- `Load/InterfaceMemberSubstitutor.cs` - Generic interface substitution

## MetadataLoadContext

The Load phase uses `System.Reflection.MetadataLoadContext` to load assemblies in isolation. This is critical because:

1. **Isolation** - Assemblies load in a separate context from the running application
2. **Version independence** - Can load any .NET version's assemblies
3. **No code execution** - Pure metadata inspection, no assembly code runs

### Critical Pattern: Name-Based Comparison

**MetadataLoadContext types CANNOT be compared with `typeof()`:**

```csharp
// ❌ WRONG - Fails for MetadataLoadContext types
if (type == typeof(bool)) return "boolean";

// ✅ CORRECT - Use name-based comparisons
if (type.FullName == "System.Boolean") return "boolean";
```

This is because MetadataLoadContext loads assemblies into isolated contexts, creating different `Type` instances than `typeof()` returns.

## Assembly Loading

### LoadClosure Algorithm

`AssemblyLoader.LoadClosure()` implements transitive closure loading:

```
LoadClosure(seedPaths, refPaths, strictVersions)
    │
    ├─► Phase 1: Build candidate map
    │   └─► Scan refPaths directories for all .dll files
    │   └─► Map AssemblyKey → file paths
    │
    ├─► Phase 2: BFS closure resolution
    │   └─► Start from seed assemblies
    │   └─► Walk AssemblyReference entries via PEReader
    │   └─► Resolve each reference from candidate map
    │   └─► Version policy: highest version wins
    │
    ├─► Phase 3: Validate assembly identity
    │   └─► PG_LOAD_002: Mixed PublicKeyToken check
    │   └─► PG_LOAD_003: Major version drift check
    │
    ├─► Phase 4: Find core library
    │   └─► Locate System.Private.CoreLib
    │
    └─► Phase 5: Create context and load
        └─► Build PathAssemblyResolver
        └─► Create MetadataLoadContext
        └─► Load all assemblies
```

### AssemblyKey

Assemblies are identified by a composite key:

```csharp
public sealed record AssemblyKey(
    string Name,           // e.g., "System.Collections"
    string PublicKeyToken, // e.g., "b03f5f7f11d50a3a"
    string Culture,        // e.g., "" (neutral)
    string Version);       // e.g., "10.0.0.0"
```

### Version Resolution

When multiple versions of an assembly are found:

1. **Highest version wins** - Always prefer newest version
2. **Major drift warning** - Warn if major versions differ
3. **Strict mode error** - Error on major drift with `--strict`

## Reflection Reading

### ReflectionReader

`ReflectionReader.ReadAssemblies()` converts loaded assemblies to SymbolGraph:

```
ReadAssemblies(loadContext, assemblyPaths)
    │
    ├─► Load assemblies via AssemblyLoader
    │
    ├─► For each assembly (deterministic order):
    │   ├─► Skip compiler-generated types
    │   │   └─► Names containing '<' or '>'
    │   │   └─► Examples: <Module>, <>c__DisplayClass
    │   │
    │   └─► For each public type:
    │       └─► ReadType() → TypeSymbol
    │
    ├─► Group types by namespace
    │
    └─► Build NamespaceSymbol per namespace
        └─► Track contributing assemblies
```

### Type Kind Determination

```csharp
DetermineTypeKind(Type type)
    │
    ├─► IsEnum → TypeKind.Enum
    ├─► IsInterface → TypeKind.Interface
    ├─► IsDelegate() → TypeKind.Delegate
    │   └─► NOTE: Name-based check (see IsDelegate)
    ├─► IsAbstract && IsSealed → TypeKind.StaticNamespace
    ├─► IsValueType → TypeKind.Struct
    └─► Else → TypeKind.Class
```

### Delegate Detection

Delegates require special handling due to MetadataLoadContext:

```csharp
IsDelegate(Type type)
    │
    ├─► Skip System.Delegate and System.MulticastDelegate
    │   └─► These are base classes, not concrete delegates
    │
    └─► Walk inheritance chain via name comparison
        └─► Check if baseType.FullName == "System.Delegate" or "System.MulticastDelegate"
```

### Accessibility Computation

Nested types require special handling:

```csharp
ComputeAccessibility(Type type)
    │
    ├─► Top-level: IsPublic → Public, else Internal
    │
    └─► Nested:
        └─► IsNestedPublic + Parent Public → Public
        └─► Otherwise → Internal (even if IsNestedPublic)
```

### Member Reading

Members are read with `BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly`:

```
ReadMembers(Type type)
    │
    ├─► Methods (skip IsSpecialName)
    │   └─► ReadMethod() → MethodSymbol
    │   └─► Detect explicit interface implementations (name contains '.')
    │   └─► Detect extension methods (ExtensionAttribute + static)
    │
    ├─► Properties
    │   └─► ReadProperty() → PropertySymbol
    │   └─► Index parameters for indexers
    │
    ├─► Fields
    │   └─► ReadField() → FieldSymbol
    │   └─► Capture const values
    │
    ├─► Events
    │   └─► ReadEvent() → EventSymbol
    │
    └─► Constructors
        └─► ReadConstructor() → ConstructorSymbol
```

### StableId Generation

Each symbol gets a stable identifier for tracking:

**TypeStableId:**
```csharp
{
    AssemblyName = "System.Private.CoreLib",
    ClrFullName = "System.Collections.Generic.List`1"
}
```

**MemberStableId:**
```csharp
{
    AssemblyName = "System.Private.CoreLib",
    DeclaringClrFullName = "System.Collections.Generic.List`1",
    MemberName = "Add",
    CanonicalSignature = "Add(T):Void",
    MetadataToken = 100663297
}
```

## Type Reference Factory

### TypeReferenceFactory

Converts `System.Type` to our `TypeReference` hierarchy with memoization and cycle detection:

```
Create(Type type)
    │
    ├─► Check cache → return cached
    │
    ├─► Check in-progress (cycle detection)
    │   └─► Return PlaceholderTypeReference
    │
    └─► CreateInternal():
        ├─► IsByRef → ByRefTypeReference
        ├─► IsPointer → PointerTypeReference (with depth)
        ├─► IsArray → ArrayTypeReference (with rank)
        ├─► IsGenericParameter → GenericParameterReference
        └─► Else → NamedTypeReference
```

### Named Type Reference

For named types (class, struct, interface, enum, delegate):

```csharp
CreateNamed(Type type)
    │
    ├─► Get assembly name
    │
    ├─► Get full name
    │   └─► CRITICAL: For constructed generics, use definition name
    │   └─► "System.IEquatable`1" NOT "System.IEquatable`1[[...]]"
    │
    ├─► Hardening: Guarantee Name never empty
    │   └─► Extract from FullName if needed
    │   └─► Fallback to "UnknownType"
    │
    ├─► Handle generics
    │   └─► Get arity from generic arguments
    │   └─► For constructed types: Create type argument refs
    │
    └─► For interfaces: Stamp InterfaceStableId
```

### Generic Parameter Symbol

Generic parameters store variance and constraints:

```csharp
CreateGenericParameterSymbol(Type type)
    │
    ├─► Extract position and declaring context
    │
    ├─► Get variance
    │   └─► Covariant (out T)
    │   └─► Contravariant (in T)
    │   └─► None
    │
    ├─► Get special constraints
    │   └─► ReferenceType (class constraint)
    │   └─► ValueType (struct constraint)
    │   └─► DefaultConstructor (new() constraint)
    │
    └─► Store RawConstraintTypes for ConstraintCloser
```

**Note:** Type constraints are NOT resolved during Load to avoid infinite recursion on recursive constraints like `T : IComparable<T>`. The `ConstraintCloser` pass resolves them during Shape phase.

## Assembly Resolution

### DeclaringAssemblyResolver

Resolves CLR type names to their declaring assemblies:

```
ResolveAssembly(clrFullName)
    │
    ├─► Check cache
    │
    └─► Search all loaded assemblies
        └─► assembly.GetType(clrFullName)
        └─► Return assembly name if found
```

Used for cross-assembly dependency resolution during import planning.

## Interface Member Substitution

### InterfaceMemberSubstitution

Builds substitution maps for closed generic interfaces:

```
SubstituteClosedInterfaces(ctx, graph)
    │
    ├─► Build interface index (ClrFullName → TypeSymbol)
    │
    └─► For each type implementing interfaces:
        └─► For each closed generic interface:
            └─► Build substitution map (T → int, etc.)
            └─► Maps stored for Shape phase use
```

**Example:** For `IComparable<int>.CompareTo(T)`, substitutes `T → int`:
- Original: `CompareTo(T): int`
- Substituted: `CompareTo(int): int`

### SubstituteTypeReference

Recursively substitutes type parameters:

```csharp
SubstituteTypeReference(original, substitutionMap)
    │
    ├─► GenericParameterReference → lookup in map
    ├─► ArrayTypeReference → substitute element type
    ├─► PointerTypeReference → substitute pointee type
    ├─► ByRefTypeReference → substitute referenced type
    ├─► NamedTypeReference → substitute type arguments
    └─► Else → return unchanged
```

## Output: SymbolGraph

The Load phase produces a `SymbolGraph` containing:

```csharp
SymbolGraph
{
    Namespaces: ImmutableArray<NamespaceSymbol>,
    SourceAssemblies: ImmutableHashSet<string>,
    TypeIndex: Dictionary<ClrFullName, TypeSymbol>,  // Built by WithIndices()
    InterfaceIndex: ...                              // Built by WithIndices()
}
```

Each namespace contains types, and each type contains members with full CLR metadata captured in immutable symbol records.

## Call Graph

```
Builder.Build()
    │
    └─► ReflectionReader.ReadAssemblies()
        │
        ├─► AssemblyLoader.LoadAssemblies()
        │   └─► CreateLoadContext()
        │   └─► For each: LoadFromAssemblyPath()
        │
        └─► For each assembly:
            └─► For each public type:
                └─► ReadType()
                    ├─► DetermineTypeKind()
                    ├─► ComputeAccessibility()
                    ├─► TypeReferenceFactory.Create() for base/interfaces
                    └─► ReadMembers()
                        ├─► ReadMethod()
                        ├─► ReadProperty()
                        ├─► ReadField()
                        ├─► ReadEvent()
                        └─► ReadConstructor()
```
