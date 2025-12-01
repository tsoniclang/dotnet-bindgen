# Phase 2: Load (Reflection)

## Overview

The **Load phase** performs reflection over .NET assemblies to extract pure CLR metadata. Operates entirely in the CLR domain—no TypeScript concepts yet. Uses `System.Reflection` and `MetadataLoadContext` to build complete `SymbolGraph`.

**Responsibilities:**
- Load assemblies with transitive closure via BFS
- Validate assembly identity (version consistency, PublicKeyToken)
- Extract public types and members via reflection
- Build type references (named, generic, array, pointer, byref)
- Substitute type parameters in closed generic interfaces

**Key constraint:** Pure CLR data—no `TsEmitName`, no TypeScript transforms. Those happen in later phases.

---

## File: AssemblyLoader.cs

### Purpose
Creates `MetadataLoadContext` for loading assemblies in isolation. Handles reference pack resolution for .NET BCL assemblies. Implements transitive closure loading via BFS. Validates assembly identity.

### Record: LoadClosureResult
```csharp
public sealed record LoadClosureResult(
    MetadataLoadContext LoadContext,
    IReadOnlyList<Assembly> Assemblies,
    IReadOnlyDictionary<AssemblyKey, string> ResolvedPaths);
```

**Fields:**
- `LoadContext` - MetadataLoadContext with all assemblies loaded
- `Assemblies` - Successfully loaded Assembly objects
- `ResolvedPaths` - Map from AssemblyKey to resolved file path

### Method: LoadClosure

**Signature:**
```csharp
public LoadClosureResult LoadClosure(
    IReadOnlyList<string> seedPaths,
    IReadOnlyList<string> refPaths,
    bool strictVersions = false)
```

**Purpose:** Loads transitive closure via BFS. Main entry point for full BCL generation.

**Parameters:**
- `seedPaths` - Initial assemblies (starting point)
- `refPaths` - Directories to search for referenced assemblies
- `strictVersions` - If true, error on major version drift; otherwise warn

**Returns:** `LoadClosureResult` - Context, assemblies, resolved paths

**Algorithm (5 phases):**

**Phase 1: Build candidate map**
- `BuildCandidateMap(refPaths)` scans reference directories
- Returns `AssemblyKey → List<string>` (multiple versions possible)

**Phase 2: BFS closure resolution**
- `ResolveClosure(seedPaths, candidateMap, strictVersions)` walks assembly references
- Uses BFS to traverse dependency graph
- Returns `AssemblyKey → string` (resolved file path, highest version wins)

**Phase 3: Validate assembly identity**
- `ValidateAssemblyIdentity(resolvedPaths, strictVersions)` validates consistency
- Guards: PG_LOAD_002 (mixed PublicKeyToken), PG_LOAD_003 (version drift), PG_LOAD_004 (retargetable)

**Phase 4: Find core library**
- `FindCoreLibrary(resolvedPaths)` locates System.Private.CoreLib
- Required for MetadataLoadContext creation

**Phase 5: Create MetadataLoadContext and load**
- Creates `PathAssemblyResolver` with all resolved paths
- Creates `MetadataLoadContext` with core library
- Loads all assemblies in dependency order
- Returns `LoadClosureResult`

### Method: ResolveClosure (private)

**Signature:**
```csharp
private Dictionary<AssemblyKey, string> ResolveClosure(
    IReadOnlyList<string> seedPaths,
    Dictionary<AssemblyKey, List<string>> candidateMap,
    bool strictVersions)
```

**Purpose:** BFS over assembly references. Highest version wins.

**BFS Algorithm:**
1. **Initialize:**
   - Queue with seed paths
   - Visited set (by AssemblyKey)
   - Resolved map (AssemblyKey → path)
2. **BFS loop:**
   - Dequeue current path
   - Get AssemblyKey via `AssemblyName.GetAssemblyName`
   - Skip if visited
   - Mark visited
   - **Version policy:** If already resolved, keep highest version (log upgrade)
   - Add to resolved map
   - **Load metadata:** Use FileStream + PEReader + MetadataReader (lightweight, no Assembly.Load)
   - **Walk references:**
     - Extract name, version, culture, PublicKeyToken from metadata
     - Create reference AssemblyKey
     - Look up in candidateMap
     - If not found: Skip (PG_LOAD_001, external reference - caught by PhaseGate)
     - If found: Pick highest version, enqueue for BFS
   - Catch exceptions (log warnings)
3. **Return:** Resolved map

**Key behaviors:**
- Version upgrades: If A v1.0 and v2.0 both referenced, v2.0 wins
- Missing references: Silently skipped (validated later by PhaseGate)
- Lightweight loading: PEReader/MetadataReader only (not full Assembly.Load)

**Time complexity:** O(N) where N = total assemblies in closure

### Method: ValidateAssemblyIdentity (private)

**Signature:**
```csharp
private void ValidateAssemblyIdentity(
    Dictionary<AssemblyKey, string> resolvedPaths,
    bool strictVersions)
```

**Purpose:** Validates assembly identity consistency. Implements PhaseGate guards.

**Guards:**

**PG_LOAD_002: Mixed PublicKeyToken**
- Group by name, extract distinct PublicKeyTokens
- If count > 1: Emit ERROR with `MixedPublicKeyTokenForSameName` diagnostic

**PG_LOAD_003: Version drift**
- For each name with multiple versions: Parse versions, find min/max major
- If max != min: Major version drift
- If `strictVersions`: Emit ERROR
- Else: Emit WARNING

**PG_LOAD_004: Retargetable/ContentType**
- Placeholder (requires extending AssemblyKey to track flags)

---

## File: ReflectionReader.cs

### Purpose
Reads assemblies via reflection and builds complete `SymbolGraph`. Pure CLR facts only. Extracts all public types and members with full metadata (accessibility, virtual/override, static, abstract, etc.).

### Method: ReadAssemblies

**Signature:**
```csharp
public SymbolGraph ReadAssemblies(
    MetadataLoadContext loadContext,
    IReadOnlyList<string> assemblyPaths)
```

**Purpose:** Main entry point. Builds complete `SymbolGraph` grouped by namespace.

**Algorithm:**
1. **Load assemblies:** `AssemblyLoader.LoadAssemblies(loadContext, assemblyPaths)`
2. **Initialize:**
   - `namespaceGroups: Dictionary<string, List<TypeSymbol>>` - Group types by namespace
   - `sourceAssemblies: HashSet<string>` - Track source assembly paths
3. **Process assemblies** (sorted by name for determinism):
   - For each assembly:
     - Add location to sourceAssemblies
     - For each type:
       - Skip compiler-generated (`IsCompilerGenerated`)
       - Compute accessibility (`ComputeAccessibility`)
       - Skip non-public
       - Call `ReadType` → TypeSymbol
       - Group by namespace
4. **Build namespace symbols:**
   - Sort namespaces alphabetically
   - For each namespace:
     - Create `TypeStableId`
     - Collect contributing assemblies
     - Create `NamespaceSymbol` with types
5. **Return SymbolGraph**

**Key behaviors:**
- Deterministic: Sorts assemblies and namespaces
- Public-only: Filters non-public types
- Compiler-generated filtered: Skips angle-bracket types (`<>c__DisplayClass`, etc.)

### Method: ReadType (private)

**Signature:**
```csharp
private TypeSymbol ReadType(Type type)
```

**Purpose:** Converts `System.Type` to `TypeSymbol`. Reads all type metadata.

**Algorithm:**
1. Create StableId: AssemblyName + ClrFullName (interned)
2. Determine type kind: `DetermineTypeKind` → `TypeKind` enum
3. Compute accessibility: `ComputeAccessibility` → `Accessibility` enum
4. Read generic parameters: `_typeFactory.CreateGenericParameterSymbol` for each
5. Read base type and interfaces: `_typeFactory.Create` for each
6. Read members: `ReadMembers` → `TypeMembers`
7. Read nested types: Recursively `ReadType` for each (filtered, no compiler-generated)
8. Build TypeSymbol: All CLR metadata (IsValueType, IsAbstract, IsSealed, IsStatic)

**IsStatic determination:**
```csharp
IsStatic = type.IsAbstract && type.IsSealed && !type.IsValueType
```

### Method: ComputeAccessibility (private static)

**Purpose:** Computes accessibility for types, correctly handling nested types.

**Algorithm:**
- **Top-level:** `type.IsPublic` → `Accessibility.Public`, else `Accessibility.Internal`
- **Nested:**
  - If `type.IsNestedPublic`:
    - Recursively `ComputeAccessibility(type.DeclaringType!)`
    - If declaring type is Public → return `Accessibility.Public`
    - Else → return `Accessibility.Internal`
  - Any other nested visibility → `Accessibility.Internal`

**Rationale:** Nested public type only truly public if all ancestors public.

**Time complexity:** O(N) where N = nesting depth

### Method: DetermineTypeKind (private)

**Returns:** `TypeKind` - Enum, Interface, Delegate, StaticNamespace, Struct, Class

**Checked in order:**
1. `type.IsEnum` → `TypeKind.Enum`
2. `type.IsInterface` → `TypeKind.Interface`
3. `type.IsSubclassOf(typeof(Delegate))` or `typeof(MulticastDelegate)` → `TypeKind.Delegate`
4. `type.IsAbstract && type.IsSealed && !type.IsValueType` → `TypeKind.StaticNamespace`
5. `type.IsValueType` → `TypeKind.Struct`
6. Otherwise → `TypeKind.Class`

### Method: ReadMembers (private)

**Purpose:** Reads all public members: methods, properties, fields, events, constructors. Uses `BindingFlags.DeclaredOnly` to avoid inherited members.

**Algorithm:**
1. **Initialize collections**
2. **Define binding flags:**
   - `publicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly`
   - `publicStatic = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly`
3. **Read methods:**
   - Get via `type.GetMethods(publicInstance | publicStatic)`
   - Skip special names (property/event accessors)
   - Track by `methodKey = "{method.Name}|{method.MetadataToken}"` (duplicate detection)
   - Call `ReadMethod` for each
   - Check duplicate StableIds (log ERROR, skip)
4. **Read properties, fields, events, constructors** (similar pattern)
5. **Return TypeMembers:** Convert all lists to ImmutableArrays

**Key behaviors:**
- DeclaredOnly: Only members declared on this type (not inherited)
- Duplicate detection: Prevents reflection bugs
- Special names skipped: Property/event accessors excluded

### Method: ReadMethod (private)

**Purpose:** Converts `MethodInfo` to `MethodSymbol`. Handles explicit interface implementations.

**Algorithm:**
1. **Detect explicit interface implementation:**
   - `clrName = method.Name`
   - If `clrName.Contains('.')`: Explicit (e.g., "System.IDisposable.Dispose") - use qualified name
2. **Create MemberStableId:** AssemblyName + DeclaringClrFullName + MemberName + CanonicalSignature + MetadataToken
3. **Detect extension methods:** Check for `ExtensionAttribute` on method → Set `IsExtensionMethod = true`, `ExtensionTarget = parameters[0].Type`
4. **Read parameters:** `ReadParameter` for each
5. **Read generic parameters:** If generic method, `CreateGenericParameterSymbol` for each
6. **Build MethodSymbol:**
   - ReturnType via `_typeFactory.Create(method.ReturnType)`
   - IsStatic, IsAbstract, IsVirtual, IsSealed from method
   - IsOverride via `IsMethodOverride(method)`
   - Visibility via `GetVisibility(method)`
   - IsExtensionMethod, ExtensionTarget (from step 3)
   - Provenance = `MemberProvenance.Original`
   - EmitScope = `EmitScope.ClassSurface` (all reflected members start on class)

### Method: ReadProperty, ReadField, ReadEvent, ReadConstructor (private)

Similar patterns to `ReadMethod` with member-specific logic:
- **ReadProperty:** Handles indexers (IndexParameters), accessors (HasGetter/HasSetter)
- **ReadField:** IsReadOnly (`field.IsInitOnly`), IsConst (`field.IsLiteral`), ConstValue
- **ReadEvent:** EventHandlerType, add method visibility
- **ReadConstructor:** Parameters, IsStatic (for static constructors)

### Method: IsMethodOverride (private static)

**Purpose:** Checks if method is override (vs new virtual or original virtual). Works with MetadataLoadContext.

```csharp
return method.IsVirtual && !method.Attributes.HasFlag(MethodAttributes.NewSlot);
```

**Explanation:**
- `IsVirtual` - Participates in virtual dispatch
- `NewSlot` - Introduces new vtable slot (new virtual or hiding member)
- Override = virtual method that reuses parent's vtable slot

### Method: IsCompilerGenerated (private static)

**Purpose:** Checks if type name indicates compiler-generated code.

```csharp
return typeName.Contains('<') || typeName.Contains('>');
```

**Examples:** `<Module>`, `<PrivateImplementationDetails>`, `<>c__DisplayClass`, `<>d__Iterator`, `<>f__AnonymousType`

---

## File: InterfaceMemberSubstitution.cs

### Purpose
Substitutes generic type parameters in interface members for closed generic interfaces. For `IComparable<T>.CompareTo(T)` implemented as `IComparable<int>`, substitutes `T → int`.

**Note:** This file only builds substitution maps—actual member substitution performed by Shape phase components.

### Method: SubstituteClosedInterfaces

**Signature:**
```csharp
public static void SubstituteClosedInterfaces(BuildContext ctx, SymbolGraph graph)
```

**Purpose:** Processes all types, building substitution maps for closed generic interfaces.

**Algorithm:**
1. Log start: "Building closed interface member maps..."
2. Build interface index: `BuildInterfaceIndex(graph)` → `Dictionary<string, TypeSymbol>` (ClrFullName → TypeSymbol)
3. Process all types:
   - For each namespace (log progress every 10):
     - For each type: `ProcessType(ctx, type, interfaceIndex)` → substitution count
4. Log completion: "Created {totalSubstitutions} interface member mappings"

**Note:** Substitution maps built but not stored. Shape phase components rebuild as needed using `BuildSubstitutionMap` and `SubstituteTypeReference`.

### Method: ProcessType (private static)

**Purpose:** Processes one type, building substitution maps for all closed generic interfaces it implements.

**Algorithm:**
1. If no interfaces, return 0
2. For each interface reference in `type.Interfaces`:
   - Check if closed generic: Cast to `NamedTypeReference`, check `TypeArguments.Count > 0`
   - If closed generic:
     - Extract generic definition name via `GetGenericDefinitionName`
     - Look up interface definition in index
     - If found: `BuildSubstitutionMap(ifaceSymbol, namedRef)`, increment count
3. Return total count

**Example:**
- `List<int>` implements `ICollection<int>`
- Generic definition: `System.Collections.Generic.ICollection`1`
- Substitution map: `T → int`

### Method: BuildSubstitutionMap (private static)

**Purpose:** Builds substitution map from generic parameter names to type arguments.

**Algorithm:**
1. Create empty map
2. Validate arity: `interfaceSymbol.GenericParameters.Length == closedInterfaceRef.TypeArguments.Count`
   - If mismatch: Return empty map
3. For each generic parameter index:
   - Get parameter from interfaceSymbol (e.g., "T")
   - Get argument from closedInterfaceRef (e.g., TypeReference for "int")
   - Add to map: `map[param.Name] = arg`
4. Return map

**Example:**
- Interface: `IComparable<T>`
- Closed: `IComparable<int>`
- Map: `{ "T" → TypeReference(int) }`

### Method: SubstituteTypeReference (public static)

**Purpose:** Substitutes type parameters in type reference. Used by Shape phase components.

**Algorithm (recursive pattern matching):**
1. **GenericParameterReference:** If in map → return mapped type; else → return original
2. **ArrayTypeReference:** Recursively substitute element type, return new ArrayTypeReference
3. **PointerTypeReference:** Recursively substitute pointee type, return new PointerTypeReference
4. **ByRefTypeReference:** Recursively substitute referenced type, return new ByRefTypeReference
5. **NamedTypeReference with type arguments:** Recursively substitute each argument, return new NamedTypeReference
6. **Other:** Return original (no substitution needed)

**Time complexity:** O(D) where D = type depth

**Example:**
- Original: `Array<T>` (ArrayTypeReference with GenericParameterReference("T"))
- Substitution map: `{ "T" → TypeReference(int) }`
- Result: `Array<int>` (ArrayTypeReference with TypeReference(int))

---

## File: TypeReferenceFactory.cs

### Purpose
Converts `System.Type` to `TypeReference` model. Handles all type constructs: named, generic, array, pointer, byref, nested. Uses memoization with cycle detection to prevent stack overflow on recursive constraints (e.g., `IComparable<T> where T : IComparable<T>`).

### Method: Create

**Signature:**
```csharp
public TypeReference Create(Type type)
```

**Purpose:** Converts `System.Type` to `TypeReference`. Memoized with cycle detection.

**Algorithm:**
1. **Check cache:** If already converted → return cached result
2. **Detect cycle:** If in `_inProgress` set → return `PlaceholderTypeReference` (breaks recursion)
3. **Mark as in-progress:** Add to `_inProgress` set (try-finally for cleanup)
4. **Convert type:** `CreateInternal(type)` (may recursively call Create)
5. **Cache result:** Add to `_cache`
6. **Cleanup:** Remove from `_inProgress` (in finally block)
7. **Return result**

**Time complexity:** O(D) first call (D = type depth), O(1) cached calls
**Space complexity:** O(D) recursion stack, O(T) cache (T = total types)

**Cycle detection example:**
- `IComparable<T> where T : IComparable<T>`
- Call Create(T) → Mark T in-progress → Resolve constraint IComparable<T> → Create(T) → T is in-progress → return PlaceholderTypeReference → Cycle broken!

### Method: CreateInternal (private)

**Purpose:** Dispatches to appropriate handler based on type kind.

**Checked in order:**
1. **ByRef:** `type.IsByRef` → `ByRefTypeReference` (recursively Create element type)
2. **Pointer:** `type.IsPointer` → `PointerTypeReference` (count depth, recursively Create final element)
3. **Array:** `type.IsArray` → `ArrayTypeReference` (rank via `GetArrayRank`, recursively Create element)
4. **Generic parameter:** `type.IsGenericParameter` → `CreateGenericParameter(type)`
5. **Named types:** All others → `CreateNamed(type)`

### Method: CreateNamed (private)

**Purpose:** Creates `NamedTypeReference` for class, struct, interface, enum, delegate.

**Algorithm:**

**Step 1: Extract basic metadata**
- `assemblyName = type.Assembly.GetName.Name ?? "Unknown"`
- **CRITICAL - Open generic form for constructed generics:**
  ```csharp
  var fullName = type.IsGenericType && type.IsConstructedGenericType
      ? type.GetGenericTypeDefinition.FullName ?? type.Name
      : type.FullName ?? type.Name;
  ```
  - For constructed generics (e.g., `IEquatable<StandardFormat>`), use open generic form
  - Open form: `"System.IEquatable\`1"` (clean, backtick arity only)
  - Constructed form: `"System.IEquatable\`1[[System.Buffers.StandardFormat, ...]]"` (has assembly-qualified type args)
  - **Why needed:** Constructed form breaks StableId lookup, causes import bugs
  - **Related:** ImportGraph.GetOpenGenericClrKey uses same logic
- `namespaceName = type.Namespace ?? ""`
- `name = type.Name`

**Step 2: HARDENING - Guarantee Name never empty**
- If `name` null/empty/whitespace:
  - If `fullName` valid: Extract last segment after '.' or '+' (nested types)
  - Else: Use synthetic `"UnknownType"`, log warning

**Step 3: Handle generic types**
- Initialize `arity = 0`, `typeArgs = []`
- If `type.IsGenericType`:
  - Get arity via `type.GetGenericArguments.Length`
  - If `type.IsConstructedGenericType`: Recursively Create each type argument

**Step 4: HARDENING - Stamp interface StableId at load time**
- If `type.IsInterface`:
  - Format: `"{assemblyName}:{fullName}"` (same as ScopeFactory.GetInterfaceStableId)
  - Intern and store in `interfaceStableId` field
  - Purpose: Eliminates repeated computation, graph lookups in later phases

**Step 5: Build NamedTypeReference**
- All strings interned via `_ctx.Intern`
- Return with all fields populated

### Method: CreateGenericParameter (private)

**Purpose:** Creates `GenericParameterReference` for generic type parameter (e.g., `T` in `List<T>`).

**Algorithm:**
1. Extract declaring context: `declaringType = type.DeclaringType ?? type.DeclaringMethod?.DeclaringType`
2. Create `GenericParameterId`: DeclaringTypeName, Position, IsMethodParameter
3. **IMPORTANT - Constraints NOT resolved here:** To avoid infinite recursion on recursive constraints
   - `Constraints` field left empty
   - ConstraintCloser (Shape phase) resolves later
4. Build GenericParameterReference: Id, Name (interned), Position, Constraints (empty)

### Method: CreateGenericParameterSymbol (public)

**Purpose:** Creates `GenericParameterSymbol` from `System.Type`. Stores variance and special constraints; ConstraintCloser resolves type constraints later.

**Algorithm:**
1. Validate: If not generic parameter → throw ArgumentException
2. Extract declaring context (same as CreateGenericParameter)
3. Create `GenericParameterId`
4. **Extract variance:**
   - `GenericParameterAttributes` via `type.GenericParameterAttributes`
   - Covariant flag → `Variance.Covariant` (e.g., `out T` in `IEnumerable<out T>`)
   - Contravariant flag → `Variance.Contravariant` (e.g., `in T` in `Action<in T>`)
   - Otherwise → `Variance.None`
5. **Extract special constraints:**
   - ReferenceTypeConstraint → `GenericParameterConstraints.ReferenceType` (class)
   - NotNullableValueTypeConstraint → `GenericParameterConstraints.ValueType` (struct)
   - DefaultConstructorConstraint → `GenericParameterConstraints.DefaultConstructor` (new)
   - Combine with bitwise OR (flags enum)
6. **Store raw constraint types:**
   - `type.GetGenericParameterConstraints` → raw `System.Type[]`
   - Store in `RawConstraintTypes` field
   - ConstraintCloser converts to TypeReferences during Shape phase
   - Prevents infinite recursion
7. Build GenericParameterSymbol: Id, Name, Position, Constraints (empty), RawConstraintTypes, Variance, SpecialConstraints

---

## File: DeclaringAssemblyResolver.cs

### Purpose
Resolves CLR type full names to their declaring assembly names using reflection context. Used for cross-assembly dependency resolution to identify types outside current generation set. Enables future generation of ambient stubs for external dependencies.

**Context:** When ImportGraph encounters type references not in current SymbolGraph, those CLR keys are collected as "unresolved". DeclaringAssemblyResolver searches all loaded assemblies to determine which assembly declares each unresolved type.

**Use case:** If generating System.Linq and encountering reference to System.IO type (not in generation set), resolver identifies "System.IO" as declaring assembly. Future phases can generate ambient stub declarations for cross-assembly imports.

### Method: ResolveAssembly

**Signature:**
```csharp
public string? ResolveAssembly(string clrFullName)
```

**Purpose:** Resolves single CLR type full name (backtick form) to declaring assembly name. Returns null if not found.

**Parameters:** `clrFullName` - CLR full name with backtick arity, e.g., `"System.Collections.Generic.IEnumerable`1"`

**Returns:** Assembly name (e.g., `"System.Private.CoreLib"`) if found; `null` if not found

**Algorithm:**
1. Check cache - return cached result if available
2. Iterate all assemblies in MetadataLoadContext via `GetAssemblies`
3. For each assembly: `assembly.GetType(clrFullName, throwOnError: false)`
4. If type found: Cache and return assembly name
5. If not found in any assembly: Cache null, return null
6. On exception: Log error, cache null, return null

**Why:** MetadataLoadContext lacks global FindType, must search assemblies linearly. Caching prevents repeated expensive searches.

**Examples:**
- `"System.IO.Stream"` → `"System.Private.CoreLib"`
- `"System.Linq.Enumerable"` → `"System.Linq"`
- `"FooBar.NonExistent"` → `null`

### Method: ResolveBatch

**Signature:**
```csharp
public Dictionary<string, string> ResolveBatch(IEnumerable<string> clrKeys)
```

**Purpose:** Batch resolves multiple CLR keys. Only returns successful resolutions (not null results).

**Algorithm:**
1. Create empty results dictionary
2. For each CLR key: Call `ResolveAssembly`
3. If result non-null: Add to results
4. Log batch resolution stats (X resolved out of Y total)
5. Return results

**Example:**
```
Input: ["System.IO.Stream", "System.Linq.Enumerable", "Unknown.Type"]
Output: {
  "System.IO.Stream" → "System.Private.CoreLib",
  "System.Linq.Enumerable" → "System.Linq"
}
```

### Method: GroupByAssembly

**Purpose:** Groups resolved types by declaring assembly name. Useful for diagnostic output and planning stub generation.

**Example:**
```
Input: {
  "System.IO.Stream" → "System.Private.CoreLib",
  "System.IO.File" → "System.Private.CoreLib",
  "System.Linq.Enumerable" → "System.Linq"
}

Output: {
  "System.Private.CoreLib" → ["System.IO.Stream", "System.IO.File"],
  "System.Linq" → ["System.Linq.Enumerable"]
}
```

### Integration Point

Used in Builder.PlanPhase:
```csharp
// After ImportGraph.Build
if (importGraph.UnresolvedClrKeys.Count > 0)
{
    var resolver = new DeclaringAssemblyResolver(loadContext, ctx);
    var unresolvedToAssembly = resolver.ResolveBatch(importGraph.UnresolvedClrKeys);
    importGraph.UnresolvedToAssembly = unresolvedToAssembly;

    // Diagnostic logging
    var byAssembly = resolver.GroupByAssembly(unresolvedToAssembly);
    foreach (var (assembly, types) in byAssembly)
        ctx.Log("CrossAssembly", $"  - {assembly}: {types.Count} types");
}
```

---

## Key Algorithms

### BFS Transitive Closure (ResolveClosure)

**Purpose:** Resolve all assemblies transitively referenced from seeds.

**Algorithm:**
1. Initialize: BFS queue (seed paths), visited set (AssemblyKey), resolved map (AssemblyKey → path)
2. While queue not empty:
   - Dequeue current path
   - Get AssemblyKey from path
   - Skip if visited → Mark visited
   - If already resolved: Compare versions (keep highest), continue
   - Add to resolved map
   - Read assembly metadata (PEReader/MetadataReader)
   - For each assembly reference:
     - Create reference AssemblyKey
     - Look up in candidate map
     - If found: Pick highest version, enqueue
     - If not found: Skip (external reference)
3. Return resolved map

**Time complexity:** O(N) where N = total assemblies in closure
**Space complexity:** O(N) for visited set and resolved map

### Type Reference Memoization with Cycle Detection (Create)

**Purpose:** Convert System.Type to TypeReference without infinite recursion.

**Algorithm:**
1. Check cache → return if found
2. Check cycle → if in `_inProgress` → return PlaceholderTypeReference
3. Mark in-progress → Add to `_inProgress`
4. Convert type → Call `CreateInternal` (may recursively call Create)
5. Cache result
6. Cleanup → Remove from `_inProgress` (in finally)
7. Return result

**Time complexity:** O(D) first call (D = type depth), O(1) cached
**Space complexity:** O(D) recursion stack, O(T) cache (T = total types)

**Cycle detection example:**
- `IComparable<T> where T : IComparable<T>`
- Create(T) → Mark T in-progress → Resolve constraint IComparable<T> → Create generic arg T → T is in-progress → return PlaceholderTypeReference → Cycle broken!

### Accessibility Computation for Nested Types (ComputeAccessibility)

**Purpose:** Determine effective public accessibility for nested types.

**Algorithm:**
1. If top-level: `IsPublic` → `Accessibility.Public`; else → `Accessibility.Internal`
2. If nested:
   - If `IsNestedPublic`:
     - Recursively `ComputeAccessibility(DeclaringType)`
     - If declaring type Public → return `Accessibility.Public`
     - Else → return `Accessibility.Internal`
   - Any other nested visibility → return `Accessibility.Internal`

**Time complexity:** O(N) where N = nesting depth

**Example:**
```csharp
public class Outer { public class Inner1 { public class Inner2 { } } }  // All Public
internal class Hidden { public class Inner { } }  // Inner effectively Internal
```

### Generic Parameter Substitution (SubstituteTypeReference)

**Purpose:** Substitute generic parameters in type references for closed generic interfaces.

**Algorithm (recursive pattern matching):**
1. **GenericParameterReference:** If in map → return mapped; else → return original
2. **ArrayTypeReference:** Recursively substitute element, return new ArrayTypeReference
3. **PointerTypeReference:** Recursively substitute pointee, return new PointerTypeReference
4. **ByRefTypeReference:** Recursively substitute referenced, return new ByRefTypeReference
5. **NamedTypeReference with type arguments:** Recursively substitute each argument, return new NamedTypeReference
6. **Other:** Return original

**Time complexity:** O(D) where D = type depth

**Example:**
- Original: `IComparable<T>.CompareTo(T other)`
- Substitution map: `{ "T" → int }`
- Result: `IComparable<int>.CompareTo(int other)`

---

## Summary

The **Load phase** is responsible for:

1. **Loading assemblies** with transitive closure resolution (AssemblyLoader)
2. **Validating assembly identity** consistency (PublicKeyToken, version drift)
3. **Extracting types and members** via reflection (ReflectionReader)
4. **Building type references** with memoization and cycle detection (TypeReferenceFactory)
5. **Building substitution maps** for closed generic interfaces (InterfaceMemberSubstitution)
6. **Resolving declaring assemblies** for cross-assembly dependencies (DeclaringAssemblyResolver)

**Output:** `SymbolGraph` with pure CLR metadata—no TypeScript concepts yet. Flows into Shape phase for further processing.

**Key design decisions:**
- **MetadataLoadContext isolation:** Load assemblies in isolation, enabling reflection on BCL without version conflicts
- **Name-based type comparisons:** Required for MetadataLoadContext compatibility (typeof doesn't work)
- **Cycle detection:** Prevents stack overflow on recursive generic constraints
- **DeclaredOnly members:** Avoids reading inherited members (inheritance flattening happens in Shape)
- **Compiler-generated filtered:** Skips angle-bracket types not valid in declarations
- **Deduplication:** Assembly identity, member MetadataToken, type keys all deduplicated
- **Determinism:** Sorted iteration for reproducible output
- **Cross-assembly resolution:** DeclaringAssemblyResolver maps unresolved type references to declaring assemblies
