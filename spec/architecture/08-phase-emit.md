# Phase 4: Emit - Output File Generation

## Overview

The **Emit Phase** is the fourth and final phase of the architecture. It takes the validated `EmissionPlan` (from the Plan phase) and generates all output files:

1. **TypeScript Declarations** - Public-facing `.d.ts` files with TypeScript type definitions
2. **Internal Declarations** - Internal implementation `.d.ts` files with actual type definitions
3. **Metadata Sidecars** - `metadata.json` files with CLR-specific information for Tsonic compiler
4. **Binding Metadata** - `bindings.json` files mapping TypeScript names to CLR names
5. **Module Stubs** - `index.js` stub files that throw at runtime (prevent execution)
6. **Support Types** - Centralized `_support/types.d.ts` with marker types for unsafe constructs

**Key Principles:**
- **Uses Renamer for all names** - Single source of truth for TypeScript identifiers
- **Respects EmitScope** - Only emits members with ClassSurface/StaticSurface/ViewOnly scopes
- **Type safety via TypeNameResolver** - Never uses CLR names directly
- **Deterministic output** - Uses EmitOrder from Plan phase for stable ordering

---

## File Organization

### Output Directory Structure

```
output/
  System/
    index.d.ts              # Public facade (re-exports from internal/)
    index.js                # Runtime stub (throws)
    bindings.json           # CLR name mappings
    internal/
      index.d.ts            # Actual type declarations
      metadata.json         # CLR-specific metadata
  System.Collections.Generic/
    index.d.ts
    index.js
    bindings.json
    internal/
      index.d.ts
      metadata.json
  _support/
    types.d.ts              # Shared marker types (TSUnsafePointer, TSByRef)
```

**Special Cases:**
- **Root namespace** (empty name): Uses `_root/` instead of `internal/` to avoid collision with "Internal" namespace
- **Dotted namespaces**: Cannot use `export import` syntax (TypeScript limitation)

---

## File: FacadeEmitter.cs

### Purpose
Generates public-facing `index.d.ts` files for each namespace. These are the entry points that users import from.

### Method: Emit
```csharp
public static void Emit(BuildContext ctx, EmissionPlan plan, string outputDirectory)
```
**What it does:**
- Iterates through `plan.EmissionOrder.Namespaces` in deterministic order
- For each namespace, generates facade content via `GenerateFacade`
- Writes to `output/{namespace}/index.d.ts`
- Logs each emitted file

**Output structure:**
- File header with generation info
- Import from `internal/index.d.ts` (or `_root/index.d.ts` for root namespace)
- Imports from dependencies (other namespaces)
- Re-export namespace (for non-dotted namespaces)
- Individual type exports for convenience

### Method: GenerateFacade
```csharp
private static string GenerateFacade(BuildContext ctx, EmissionPlan plan, Model.Symbols.NamespaceSymbol ns)
```
**What it does:**
1. **File header** - Comments with namespace name and purpose
2. **Internal import** - `import * as Internal from './internal/index.js';`
3. **Dependency imports** - Cross-namespace references with aliases
4. **Wildcard re-export** - `export * from './internal/index.js';` (exports all types)
5. **Dual type exports** - For each generic type, exports both canonical and friendly aliases:
   - **Canonical**: `export type List_1<T> = Internal.List_1<T>;` (includes arity suffix)
   - **Friendly**: `export type List<T> = Internal.List_1<T>;` (arity-less, user-friendly)

**Algorithm:**
- Uses `plan.Imports.NamespaceImports` to get required imports
- Uses `plan.Imports.NamespaceExports` to get types to re-export
- For each generic type (e.g., `List_1`, `Dictionary_2`):
  - Exports canonical name with arity suffix
  - Generates friendly name by stripping `_N` suffix
  - Exports friendly alias pointing to same internal type
- For non-generic types, exports single canonical name

**Example output:**
```typescript
// System.Collections.Generic/index.d.ts
export * from './internal/index.js';

// Dual exports for generics
export type List_1<T> = Internal.List_1<T>;              // Canonical
export type List<T> = Internal.List_1<T>;                // Friendly
export type Dictionary_2<TKey, TValue> = Internal.Dictionary_2<TKey, TValue>;  // Canonical
export type Dictionary<TKey, TValue> = Internal.Dictionary_2<TKey, TValue>;    // Friendly

// Single export for non-generics
export type CollectionExtensions = Internal.CollectionExtensions;
```

**Why both?**
- **Canonical names** match CLR naming, preserve full metadata
- **Friendly names** provide ergonomic TypeScript experience
- Users can choose either style: `List<string>` or `List_1<string>`

### Method: GetImportAlias
```csharp
private static string GetImportAlias(string namespaceName)
```
**What it does:**
- Converts dotted namespace to valid identifier: `"System.Collections.Generic"` → `"System_Collections_Generic"`

### Method: AppendDelegateAliases
```csharp
private static void AppendDelegateAliases(StringBuilder sb, BuildContext ctx, SymbolGraph graph)
```
**Purpose:** Generates lambda-compatible convenience aliases for `Action` and `Func` delegates in the System namespace facade.

**What it does:**
1. **Finds all Action/Func delegates** - Discovers all Action_N and Func_N types from System namespace
2. **Emits unspecified marker** - `declare const __unspecified: unique symbol; export type __ = typeof __unspecified;`
3. **Generates conditional type aliases** - Creates `Action<T1, T2, ...>` and `Func<T1, T2, ...>` with default parameters
4. **Unions call signatures with internal types** - Each arity branch: `((args) => result) | Internal.Func_N<...>`

**Why this is needed:**
- Internal delegate types are emitted as **classes** (to preserve all CLR members like BeginInvoke, EndInvoke, Clone)
- TypeScript lambdas (`x => x + 1`) are **not assignable** to class types
- Facade aliases union **call signatures** with class types, making lambdas assignable

**Generated output example:**
```typescript
// Unspecified marker for default parameter detection
declare const __unspecified: unique symbol;
export type __ = typeof __unspecified;

// Action convenience alias (0-16 parameters)
export type Action<
  T1 = __,
  T2 = __,
  // ... up to T16
> =
  [T1] extends [__] ? ((() => void) | Internal.Action) :
  [T2] extends [__] ? (((arg1: T1) => void) | Internal.Action_1<T1>) :
  [T3] extends [__] ? (((arg1: T1, arg2: T2) => void) | Internal.Action_2<T1, T2>) :
  // ... conditional routing to correct arity
  ((arg1: T1, ..., arg16: T16) => void) | Internal.Action_16<T1, ..., T16>;

// Func convenience alias (1-17 type parameters, last is return type)
export type Func<
  T1 = __,
  T2 = __,
  // ... up to T17
> =
  [T2] extends [__] ? ((() => T1) | Internal.Func_1<T1>) :
  [T3] extends [__] ? (((arg1: T1) => T2) | Internal.Func_2<T1, T2>) :
  // ... conditional routing
  ((arg1: T1, ..., arg16: T16) => T17) | Internal.Func_17<...>;
```

**How the conditional types work:**
1. **Default parameters** - All type parameters default to `__` (the unspecified marker)
2. **Conditional routing** - Check `[T_N] extends [__]` to detect which parameters are provided
3. **Call signature** - Each branch includes `((args) => result)` making lambdas assignable
4. **Union with internal** - Also includes `Internal.Func_N` for CLR delegate instances

**Usage examples:**
```typescript
// ✅ Lambda assignment works!
import { Func, Action } from "./System";
import { int } from "./System/internal";

const func: Func<int, int> = x => (x + 1) as int;
const result: int = func(42 as int);

const action: Action<int> = x => console.log(x);
action(10 as int);
```

**Comparison with internal types:**
```typescript
// ❌ Internal delegate types DON'T accept lambdas
import { Func_2 } from "./System/internal";

const func: Func_2<int, int> = x => x + 1;  // Type error!
// Error: Type '(x: int) => int' is not assignable to type 'Func_2<int, int>'
```

**Arity support:**
- **Action**: 0 to 16 parameters (Action through Action_16)
- **Func**: 1 to 17 type parameters (last is return type, so 0-16 input parameters)

**Only emitted for System namespace** - Other namespaces don't get delegate aliases (they should import from System facade)

### Output Format Example

```typescript
// Generated by tsbindgen - tsbindgen
// Namespace: System.Collections.Generic
// Facade - Public API Surface

// Import internal declarations
import * as Internal from './internal/index.js';

// Import dependencies
import * as System from '../System/index.js';

// Re-export namespace
export import Generic = Internal.System.Collections.Generic;

// Individual type exports for convenience
export type List_1 = Internal.System.Collections.Generic.List_1;
export type Dictionary_2 = Internal.System.Collections.Generic.Dictionary_2;
```

---

## File: InternalIndexEmitter.cs

### Purpose
Generates `internal/index.d.ts` files with actual TypeScript declarations. These contain the real type definitions that the facade re-exports.

### Method: ShouldEmit
```csharp
public static bool ShouldEmit(TypeSymbol type)
```
**What it does:**
- Returns `true` only for public types (`type.Accessibility == Accessibility.Public`)
- Internal types are never emitted to `.d.ts` files

### Method: Emit
```csharp
public static void Emit(BuildContext ctx, EmissionPlan plan, string outputDirectory)
```
**What it does:**
- Iterates through `plan.EmissionOrder.Namespaces`
- For each namespace, generates declarations via `GenerateNamespaceDeclaration`
- Writes to `output/{namespace}/internal/index.d.ts` (or `_root/index.d.ts` for root)
- Creates subdirectory if needed

### Method: GenerateNamespaceDeclaration
```csharp
private static string GenerateNamespaceDeclaration(BuildContext ctx, SymbolGraph graph, ImportPlan importPlan, NamespaceEmitOrder nsOrder)
```
**What it does:**
1. **Creates TypeNameResolver** - Single source of truth for type names
2. **File header** - Comments with namespace and contributing assemblies
3. **Imports branded primitives** - Imports `int`, `byte`, `decimal`, etc. from `@tsonic/types` package
4. **Conditional support types import** - If namespace uses pointers/byrefs, imports from `_support/types.d.ts`
5. **Cross-namespace imports** - Type imports from other namespaces with aliases if needed
6. **Type declarations** - Iterates through `nsOrder.OrderedTypes` and emits each as pure ES module exports

**Algorithm:**
- Calls `NamespaceUsesSupportTypes` to check if `TSUnsafePointer`/`TSByRef` needed
- Uses `importPlan.GetImportsFor` to get cross-namespace imports
- All types are exported directly at module level (pure ESM, no TypeScript namespace wrappers)
- For each type, checks if it has explicit views (companion views pattern):
  - **With views**: Emits `TypeName$instance` class + `__TypeName$views` interface + intersection type alias
  - **Without views**: Emits normal class/interface/enum/delegate

### Branded Primitive Types Import
**What it does:**
- Imports branded primitive type aliases from the external `@tsonic/types` package
- All CLR numeric types are imported: `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `int128`, `uint128`, `half`, `float`, `double`, `decimal`, `nint`, `nuint`, `char`

**Generated code:**
```typescript
// Branded primitive types are sourced from @tsonic/types
import type { sbyte, byte, short, ushort, int, uint, long, ulong, int128, uint128, half, float, double, decimal, nint, nuint, char } from '@tsonic/types';
```

**Rationale:**
- Centralizes branded primitive definitions in a shared package
- Ensures consistency across all generated .NET bindings
- Allows users to use the same branded types in their own code

### Method: EmitCompanionViewsInterface
```csharp
private static string EmitCompanionViewsInterface(TypeSymbol type, ImmutableArray<Shape.ViewPlanner.ExplicitView> views, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Generates companion interface with view properties: `interface __List_1$views<T> { ... }`
- Each view becomes a readonly property: `readonly IEnumerable_1$view: IEnumerable_1<T>;`

### Method: EmitIntersectionTypeAlias
```csharp
private static string EmitIntersectionTypeAlias(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Creates type alias that intersects instance and views:
  - `export type List_1<T> = List_1$instance<T> & __List_1$views<T>;`

### Method: FindMatchingInterface - FIX D
```csharp
private static TypeReference? FindMatchingInterface(TypeSymbol type, TypeReference viewInterfaceRef)
```
**Lines:** 283-300

**Purpose:** Find the matching interface in `type.Interfaces` that corresponds to the view's interface reference, with correct type arguments substituted.

**Problem it solves:**
```csharp
// ViewPlanner creates ExplicitView with interface reference like:
view.InterfaceReference = IEnumerable<T>  // Generic parameter T (open generic)

// But class implements the interface with concrete type:
class List<T> : IEnumerable<T> { }
type.Interfaces = [ IEnumerable<T> ]  // Actual type argument matches class's T

// WITHOUT FIX D:
interface __List_1$views<T> {
  readonly IEnumerable_1$view: IEnumerable_1<T>;  // Correct by luck
}

// But for classes with different type parameters:
class Dictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>> { }
view.InterfaceReference = IEnumerable<T>  // Wrong! Should be KeyValuePair<TKey, TValue>

// WITH FIX D:
interface __Dictionary_2$views<TKey, TValue> {
  readonly IEnumerable_1$view: IEnumerable_1<KeyValuePair<TKey, TValue>>;  // Correct!
}
```

**Algorithm:**
1. **Get view interface base name:**
   - Call `GetInterfaceBaseName(viewInterfaceRef)` to extract interface name
   - Example: `IEnumerable\`1` (without type arguments)

2. **Search type's implemented interfaces:**
   - For each `implementedInterface` in `type.Interfaces`:
     - Get base name: `GetInterfaceBaseName(implementedInterface)`
     - If base names match: return `implementedInterface`

3. **Return:**
   - Matched interface (with correct type arguments), or `null` if not found

**Why needed:** ViewPlanner stores interface references with generic parameters from the interface's perspective, but when emitting companion views, we need the type arguments from the implementing class's perspective.

**Example:**
```csharp
// Input:
type = Dictionary<TKey, TValue> (implements IEnumerable<KeyValuePair<TKey, TValue>>)
viewInterfaceRef = IEnumerable<T> (from ViewPlanner, generic param)

// Processing:
viewBaseName = "IEnumerable`1"
type.Interfaces[0] = IEnumerable<KeyValuePair<TKey, TValue>>
implBaseName = "IEnumerable`1"
Match! Return IEnumerable<KeyValuePair<TKey, TValue>>

// Used in EmitCompanionViewsInterface:
var matchedInterface = FindMatchingInterface(type, view.InterfaceReference);
// Result: IEnumerable<KeyValuePair<TKey, TValue>> (not IEnumerable<T>)
```

**Called by:** `EmitCompanionViewsInterface` at line 267 for each view property

**Impact:** Prevents generic parameter leaks in companion views. Without this, view properties would use orphaned generic parameters instead of the class's actual type arguments.

### Method: GetInterfaceBaseName - FIX D Helper
```csharp
private static string GetInterfaceBaseName(TypeReference typeRef)
```
**Lines:** 306-314

**Purpose:** Extract the base name (without type arguments) from an interface reference for matching purposes.

**Examples:**
```csharp
NamedTypeReference("IEnumerator`1", [T])           → "IEnumerator`1"
NamedTypeReference("IEnumerable`1", [string])      → "IEnumerable`1"
NestedTypeReference("Outer`1+Inner")               → "Inner"
```

**Algorithm:**
- `NamedTypeReference named` → `named.Name` (just the name with arity marker)
- `NestedTypeReference nested` → `nested.NestedName`
- Other types → `typeRef.ToString` or empty string

**Why needed:** Interface matching must ignore type arguments and compare only the base generic definition. `IEnumerable<T>` and `IEnumerable<string>` both have base name `IEnumerable\`1`.

**Used by:** `FindMatchingInterface` to compare interface names

**Note:** Returns name WITH backtick arity (e.g., `IEnumerable\`1`), not without (e.g., `IEnumerable`). This is critical for distinguishing generic arities.

### Method: QualifyViewInterface - TS2304 FIX
```csharp
private static string QualifyViewInterface(
    Model.Types.TypeReference interfaceRef,
    string currentNamespace,
    TypeNameResolver resolver,
    BuildContext ctx)
```
**Lines:** 408-450

**Purpose:** Qualify view interface types with namespace alias if they're cross-namespace. This fixes TS2304 "Cannot find name" errors for transitively implemented interfaces.

**Problem it solves:**
```typescript
// WITHOUT QualifyViewInterface:
interface __List_1$views<T> {
  readonly IEnumerable_1$view: IEnumerable_1<T>;  // TS2304: Cannot find name 'IEnumerable_1'
}

// WITH QualifyViewInterface:
interface __List_1$views<T> {
  readonly IEnumerable_1$view: System_Collections_Generic_Internal.System.Collections.Generic.IEnumerable_1<T>;
}
```

**Why needed:** View interfaces that are transitively implemented (not directly in the implements clause) don't get added to `ValueImportQualifiedNames` by ImportPlanner, so they need manual cross-namespace qualification.

**Algorithm:**
1. **Print base type name:**
   - Use `TypeRefPrinter.Print` to get initial type name

2. **Check if it's a NamedTypeReference:**
   - Extract namespace from CLR full name (before last dot)
   - Example: `System.Collections.Generic.IEnumerable\`1` → namespace is `System.Collections.Generic`

3. **Check if cross-namespace:**
   - Compare `interfaceNamespace` with `currentNamespace`
   - If same namespace, return base name (no qualification needed)
   - If already qualified (contains `.`), return as-is

4. **Qualify with namespace alias:**
   - Generate alias: `interfaceNamespace.Replace('.', '_') + "_Internal"`
   - Example: `System.Collections.Generic` → `System_Collections_Generic_Internal`
   - Return: `{alias}.{interfaceNamespace}.{baseName}`

**Example:**
```csharp
// Input:
interfaceRef = IEnumerable<T> (from System.Collections.Generic)
currentNamespace = "System.Collections"

// Processing:
baseName = "IEnumerable_1<T>"
interfaceNamespace = "System.Collections.Generic"
Different from currentNamespace → needs qualification

// Output:
"System_Collections_Generic_Internal.System.Collections.Generic.IEnumerable_1<T>"
```

**Called by:** `EmitCompanionViewsInterface` at line 398 when emitting each view property

**Impact:** Eliminated 1 TS2304 error in System.Collections namespace. Prevents "Cannot find name" errors for transitive interface references in companion views.

**Addition:** This method was added in to handle edge cases where ImportPlanner doesn't track transitive interfaces (interfaces not directly in the class's implements clause but inherited through interface inheritance).

### Method: NamespaceUsesSupportTypes - TS2304 FIX
```csharp
private static bool NamespaceUsesSupportTypes(NamespaceSymbol ns)
```
**What it does:**
- Scans all public types and their ClassSurface/StaticSurface members
- **Addition:** Also checks constructor parameters for unsafe types
- Checks if any use `PointerTypeReference` or `ByRefTypeReference`
- Returns `true` if support types import needed

**Constructor Checking (lines 532-541):**
```csharp
// Check constructors for unsafe types
foreach (var ctor in type.Members.Constructors)
{
    // Constructors are always on class surface
    foreach (var param in ctor.Parameters)
    {
        if (ContainsUnsafeType(param.Type))
            return true;
    }
}
```

**Why needed:** Constructors with pointer/byref parameters need TSUnsafePointer/TSByRef imports, but constructors weren't being checked before . This caused missing import errors.

**Impact:** Eliminated 1 TS2304 error for `TSUnsafePointer` in namespaces with unsafe constructor parameters.

### Method: ContainsUnsafeType
```csharp
private static bool ContainsUnsafeType(Model.Types.TypeReference typeRef)
```
**What it does:**
- Recursively checks type reference for pointers/byrefs
- Handles arrays (checks element type), named types (checks type arguments), nested types

### Output Format Example

```typescript
// Generated by tsbindgen - tsbindgen
// Namespace: System.Collections.Generic
// Assembly: System.Private.CoreLib

// Branded primitive types for CLR numeric types
export type sbyte = number & { __brand: "sbyte" };
export type byte = number & { __brand: "byte" };
export type int = number & { __brand: "int" };
// ... all primitives ...

// Import support types for unsafe CLR constructs
import type { TSUnsafePointer, TSByRef } from "../_support/types.js";

// Import types from other namespaces
import type { Object, ValueType } from "../System/internal/index.js";

export namespace System.Collections.Generic {
    // With companion views:
    export class List_1$instance<T> {
        constructor(capacity: int);
        readonly Count: int;
        Add(item: T): void;
    }

    export interface __List_1$views<T> {
        readonly IEnumerable_1$view: IEnumerable_1<T>;
    }

    export type List_1<T> = List_1$instance<T> & __List_1$views<T>;

    // Without views:
    export interface IEnumerable_1<T> {
        GetEnumerator: IEnumerator_1<T>;
    }
}
```

---

## File: MetadataEmitter.cs

### Purpose
Generates `metadata.json` files with CLR-specific information that TypeScript can't represent. Used by Tsonic compiler for correct C# code generation.

### Record: NamespaceMetadata
```csharp
public sealed record NamespaceMetadata
{
    public required string Namespace { get; init; }
    public required List<string> ContributingAssemblies { get; init; }
    public required List<TypeMetadata> Types { get; init; }
}
```

### Record: TypeMetadata
```csharp
public sealed record TypeMetadata
{
    public required string StableId { get; init; }          // "System.Private.CoreLib:System.Collections.Generic.List`1"
    public required string ClrName { get; init; }           // "System.Collections.Generic.List`1"
    public required string TsEmitName { get; init; }        // "List_1"
    public required string Kind { get; init; }              // "Class"
    public required string Accessibility { get; init; }      // "Public"
    public required bool IsAbstract { get; init; }
    public required bool IsSealed { get; init; }
    public required bool IsStatic { get; init; }
    public required int Arity { get; init; }
    public required List<MethodMetadata> Methods { get; init; }
    public required List<PropertyMetadata> Properties { get; init; }
    public required List<FieldMetadata> Fields { get; init; }
    public required List<EventMetadata> Events { get; init; }
    public required List<ConstructorMetadata> Constructors { get; init; }
}
```

### Record: MethodMetadata
```csharp
public sealed record MethodMetadata
{
    public required string StableId { get; init; }
    public required string ClrName { get; init; }                  // "Add"
    public required string TsEmitName { get; init; }               // "Add" or "IEnumerable_1$view$Add"
    public required string NormalizedSignature { get; init; }
    public required string Provenance { get; init; }               // "Direct", "Inherited", "ExplicitImpl"
    public required string EmitScope { get; init; }                // "ClassSurface", "ViewOnly"
    public required bool IsStatic { get; init; }
    public required bool IsAbstract { get; init; }
    public required bool IsVirtual { get; init; }
    public required bool IsOverride { get; init; }
    public required bool IsSealed { get; init; }
    public required int Arity { get; init; }
    public required int ParameterCount { get; init; }
    public string? SourceInterface { get; init; }                  // For ViewOnly members
}
```

### Record: PropertyMetadata
```csharp
public sealed record PropertyMetadata
{
    public required string StableId { get. init; }
    public required string ClrName { get. init; }
    public required string TsEmitName { get. init; }
    public required string NormalizedSignature { get. init; }
    public required string Provenance { get; init; }
    public required string EmitScope { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsAbstract { get; init; }
    public required bool IsVirtual { get; init; }
    public required bool IsOverride { get; init; }
    public required bool IsIndexer { get; init; }
    public required bool HasGetter { get; init; }
    public required bool HasSetter { get; init; }
    public string? SourceInterface { get; init; }
}
```

### Record: FieldMetadata
```csharp
public sealed record FieldMetadata
{
    public required string StableId { get; init; }    public required string ClrName { get. init; }
    public required string TsEmitName { get. init; }
    public required string NormalizedSignature { get. init; }
    public required bool IsStatic { get. init; }
    public required bool IsReadOnly { get. init; }
    public required bool IsLiteral { get; init; }}
```

### Record: EventMetadata
```csharp
public sealed record EventMetadata
{
    public required string StableId { get; init; }    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get; init; }
}
```

### Record: ConstructorMetadata
```csharp
public sealed record ConstructorMetadata
{
    public required string StableId { get; init; }    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get. init; }
    public required int ParameterCount { get; init; }
}
```

### Method: Emit
```csharp
public static void Emit(BuildContext ctx, EmissionPlan plan, string outputDirectory)
```
**What it does:**
- Iterates through `plan.EmissionOrder.Namespaces`
- For each namespace, generates metadata via `GenerateMetadata`
- Writes to `output/{namespace}/internal/metadata.json`
- Uses indented JSON format with null value omission

### Method: GenerateMetadata
```csharp
private static NamespaceMetadata GenerateMetadata(BuildContext ctx, NamespaceEmitOrder nsOrder)
```
**What it does:**
- Creates `NamespaceMetadata` with namespace name and contributing assemblies
- For each type in `nsOrder.OrderedTypes`, calls `GenerateTypeMetadata`

### Method: GenerateTypeMetadata
```csharp
private static TypeMetadata GenerateTypeMetadata(TypeSymbol type, BuildContext ctx)
```
**What it does:**
- Gets final TypeScript name from `ctx.Renamer.GetFinalTypeName(type)`
- Creates `TypeMetadata` with all type information
- Generates metadata for all member kinds (methods, properties, fields, events, constructors)

### Method: GenerateMethodMetadata
```csharp
private static MethodMetadata GenerateMethodMetadata(MethodSymbol method, TypeSymbol declaringType, BuildContext ctx)
```
**What it does:**
- Uses **view scope** for ViewOnly members (via `ScopeFactory.ViewSurface`)
- Uses **class scope** for ClassSurface members (via `ScopeFactory.ClassSurface`)
- Gets final TS name from `ctx.Renamer.GetFinalMemberName`
- Generates normalized signature via `SignatureNormalization.NormalizeMethod`

**Key Decision:**
- ViewOnly members get view-scoped names: `"IEnumerable_1$view$GetEnumerator"`
- ClassSurface members get class-scoped names: `"GetEnumerator"`

### Output Format Example

```json
{
  "namespace": "System.Collections.Generic",
  "contributingAssemblies": [
    "System.Private.CoreLib"
  ],
  "types": [
    {
      "clrName": "System.Collections.Generic.List`1",
      "tsEmitName": "List_1",
      "kind": "Class",
      "accessibility": "Public",
      "isAbstract": false,
      "isSealed": false,
      "isStatic": false,
      "arity": 1,
      "methods": [
        {
          "clrName": "Add",
          "tsEmitName": "Add",
          "normalizedSignature": "Add(T):System.Void",
          "provenance": "Direct",
          "emitScope": "ClassSurface",
          "isStatic": false,
          "isAbstract": false,
          "isVirtual": false,
          "isOverride": false,
          "isSealed": false,
          "arity": 0,
          "parameterCount": 1
        },
        {
          "clrName": "GetEnumerator",
          "tsEmitName": "IEnumerable_1$view$GetEnumerator",
          "normalizedSignature": "GetEnumerator:System.Collections.Generic.IEnumerator`1<T>",
          "provenance": "ExplicitImpl",
          "emitScope": "ViewOnly",
          "isStatic": false,
          "isAbstract": false,
          "isVirtual": false,
          "isOverride": false,
          "isSealed": false,
          "arity": 0,
          "parameterCount": 0,
          "sourceInterface": "System.Collections.Generic.IEnumerable`1"
        }
      ],
      "properties": [],
      "fields": [],
      "events": [],
      "constructors": [
        {
          "normalizedSignature": ".ctor(System.Int32):System.Void",
          "isStatic": false,
          "parameterCount": 1
        }
      ]
    }
  ]
}
```

---

## File: BindingEmitter.cs

### Purpose
Generates `bindings.json` files with CLR-to-TypeScript name mappings. Used by runtime for binding and Tsonic compiler for code generation.

### Record: NamespaceBindings
```csharp
public sealed record NamespaceBindings
{
    public required string Namespace { get; init; }
    public required List<TypeBinding> Types { get; init; }
}
```

### Record: TypeBinding
```csharp
public sealed record TypeBinding
{
    public required string StableId { get; init; }          // "System.Private.CoreLib:System.Collections.Generic.List`1"
    public required string ClrName { get; init; }           // "System.Collections.Generic.List`1"
    public required string TsEmitName { get; init; }        // "List_1"
    public required string AssemblyName { get; init; }      // "System.Private.CoreLib"
    public required int MetadataToken { get; init; }        // 0 for types
    public required List<MethodBinding> Methods { get; init; }
    public required List<PropertyBinding> Properties { get; init; }
    public required List<FieldBinding> Fields { get; init; }
    public required List<EventBinding> Events { get; init; }
    public required List<ConstructorBinding> Constructors { get; init; }
}
```

### Record: MethodBinding
```csharp
public sealed record MethodBinding
{
    public required string StableId { get; init; }              // "System.Private.CoreLib:System.Collections.Generic.List`1::Add(!0):System.Void"
    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required int MetadataToken { get; init; }
    public required string CanonicalSignature { get; init; }    public required string NormalizedSignature { get; init; }    public required string EmitScope { get; init; }
    public required int Arity { get; init; }
    public required int ParameterCount { get; init; }
    public required string DeclaringClrType { get; init; }
    public required string DeclaringAssemblyName { get; init; }
}
```

### Record: PropertyBinding
```csharp
public sealed record PropertyBinding
{
    public required string StableId { get; init; }    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required int MetadataToken { get; init; }
    public required string CanonicalSignature { get; init; }
    public required string NormalizedSignature { get; init; }
    public required string EmitScope { get; init; }
    public required bool IsIndexer { get; init; }
    public required bool HasGetter { get; init; }
    public required bool HasSetter { get; init; }
    public required string DeclaringClrType { get; init; }
    public required string DeclaringAssemblyName { get; init; }
}
```

### Record: FieldBinding
```csharp
public sealed record FieldBinding
{
    public required string StableId { get; init; }    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required int MetadataToken { get; init; }
    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsReadOnly { get; init; }
    public required string DeclaringClrType { get; init; }
    public required string DeclaringAssemblyName { get; init; }
}
```

### Record: EventBinding
```csharp
public sealed record EventBinding
{
    public required string StableId { get; init; }    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required int MetadataToken { get; init; }
    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get; init; }
    public required string DeclaringClrType { get; init; }
    public required string DeclaringAssemblyName { get; init; }
}
```

### Record: ConstructorBinding
```csharp
public sealed record ConstructorBinding
{
    public required string StableId { get; init; }    public required int MetadataToken { get; init; }
    public required string CanonicalSignature { get; init; }
    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get; init; }
    public required int ParameterCount { get; init; }
    public required string DeclaringClrType { get; init; }
    public required string DeclaringAssemblyName { get; init; }
}
```

### Method: Emit
```csharp
public static void Emit(BuildContext ctx, EmissionPlan plan, string outputDirectory)
```
**What it does:**
- Iterates through `plan.EmissionOrder.Namespaces`
- For each namespace, generates bindings via `GenerateBindings`
- Writes to `output/{namespace}/bindings.json`

### Method: GenerateBindings
```csharp
private static NamespaceBindings GenerateBindings(BuildContext ctx, NamespaceEmitOrder nsOrder)
```
**What it does:**
- Creates `NamespaceBindings` with namespace name
- For each type in `nsOrder.OrderedTypes`, calls `GenerateTypeBinding`

### Method: GenerateTypeBinding
```csharp
private static TypeBinding GenerateTypeBinding(TypeSymbol type, BuildContext ctx)
```
**What it does:**
- Gets final TypeScript name from `ctx.Renamer.GetFinalTypeName(type)`
- Includes **ALL members** (ClassSurface, StaticSurface, AND ViewOnly)
- ViewOnly members get view-scoped names (e.g., `"IEnumerable_1$view$GetEnumerator"`)

**Key Decision:**
- Bindings include ViewOnly members (unlike declarations which only emit to views)
- This allows runtime to bind to explicit interface implementations

### Output Format Example

```json
{
  "namespace": "System.Collections.Generic",
  "types": [
    {
      "stableId": "System.Private.CoreLib:System.Collections.Generic.List`1",
      "clrName": "System.Collections.Generic.List`1",
      "tsEmitName": "List_1",
      "assemblyName": "System.Private.CoreLib",
      "metadataToken": 0,
      "methods": [
        {
          "stableId": "System.Private.CoreLib:System.Collections.Generic.List`1::Add(!0):System.Void",
          "clrName": "Add",
          "tsEmitName": "Add",
          "metadataToken": 100663359,
          "canonicalSignature": "Add(!0):System.Void",
          "normalizedSignature": "Add(T):System.Void",
          "emitScope": "ClassSurface",
          "arity": 0,
          "parameterCount": 1
        },
        {
          "clrName": "GetEnumerator",
          "tsEmitName": "IEnumerable_1$view$GetEnumerator",
          "metadataToken": 100663360,
          "canonicalSignature": "GetEnumerator:System.Collections.Generic.IEnumerator`1<!0>",
          "normalizedSignature": "GetEnumerator:System.Collections.Generic.IEnumerator`1<T>",
          "emitScope": "ViewOnly",
          "arity": 0,
          "parameterCount": 0
        }
      ],
      "properties": [],
      "fields": [],
      "events": [],
      "constructors": []
    }
  ]
}
```

---

## File: ModuleStubEmitter.cs

### Purpose
Generates `index.js` stub files that throw at runtime. Prevents accidental execution while allowing TypeScript type checking.

### Method: Emit
```csharp
public static void Emit(BuildContext ctx, EmissionPlan plan, string outputDirectory)
```
**What it does:**
- Iterates through `plan.EmissionOrder.Namespaces`
- For each namespace, generates stub via `GenerateStub`
- Writes to `output/{namespace}/index.js`

### Method: GenerateStub
```csharp
private static string GenerateStub(string namespaceName)
```
**What it does:**
- Creates JavaScript file with `throw new Error(...)` statement
- Error message explains this is type-only module requiring .NET runtime

### Output Format Example

```javascript
// Generated by tsbindgen - tsbindgen
// Namespace: System.Collections.Generic
// Module Stub - Do Not Execute

throw new Error(
  'Cannot import CLR namespace System.Collections.Generic in JavaScript runtime. ' +
  'This module provides TypeScript type definitions only. ' +
  'Actual implementation requires .NET runtime via Tsonic compiler.'
);
```

---

## File: SupportTypesEmitter.cs

### Purpose
Generates centralized `_support/types.d.ts` with marker types for unsafe CLR constructs. Emitted once for entire generation, not per-namespace.

### Method: Emit
```csharp
public static void Emit(BuildContext ctx, string outputDirectory)
```
**What it does:**
- Creates `_support/` directory
- Generates content via `GenerateSupportTypes`
- Writes to `output/_support/types.d.ts`

### Method: GenerateSupportTypes
```csharp
private static string GenerateSupportTypes
```
**What it does:**
- Emits `TSUnsafePointer<T>` type for pointer types (void*, int*, T*)
- Emits `TSByRef<T>` type for ref/out/in parameters

**Key Design:**
- `TSUnsafePointer<T>` erases to `unknown` for type safety (forces explicit handling)
- `TSByRef<T>` provides structural access via `.value` property
- Both use unique symbols for branding (maintains identity for auditing)

### Output Format Example

```typescript
// Generated by tsbindgen - tsbindgen
// Support Types Module
//
// These are opaque placeholders for CLR constructs that have no TS equivalent.
// They intentionally erase to `unknown` to keep the API sound.

/**
 * Marker type for C# pointer types (void*, int*, T*, etc.).
 * Erases to `unknown` for type safety - forces explicit handling.
 * The generic parameter preserves information about the pointed-to type.
 * Branded with unique symbol to maintain identity for auditing.
 */
export type TSUnsafePointer<T> = unknown & { readonly __tsbindgenPtr?: unique symbol };

/**
 * Marker type for C# ref/out/in parameters.
 * Provides structural access via `.value` to match C# semantics.
 * Branded with unique symbol to maintain identity for auditing.
 * Example: `method(arg: TSByRef<int>)` → caller uses `arg.value`
 */
export type TSByRef<T> = { value: T } & { readonly __tsbindgenByRef?: unique symbol };
```

---

## File: TypeMap.cs

### Purpose
Maps CLR built-in types to TypeScript types. Short-circuits graph lookups for primitives and special types. **CRITICAL:** Must be checked BEFORE TypeIndex lookup to avoid PG_LOAD_001 false positives.

### Method: TryMapBuiltin
```csharp
public static bool TryMapBuiltin(string fullName, out string tsType)
```
**What it does:**
- Returns `true` if `fullName` is a known built-in type
- Sets `tsType` to the corresponding TypeScript type
- Returns `false` for non-built-in types

**Mappings:**

| CLR Type | TypeScript Type | Notes |
|----------|-----------------|-------|
| `System.Void` | `void` | |
| `System.Boolean` | `boolean` | |
| `System.String` | `string` | |
| `System.Object` | `any` | Maximum compatibility |
| `System.Char` | `string` | TS has no char type |
| `System.SByte` | `sbyte` | Branded type |
| `System.Byte` | `byte` | Branded type |
| `System.Int16` | `short` | Branded type |
| `System.UInt16` | `ushort` | Branded type |
| `System.Int32` | `int` | Branded type |
| `System.UInt32` | `uint` | Branded type |
| `System.Int64` | `long` | Branded type |
| `System.UInt64` | `ulong` | Branded type |
| `System.IntPtr` | `nint` | Branded type |
| `System.UIntPtr` | `nuint` | Branded type |
| `System.Single` | `float` | Branded type |
| `System.Double` | `double` | Branded type |
| `System.Decimal` | `decimal` | Branded type |
| `System.Array` | `any[]` | Base array type |
| `System.ValueType` | `any` | Base value type |
| `System.Enum` | `number` | Base enum type |
| `System.Delegate` | `Function` | |
| `System.MulticastDelegate` | `Function` | |

### Method: IsUnsupportedSpecialForm
```csharp
public static bool IsUnsupportedSpecialForm(string fullName, bool isPointer, bool isByRef, bool isFunctionPointer)
```
**What it does:**
- Returns `true` if type is pointer, byref, or function pointer
- These require special handling or substitution

### Method: MapUnsupportedSpecialForm
```csharp
public static string MapUnsupportedSpecialForm(string fullName, bool isPointer, bool isByRef, bool isFunctionPointer, bool allowUnsafeMaps)
```
**What it does:**
- If `allowUnsafeMaps` is `false`, throws exception
- If `allowUnsafeMaps` is `true`, returns `"any"`

### Method: IsBrandedPrimitive
```csharp
public static bool IsBrandedPrimitive(string fullName)
```
**What it does:**
- Returns `true` for numeric types that should use branded syntax
- These are emitted as type aliases in file preamble

---

## File: TypeNameResolver.cs

### Purpose
Single source of truth for resolving TypeScript identifiers from TypeReferences. Uses the Renamer to ensure imports and declarations use identical names.

### Constructor
```csharp
public TypeNameResolver(BuildContext ctx, SymbolGraph graph, ImportPlan? importPlan = null,
    string? currentNamespace = null, bool facadeMode = false)
```
**What it does:**
- Stores `BuildContext` (for Renamer access)
- Stores `SymbolGraph` (for TypeIndex lookups)
- Stores `ImportPlan` (optional - for value import qualification, TS2693 fix)
- Stores `currentNamespace` (optional - for cross-namespace qualification)
- Stores `facadeMode` flag (TS2304 FIX - enables cross-namespace qualification in facades)

**Addition**: `facadeMode` parameter added to enable cross-namespace type qualification in facade constraint clauses. When true, all cross-namespace type references are qualified with namespace alias to prevent TS2304 "Cannot find name" errors.

### Method: For(TypeSymbol)
```csharp
public string For(Model.Symbols.TypeSymbol type)
```
**What it does:**
- Returns final TypeScript identifier for a `TypeSymbol`
- Directly queries `ctx.Renamer.GetFinalTypeName(type)`

### Method: ResolveTypeName(NamedTypeReference)
```csharp
public string ResolveTypeName(NamedTypeReference named)
```
**What it does:**
- Wrapper for `For(NamedTypeReference)` to provide consistent API

### Method: For(NamedTypeReference)
```csharp
public string For(NamedTypeReference named)
```
**What it does:**
1. **Try TypeMap FIRST** - Short-circuit built-in types before graph lookup (prevents PG_LOAD_001)
2. **Check value import qualification** - TS2693 fix for value-imported types
3. **Look up TypeSymbol in graph** - Uses StableId (`{AssemblyName}:{FullName}`)
4. **If not in graph** - Type is external (from another assembly), sanitize CLR name
5. **Get final TypeScript name from Renamer** - Single source of truth
6. **Facade mode qualification** - Qualify cross-namespace types

**Algorithm:**
```csharp
// 1. Try TypeMap (built-ins like System.Int32 → int)
if (TypeMap.TryMapBuiltin(named.FullName, out var builtinType))
    return builtinType;

// 2. TS2693 FIX: Check value import qualification
if (_importPlan != null && _currentNamespace != null)
{
    if (_importPlan.ValueImportQualifiedNames.TryGetValue((_currentNamespace, clrFullName), out var qualifiedName))
        return qualifiedName;
}

// 3. Look up in graph
var stableId = $"{named.AssemblyName}:{named.FullName}";
if (!_graph.TypeIndex.TryGetValue(stableId, out var typeSymbol))
{
    // External type - sanitize CLR name
    var finalExternalName = SanitizeClrName(simpleName);

    // TS2304 FIX (Facade - ): Qualify external cross-namespace types
    if (_facadeMode && _currentNamespace != null && externalNamespace != _currentNamespace)
    {
        return $"{GetNamespaceAlias(externalNamespace)}.{finalExternalName}";
    }

    return finalExternalName;
}

// 4. Get final name from Renamer
var finalName = _ctx.Renamer.GetFinalTypeName(typeSymbol);

// 5. TS2304 FIX (Facade - ): Qualify cross-namespace types in facade mode
if (_facadeMode && _currentNamespace != null)
{
    var targetNamespace = typeSymbol.Namespace;
    if (targetNamespace != _currentNamespace)
    {
        return $"{GetNamespaceAlias(targetNamespace)}.{finalName}";
    }
}

return finalName;
```

**Facade Mode Logic**:
- When `facadeMode` is true, cross-namespace type references are qualified with namespace alias
- Example: `IEquatable_1` → `System.IEquatable_1` in `System.Buffers` facade
- Prevents TS2304 "Cannot find name" errors in facade constraint clauses
- Eliminated 7 TS2304 errors in facades

### Method: SanitizeClrName
```csharp
private static string SanitizeClrName(string clrName)
```
**What it does:**
- Replaces generic arity backtick: `List`1` → `List_1`
- Replaces nested type separator: `Foo+Bar` → `Foo_Bar`
- Removes invalid TypeScript identifier characters

### Method: TryMapPrimitive
```csharp
public static string? TryMapPrimitive(string clrFullName)
```
**What it does:**
- Static helper that calls `TypeMap.TryMapBuiltin`
- Returns `null` if not a primitive

### Method: IsPrimitive
```csharp
public static bool IsPrimitive(string clrFullName)
```
**What it does:**
- Returns `true` if type is a primitive that doesn't need imports

---

## File: Shared/NameUtilities.cs

### Purpose
Implements the **CLR-name contract** - a consistent naming policy used across all emission surfaces (class members, interface members, views). Ensures interfaces and classes emit matching member names to prevent TS2420 errors ("Class incorrectly implements interface").

**Key Principle:** Use CLR names (PascalCase from reflection), sanitize reserved words, NEVER use numeric suffixes.

### What is the CLR-Name Contract?

The CLR-name contract is a naming policy that ensures consistent member names across:
- Class surface members (`EmitScope = ClassSurface`)
- Interface members
- Explicit interface implementations (`EmitScope = ViewOnly`)

**Policy rules:**
1. **Start with CLR name** - Use the original PascalCase name from `System.Reflection`
2. **Sanitize reserved words** - Append `_` if name conflicts with TypeScript/JavaScript reserved words
3. **NEVER use numeric suffixes** - Do not emit `equals2`, `getHashCode3`, etc. (renaming artifacts)

**Why this matters:**
Without consistent naming, interfaces and classes can have mismatched member names:
```typescript
// BAD - Without CLR-name contract
interface IDisposable {
    Dispose: void;  // CLR name
}
class FileStream implements IDisposable {
    dispose: void;  // lowercase!
}
// TS2420: Class 'FileStream' incorrectly implements interface 'IDisposable'.
// Property 'Dispose' is missing in type 'FileStream' but required in type 'IDisposable'.
```

**Impact:** Reduced TS2420 errors by 81% (579 → ~100 errors)

### Method: ApplyClrSurfaceNamePolicy
```csharp
public static string ApplyClrSurfaceNamePolicy(string clrName)
```
**What it does:**
- **Main public API** for applying CLR-name contract
- Takes a CLR name (e.g., `"Dispose"`, `"GetHashCode"`, `"ToString"`)
- Returns sanitized identifier safe for TypeScript emission
- Used by PhaseGate validators to verify interface/class compatibility

**Algorithm:**
1. Call `SanitizeIdentifier(clrName)` to handle reserved words
2. Return sanitized name (no numeric suffix handling needed - contract forbids them)

**Examples:**
```csharp
ApplyClrSurfaceNamePolicy("Dispose")    → "Dispose"
ApplyClrSurfaceNamePolicy("GetType")    → "GetType"
ApplyClrSurfaceNamePolicy("ToString")   → "ToString"
ApplyClrSurfaceNamePolicy("default")    → "default_"  // Reserved word
ApplyClrSurfaceNamePolicy("class")      → "class_"    // Reserved word
```

**Usage in PhaseGate:**
```csharp
// From Plan/Validation/Names.cs:ValidateClrSurfaceNamePolicy
var surfaceNames = new HashSet<string>;

// Build class surface set
foreach (var method in classMethods)
{
    var clrName = method.ClrName;
    var surfaceName = NameUtilities.ApplyClrSurfaceNamePolicy(clrName);
    surfaceNames.Add(surfaceName);
}

// Validate interfaces
foreach (var ifaceMethod in interfaceMethods)
{
    var expectedName = NameUtilities.ApplyClrSurfaceNamePolicy(ifaceMethod.ClrName);
    if (!surfaceNames.Contains(expectedName))
    {
        // ERROR: TBG8A1 (SurfaceNamePolicyMismatch)
    }
}
```

### Method: SanitizeIdentifier
```csharp
private static string SanitizeIdentifier(string name)
```
**What it does:**
- Appends `_` to TypeScript/JavaScript reserved words
- Uses `Renaming.TypeScriptReservedWords.Sanitize` for actual reserved word checking

**Examples:**
```csharp
SanitizeIdentifier("GetType")     → "GetType"      // Not reserved
SanitizeIdentifier("default")     → "default_"     // Reserved word
SanitizeIdentifier("class")       → "class_"       // Reserved word
SanitizeIdentifier("implements")  → "implements_"  // Reserved word
SanitizeIdentifier("interface")   → "interface_"   // Reserved word
```

**Reserved words handled:**
- JavaScript keywords: `class`, `default`, `interface`, `implements`, `const`, `let`, `var`, etc.
- Future reserved: `enum`, `await`, `yield`, etc.
- Strict mode reserved: `private`, `protected`, `public`, etc.

### Method: HasNumericSuffix
```csharp
public static bool HasNumericSuffix(string name)
```
**What it does:**
- Detects if a name ends with numeric digits (e.g., `equals2`, `getHashCode3`)
- Used by PhaseGate validator `PG_NAME_SURF_002` (currently disabled)
- Returns `true` if name ends with one or more digits

**Algorithm:**
```csharp
// Start from end of string
int i = name.Length - 1;
while (i >= 0 && char.IsDigit(name[i]))
    i--;

// If we moved backward, we found trailing digits
return i < name.Length - 1;
```

**Examples:**
```csharp
HasNumericSuffix("Dispose")      → false
HasNumericSuffix("GetHashCode")  → false
HasNumericSuffix("ToInt32")      → true   // Legitimate CLR name!
HasNumericSuffix("equals2")      → true   // Renaming artifact (BAD)
HasNumericSuffix("ToString3")    → true   // Renaming artifact (BAD)
```

**Why PG_NAME_SURF_002 is disabled:**
This method cannot distinguish between:
- Legitimate CLR names with numbers (`ToInt32`, `UTF8Encoding`, `MD5`)
- Renaming artifacts (`equals2`, `toString3`)

To re-enable the validator, it would need to compare against original CLR names to detect if suffix was ADDED.

### Method: IsNonNumericOverride
```csharp
private static bool IsNonNumericOverride(string clrName, string renamedName)
```
**What it does:**
- Detects if renamer applied a semantic override (not a numeric suffix)
- Returns `true` if renamed name differs from CLR name in a non-numeric way
- Used internally (currently unused, but designed for future disambiguation logic)

**Algorithm:**
```csharp
// Same name - no override
if (clrName == renamedName)
    return false;

// Check if renamed name is clrName + digits
if (renamedName.StartsWith(clrName))
{
    var suffix = renamedName.Substring(clrName.Length);
    if (suffix.All(char.IsDigit))
        return false;  // Numeric override (e.g., ToString → ToString2)
}

// Otherwise semantic override (e.g., ToString → ToString_)
return true;
```

**Examples:**
```csharp
IsNonNumericOverride("ToString", "ToString")    → false  // No override
IsNonNumericOverride("ToString", "ToString2")   → false  // Numeric override
IsNonNumericOverride("ToString", "ToString3")   → false  // Numeric override
IsNonNumericOverride("ToString", "ToString_")   → true   // Semantic override (reserved word)
IsNonNumericOverride("GetType", "GetType_Foo")  → true   // Semantic override (different suffix)
```

### Integration with PhaseGate

**Validator: PG_NAME_SURF_001** (TBG8A1 - SurfaceNamePolicyMismatch)
- Uses `ApplyClrSurfaceNamePolicy` to validate interface/class compatibility
- Ensures emit phase will produce matching names
- Prevents TS2420 errors at generation time (not validation time)

**Validator: PG_NAME_SURF_002** (TBG8A2 - NumericSuffixOnSurface)
- Uses `HasNumericSuffix` to detect renaming artifacts
- Currently DISABLED due to legitimate CLR names with numbers
- See `Plan/Validation/Names.cs` for validator implementations

### Integration with Emit Phase

**ClassPrinter.cs:**
- Does NOT directly use NameUtilities (uses Renamer instead)
- But the CLR-name contract ensures Renamer never produces numeric suffixes on surface members

**MethodPrinter.cs:**
- Does NOT directly use NameUtilities (uses Renamer instead)
- But PhaseGate validation guarantees names will match interfaces

**Design Note:**
NameUtilities is primarily a **validation-time API** (used by PhaseGate), not an emit-time API. The emit phase uses `BuildContext.Renamer` which respects the CLR-name contract through PhaseGate enforcement.

---

## File: Printers/ClassPrinter.cs

### Purpose
Prints TypeScript class declarations from TypeSymbol. Handles classes, structs, static classes, enums, delegates, and interfaces.

### Method: Print
```csharp
public static string Print(
    TypeSymbol type,
    TypeNameResolver resolver,
    BuildContext ctx,
    SymbolGraph graph,
    HashSet<string> typesWithoutGenerics,
    BindingsProvider bindingsProvider,
    Shape.StaticFlatteningPlan staticFlattening,
    Shape.StaticConflictPlan staticConflicts,
    Shape.OverrideConflictPlan overrideConflicts,
    Plan.PropertyOverridePlan? propertyOverrides = null)
```
**What it does:**
- **GUARD:** Never prints non-public types (logs rejection if attempted)
- Dispatches to specialized printer based on `type.Kind`:
  - `TypeKind.Class` → `PrintClass`
  - `TypeKind.Struct` → `PrintStruct`
  - `TypeKind.StaticNamespace` → `PrintStaticClass`
  - `TypeKind.Enum` → `PrintEnum`
  - `TypeKind.Delegate` → `PrintDelegate`
  - `TypeKind.Interface` → `PrintInterface`

**Parameters:**
- `graph` - SymbolGraph for type lookups (needed for TS2693 same-namespace view fix)
- `typesWithoutGenerics` - Set of types losing generics during emission (for alias filtering)
- `bindingsProvider` - Provides member exposure information for emission
- `staticFlattening` - Plan for flattening static-only inheritance hierarchies (D1)
- `staticConflicts` - Plan for suppressing conflicting static members (D2)
- `overrideConflicts` - Plan for suppressing incompatible instance overrides (D3)
- `propertyOverrides` - Plan for property type unification via unions (Step E)

### Method: PrintInstance
```csharp
public static string PrintInstance(
    TypeSymbol type,
    TypeNameResolver resolver,
    BuildContext ctx,
    SymbolGraph graph,
    BindingsProvider bindingsProvider,
    Shape.StaticFlatteningPlan staticFlattening,
    Shape.StaticConflictPlan staticConflicts,
    Shape.OverrideConflictPlan overrideConflicts,
    Plan.PropertyOverridePlan? propertyOverrides = null)
```
**What it does:**
- Prints class/struct with `$instance` suffix (for companion views pattern)
- Used when type has explicit interface views
- Only classes and structs support this (other types fallback to normal Print)
- Passes all plan parameters through to PrintClass/PrintStruct for member emission

**Parameters:**
- `graph` - SymbolGraph for type lookups (needed for TS2693 same-namespace view fix)
- `bindingsProvider` - Provides member exposure information for emission
- `staticFlattening` - Plan for flattening static-only inheritance hierarchies (D1)
- `staticConflicts` - Plan for suppressing conflicting static members (D2)
- `overrideConflicts` - Plan for suppressing incompatible instance overrides (D3)
- `propertyOverrides` - Plan for property type unification via unions (Step E)

### Plan-Based Emission

ClassPrinter uses four plans from the Shape phase to handle TypeScript compatibility issues:

#### 1. StaticFlatteningPlan (D1)

**Purpose:** Flatten static-only inheritance hierarchies by removing `extends` and emitting inherited static members directly.

**Usage in PrintClass:**
```csharp
// Check if type is in flattening plan
if (staticFlattening.FlattenedTypes.TryGetValue(type.StableId.ToString(), out var inheritedStatics))
{
    // Skip emitting "extends" clause
    // Emit inherited static members in addition to own members
}
```

**Effect:**
```typescript
// Before (with extends):
class Sse$instance extends X86Base$instance {
    static Add(...): Vector128_1<float>;
    // Missing base static 'IsSupported' → TS2417
}

// After (flattened):
class Sse$instance {  // No extends!
    static Add(...): Vector128_1<float>;              // Own
    static get IsSupported(): boolean;                 // From base, emitted directly
}
```

#### 2. StaticConflictPlan (D2)

**Purpose:** Suppress static members that conflict with base class static members in hybrid types (types with both static and instance members).

**Usage in EmitStaticMembers:**
```csharp
foreach (var method in type.Members.Methods.Where(m => m.IsStatic))
{
    var key = (type.StableId.ToString(), method.StableId.ToString());
    if (staticConflicts.Suppressions.ContainsKey(key))
    {
        // Skip emitting this static member - base version inherited via extends
        continue;
    }
    // Emit method normally
}
```

**Effect:**
```typescript
// Before (conflict):
class Task_1<TResult> extends Task {
    static get CompletedTask(): Task;  // TS2417: conflicts with base!
}

// After (suppressed):
class Task_1<TResult> extends Task {
    // Static suppressed - base static inherited via extends ✓
}
```

#### 3. OverrideConflictPlan (D3)

**Purpose:** Suppress instance members with incompatible signatures (different return types, parameters) that override base members.

**Usage in EmitMembers:**
```csharp
foreach (var property in type.Members.Properties.Where(p => !p.IsStatic))
{
    var key = (type.StableId.ToString(), property.StableId.ToString());
    if (overrideConflicts.Suppressions.ContainsKey(key))
    {
        // Skip emitting - base property inherited with base signature
        continue;
    }
    // Emit property
}
```

**Effect:**
```typescript
// Before (conflict):
class WebHeaderCollection extends NameValueCollection {
    get_Item(name: string): string[];  // TS2416: incompatible with base!
}

// After (suppressed):
class WebHeaderCollection extends NameValueCollection {
    // Conflicting members suppressed - base members inherited ✓
}
```

#### 4. PropertyOverridePlan (Step E)

**Purpose:** Unify property types across inheritance chains using union types to handle property covariance.

**Usage in EmitMembers (property emission):**
```csharp
// When emitting property type:
var key = (type.StableId.ToString(), property.StableId.ToString());
if (propertyOverrides?.PropertyTypeOverrides.TryGetValue(key, out var unifiedType) == true)
{
    sb.Append(unifiedType);  // Use union type (e.g., "TypeA | TypeB")
}
else
{
    sb.Append(TypeRefPrinter.Print(property.PropertyType, resolver, ctx));  // Normal type
}
```

**Effect:**
```typescript
// Before (covariance error):
class RequestCachePolicy$instance {
    readonly level: RequestCacheLevel;
}
class HttpRequestCachePolicy$instance extends RequestCachePolicy$instance {
    readonly level: HttpRequestCacheLevel;  // TS2416: property covariance!
}

// After (unified with unions):
class RequestCachePolicy$instance {
    readonly level: HttpRequestCacheLevel | RequestCacheLevel;  // Union
}
class HttpRequestCachePolicy$instance extends RequestCachePolicy$instance {
    readonly level: HttpRequestCacheLevel | RequestCacheLevel;  // Same union ✓
}
```

**Plan Flow Summary:**
```
Shape Phase (4.7-4.10)
  ↓ Creates plans
  ↓
EmissionPlan (combines all plans)
  ↓
InternalIndexEmitter
  ↓ Passes plans to ClassPrinter.Print/PrintInstance
  ↓
ClassPrinter.PrintClass/PrintStruct
  ↓ Uses plans during member emission
  ↓
EmitMembers / EmitStaticMembers
  ↓ Checks plans for each member
  ↓ - StaticFlattening: emit inherited statics
  ↓ - StaticConflicts: suppress conflicting statics
  ↓ - OverrideConflicts: suppress incompatible overrides
  ↓ - PropertyOverrides: use union types
  ↓
TypeScript with zero errors ✅
```

### Method: PrintClass
```csharp
private static string PrintClass(
    TypeSymbol type,
    TypeNameResolver resolver,
    BuildContext ctx,
    SymbolGraph graph,
    bool instanceSuffix = false,
    BindingsProvider? bindingsProvider = null,
    Shape.StaticFlatteningPlan? staticFlattening = null,
    Shape.StaticConflictPlan? staticConflicts = null,
    Shape.OverrideConflictPlan? overrideConflicts = null,
    Plan.PropertyOverridePlan? propertyOverrides = null)
```
**What it does:**
1. Gets final TypeScript name from Renamer
2. Adds `$instance` suffix if requested
3. Emits class modifiers (`abstract` if abstract)
4. Emits generic parameters with constraints
5. **STATIC FLATTENING (D1):** Checks if type is in staticFlattening plan - if yes, skips extends clause
6. **TS2693 FIX:** Applies same-namespace view handling to base class (see `ApplyInstanceSuffixForSameNamespaceViews`)
7. **TS2863 FIX:** Filters `any`/`unknown` from extends clause (TypeScript rejects "extends any")
8. Emits base class (`extends BaseClass`, skips Object/ValueType/any/unknown, skips if flattened)
9. **TS2693 FIX:** Applies same-namespace view handling to interface list
10. Emits interfaces (`implements IFoo, IBar`)
11. Calls `EmitMembers` to emit instance and static members (with plan-based suppression/unification)
12. **STATIC FLATTENING (D1):** If type in flattening plan, emits inherited static members from base hierarchy

**Algorithm for base class emission:**
```csharp
if (type.BaseType != null)
{
    var baseTypeName = TypeRefPrinter.Print(type.BaseType, resolver, ctx);
    // TS2693 FIX: For same-namespace types with views, use instance class name
    baseTypeName = ApplyInstanceSuffixForSameNamespaceViews(baseTypeName, type.BaseType, type.Namespace, graph, ctx);

    // CRITICAL: Never emit "extends any" - TypeScript rejects it
    if (baseTypeName != "Object" &&
        baseTypeName != "ValueType" &&
        baseTypeName != "any" &&
        baseTypeName != "unknown")
    {
        sb.Append(" extends ");
        sb.Append(baseTypeName);
    }
}
```

**Algorithm for interface emission:**
```csharp
// TS2304 FIX: Filter out non-public interfaces (not in graph)
var publicInterfaces = type.Interfaces
    .Where(i => IsInterfaceInGraph(i, graph))
    .ToArray;

if (publicInterfaces.Length > 0)
{
    sb.Append(" implements ");
    var interfaceNames = publicInterfaces.Select(i =>
    {
        var name = TypeRefPrinter.Print(i, resolver, ctx);
        // TS2693 FIX: For same-namespace types with views, use instance class name
        return ApplyInstanceSuffixForSameNamespaceViews(name, i, type.Namespace, graph, ctx);
    });
    sb.Append(string.Join(", ", interfaceNames));
}
```

**Change:** Added `IsInterfaceInGraph` filter before emitting implements clause. This prevents non-public interfaces from appearing in the implements clause, which was causing massive cascading TypeScript errors (TS2420, TS2416).

**Why these fixes matter:**

1. **TS2693 (same-namespace views):** Without the fix, when a class extends/implements a type with views in the same namespace, it references the type alias name which isn't accessible inside namespace declarations:
```typescript
// BAD - Without fix
namespace System.Runtime.InteropServices {
    class SafeHandleZeroOrMinusOneIsInvalid extends SafeHandle { }
    // TS2693: 'SafeHandle' only refers to a type, but is being used as a value here.
}
```

2. **TS2863 (extends any):** When TypeRefPrinter falls back to `any` for unmappable types, emitting "extends any" causes TypeScript errors:
```typescript
// BAD - Without fix
class Foo extends any { }
// TS2863: 'any' cannot be used as a base class or interface.
```

### Method: PrintStruct
```csharp
private static string PrintStruct(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, bool instanceSuffix = false)
```
**What it does:**
- Structs emit as classes (metadata notes value semantics)
- Same as `PrintClass` but no `abstract` modifier, no base class

### Method: PrintStaticClass
```csharp
private static string PrintStaticClass(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Emits as `abstract class TypeName { ... }`
- Calls `EmitStaticMembers` to emit static-only members

### Method: PrintEnum
```csharp
private static string PrintEnum(TypeSymbol type, BuildContext ctx)
```
**What it does:**
1. Gets final TypeScript name from Renamer
2. Emits `enum TypeName { ... }`
3. Iterates through const fields (enum members)
4. Gets final member name from Renamer (using `ClassStatic` scope)
5. Emits `Name = Value,` for each member

### Method: PrintDelegate
```csharp
private static string PrintDelegate(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
1. Gets final TypeScript name from Renamer
2. Emits `type TypeName<...> = ...`
3. Finds `Invoke` method
4. Emits function signature: `(a: int, b: string) => void`

### Method: PrintInterface
```csharp
private static string PrintInterface(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
1. Gets final TypeScript name from Renamer
2. Emits `interface TypeName<...> extends ... { ... }`
3. Calls `EmitInterfaceMembers` to emit body

### Method: ApplyInstanceSuffixForSameNamespaceViews
```csharp
private static string ApplyInstanceSuffixForSameNamespaceViews(
    string resolvedName,
    TypeReference typeRef,
    string currentNamespace,
    SymbolGraph graph,
    BuildContext ctx)
```
**What it does:**
- **TS2693 FIX:** Detects same-namespace types with views and returns instance class name (`TypeName$instance`)
- Prevents "only refers to a type" errors when referencing view-emitting types in heritage clauses
- Only applies to named types in the same namespace that emit companion views

**Algorithm:**
1. Check if typeRef is a NamedTypeReference (not primitive, not generic parameter)
2. Look up type symbol in graph via ClrFullName
3. Check if type is in the same namespace as current type
4. Check if type has explicit views (ViewOnly members)
5. If all conditions met, append `$instance` to name (before generic arguments if present)

**Detailed implementation:**
```csharp
// Only applies to named types
if (typeRef is not NamedTypeReference named)
    return resolvedName;

// Look up type symbol
var clrFullName = named.FullName;
if (!graph.TypeIndex.TryGetValue(clrFullName, out var typeSymbol))
    return resolvedName; // External type

// Check if same namespace
if (typeSymbol.Namespace != currentNamespace)
    return resolvedName; // Cross-namespace (qualified names work)

// Check if type has views
if (typeSymbol.ExplicitViews.Length > 0 &&
    (typeSymbol.Kind == TypeKind.Class || typeSymbol.Kind == TypeKind.Struct))
{
    // Insert $instance BEFORE generic arguments
    var genericStart = resolvedName.IndexOf('<');
    if (genericStart >= 0)
    {
        // CORRECT: "Foo$instance<T>"
        // WRONG:   "Foo<T>$instance" (syntax error!)
        return resolvedName.Substring(0, genericStart) + "$instance" + resolvedName.Substring(genericStart);
    }
    else
    {
        // No generic arguments - just append
        return $"{resolvedName}$instance";
    }
}

return resolvedName;
```

**Why this fix is necessary:**

**The Problem (TS2693):**

When types with views are referenced in same-namespace heritage clauses, TypeScript sees:
```typescript
namespace System.Runtime.InteropServices {
    // SafeHandle emits as TWO declarations:
    type SafeHandle = SafeHandle$instance;  // Type alias (not accessible as value)
    class SafeHandle$instance { ... }        // Instance class

    // This FAILS:
    class SafeHandleZeroOrMinusOneIsInvalid extends SafeHandle { }
    // TS2693: 'SafeHandle' only refers to a type, but is being used as a value here.
}
```

**Why it fails:**
- Type aliases (`type Foo = ...`) are NOT accessible as values in heritage clauses
- Inside namespace declarations, only the instance class (`SafeHandle$instance`) is accessible as a value
- Heritage clauses (`extends`, `implements`) require VALUE references, not type references

**The Solution:**

Detect same-namespace references and use instance class name:
```typescript
namespace System.Runtime.InteropServices {
    type SafeHandle = SafeHandle$instance;
    class SafeHandle$instance { ... }

    // This WORKS:
    class SafeHandleZeroOrMinusOneIsInvalid extends SafeHandle$instance { }
    // Instance class is accessible as a value ✓
}
```

**Why cross-namespace works without fix:**

Cross-namespace references use qualified imports at module level:
```typescript
// System.Runtime.InteropServices/internal/index.d.ts
import * as System from '../../System/internal/index.js';

export namespace System.Runtime.InteropServices {
    class Foo extends System.Object { }
    // 'System.Object' resolves to the type alias at MODULE level, where it IS accessible
}
```

**Generic type handling:**

For generic types with views (e.g., `Nullable<T>`):
```typescript
// BAD - $instance after generic args (syntax error)
class Foo extends Nullable<int>$instance { }

// GOOD - $instance before generic args
class Foo extends Nullable$instance<int> { }
```

The fix inserts `$instance` before the `<` character.

**Impact:**
- Commit 5880297: "Fix same-namespace TS2693 by using $instance suffix in heritage clauses"
- Eliminated same-namespace TS2693 errors
- Cross-namespace references unaffected (continue to work)

### Method: IsInterfaceInGraph - TS2304/TS2420 FIX
```csharp
private static bool IsInterfaceInGraph(TypeReference ifaceRef, SymbolGraph graph)
```
**Lines:** 1186-1196

**Purpose:** Filter out non-public interfaces from implements clauses. Only emit interfaces that exist in the SymbolGraph (public types).

**Problem it solves:**
```csharp
// C# code:
internal interface IInternalFoo { }
public interface IPublicBar { }
public class MyClass : IInternalFoo, IPublicBar { }

// WITHOUT IsInterfaceInGraph:
class MyClass implements IInternalFoo, IPublicBar { }
// TS2304: Cannot find name 'IInternalFoo' (it was never emitted!)
// TS2420: Class incorrectly implements interface 'IInternalFoo'

// WITH IsInterfaceInGraph:
class MyClass implements IPublicBar { }
// ✓ Only public interfaces appear in implements clause
```

**Why needed:** Internal/private interfaces are not emitted to .d.ts files (only public types are emitted). If they appear in implements clauses, TypeScript can't find them and generates errors. This causes massive cascading errors because derived classes also fail to implement the phantom interface.

**Algorithm:**
1. **Check if NamedTypeReference:**
   - If not (e.g., generic parameter): return `true` (always allowed)

2. **Build StableId:**
   - Format: `"{assemblyName}:{fullName}"`
   - Example: `"System.Private.CoreLib:System.Collections.Generic.IEnumerable\`1"`

3. **Look up in TypeIndex:**
   - Use `graph.TypeIndex.TryGetValue(stableId, out _)`
   - If found: interface is public (in graph) → return `true`
   - If not found: interface is non-public or external → return `false`

**Example:**
```csharp
// Input:
ifaceRef = IComparable<T> (from mscorlib)

// Processing:
stableId = "System.Private.CoreLib:System.IComparable`1"
graph.TypeIndex.TryGetValue(stableId, ...) → true (public interface)

// Output:
true (emit this interface)
```

**Impact:**
- **Eliminated ALL TS2420 errors** (579 errors → 0)
- **Reduced TS2416 by 81.6%** (794 errors → 146)
- Total error reduction: -1,264 errors (-86.5%)

**Why such massive impact:** When a class implements a non-public interface:
1. TypeScript can't find the interface (TS2304)
2. TypeScript says class doesn't correctly implement interface (TS2420)
3. All methods/properties from that interface mismatch (TS2416 cascade)
4. Derived classes inherit the problem (exponential growth)

By filtering non-public interfaces, we prevent the root cause and eliminate the entire error cascade.

**Called by:**
- `PrintClass` at line 116 when filtering interfaces
- `PrintStruct` at line 166 when filtering interfaces

**Addition:** This method was added in as the single most impactful fix for TypeScript validation errors. It represents a fundamental insight: TypeScript can only check against types that were actually emitted.

### Method: EmitMembers
```csharp
private static void EmitMembers(StringBuilder sb, TypeSymbol type, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Emits instance members:
  - Constructors (non-static)
  - Fields (non-static, ClassSurface only)
  - Properties (non-static, ClassSurface only)
  - Methods (non-static, ClassSurface only)
- Calls `EmitStaticMembers` for static members
- Uses `ScopeFactory.ClassInstance(type)` for member name resolution

### Method: EmitStaticMembers
```csharp
private static void EmitStaticMembers(StringBuilder sb, TypeSymbol type, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Emits static members with special handling for generic classes:
  - **Fields** (static, non-const, ClassSurface or StaticSurface):
    - **NEW**: Calls `SubstituteClassGenericsInTypeRef` to widen types referencing class generics to `unknown`
    - Prevents TS2302 error (static members cannot reference class type parameters)
  - **Const fields** (as `static readonly`, ClassSurface or StaticSurface):
    - **NEW**: Also substitutes class generics with `unknown`
  - **Properties** (static, ClassSurface or StaticSurface):
    - **NEW**: Substitutes class generics with `unknown`
  - **Methods** (static, ClassSurface or StaticSurface):
    - **NEW**: Calls `LiftClassGenericsToMethod` to move class generic parameters to method level
    - Prevents TS2302 error (TypeScript doesn't support static methods using class type parameters)
- Uses `ScopeFactory.ClassStatic(type)` for member name resolution

**Why generic handling is needed:**

TypeScript does NOT allow static members to reference class-level type parameters:

```typescript
// ❌ INVALID TypeScript (TS2302 error)
class List<T> {
    static defaultValue: T;           // ERROR: Static member cannot reference class type parameter
    static createDefault: T { ... } // ERROR: Static member cannot reference class type parameter
}
```

**Solution:**
- **Static fields/properties**: Widen type to `unknown` (TypeScript limitation - cannot be made generic)
- **Static methods**: Lift class generics to method generics (makes method generic instead)

### Method: EmitInterfaceMembers
```csharp
private static void EmitInterfaceMembers(StringBuilder sb, TypeSymbol type, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Emits interface members:
  - Properties (non-static, ClassSurface only) - static not supported in TS interfaces
  - Methods (non-static, ClassSurface only) - static not supported in TS interfaces
- Uses `ScopeFactory.ClassSurface` for member name resolution

### Method: PrintGenericParameter
```csharp
private static string PrintGenericParameter(GenericParameterSymbol gp, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Emits generic parameter with constraints
- Single constraint: `T extends IFoo`
- Multiple constraints: `T extends IFoo & IBar` (intersection)

### Method: LiftClassGenericsToMethod

```csharp
private static MethodSymbol LiftClassGenericsToMethod(MethodSymbol method, TypeSymbol declaringType, BuildContext ctx)
```

**Purpose:** Lifts class-level generic parameters to method-level for static methods. Solves TS2302 error (static members cannot reference class type parameters).

**Problem it solves:**

```csharp
// C# code (valid):
class Array<T> {
    public static void Sort<TKey>(T[] keys, TKey[] items) { ... }
}

// TypeScript (INVALID - TS2302):
class Array_1<T> {
    static sort<TKey>(keys: T[], items: TKey[]): void;  // ❌ ERROR: Cannot reference 'T' in static method
}

// TypeScript (VALID after lifting):
class Array_1<T> {
    static sort<T, TKey>(keys: T[], items: TKey[]): void;  // ✅ OK: 'T' is now a method generic
}
```

**Algorithm:**

1. **Early exit if no class generics:**
   ```csharp
   if (declaringType.GenericParameters.Length == 0)
       return method;  // Nothing to lift
   ```

2. **Collect class generic parameters:**
   ```csharp
   var classGenerics = declaringType.GenericParameters.ToList;
   ```

3. **Check for name collisions with existing method generics:**
   ```csharp
   var existingMethodGenericNames = new HashSet<string>(
       method.GenericParameters.Select(gp => gp.Name)
   );
   ```

4. **Rename colliding class generics:**
   - For each class generic parameter:
     - If name collides with method generic: `T` → `T1`, `T2`, etc.
     - Build substitution map: `oldName → GenericParameterReference(newName)`
     - Example: Class has `T`, method has `T` → lift as `T1` to avoid collision

5. **Combine generics:**
   ```csharp
   var combinedGenerics = liftedGenerics
       .Concat(method.GenericParameters)
       .ToImmutableArray;
   ```
   - **Order**: Lifted class generics FIRST, then method generics
   - Ensures class generic positions match class declaration order

6. **Substitute renamed generics in signatures:**
   - If any generics were renamed (substitution map not empty):
     - Substitute in return type: `SubstituteTypeReference(method.ReturnType, substitutionMap)`
     - Substitute in parameters: For each param, substitute param type
   - Uses `Load.InterfaceMemberSubstitution.SubstituteTypeReference`

7. **Return transformed method:**
   ```csharp
   return method with {
       GenericParameters = combinedGenerics,
       ReturnType = newReturnType,
       Parameters = newParameters
   };
   ```

**Examples:**

**Example 1: Simple lifting (no collision)**
```csharp
// Input: class Array<T> { static void Sort(T[] array) }
// Class generics: [T]
// Method generics: []
// Output: static sort<T>(array: T[]): void
```

**Example 2: Collision renaming**
```csharp
// Input: class Comparer<T> { static int Compare<T>(T x, T y) }
// Class generics: [T]           (outer T)
// Method generics: [T]          (inner T, shadows outer)
// Collision: Both named 'T'
// Output: static compare<T1, T>(x: T1, y: T): int
//         (class T lifted as T1, method T keeps name T)
```

**Example 3: Multiple class generics**
```csharp
// Input: class Dictionary<TKey, TValue> { static Dictionary<TKey, TValue> Create }
// Class generics: [TKey, TValue]
// Method generics: []
// Output: static create<TKey, TValue>: Dictionary_2<TKey, TValue>
```

**Logging:**
```csharp
ctx.Log("GenericLift", $"Lifted {liftedGenerics.Count} class generics into method {type.ClrName}.{method.ClrName}");
```

### Method: SubstituteClassGenericsInTypeRef

```csharp
private static TypeReference SubstituteClassGenericsInTypeRef(
    TypeReference typeRef,
    ImmutableArray<GenericParameterSymbol> classGenerics)
```

**Purpose:** Substitutes class-level generic parameter references with `unknown` type. Used for static fields/properties that cannot be made generic in TypeScript.

**Problem it solves:**

```typescript
// TypeScript does NOT support this:
class List<T> {
    static empty: T;        // ❌ TS2302: Static member cannot reference class type parameter 'T'
    static default: T[];    // ❌ TS2302: Cannot reference 'T'
}

// TypeScript DOES support this (widened to unknown):
class List_1<T> {
    static empty: unknown;        // ✅ OK: Widened type
    static default: unknown[];    // ✅ OK: Array of unknown (still TS2302, but safer)
}
```

**Algorithm:**

1. **Early exit if no class generics:**
   ```csharp
   if (classGenerics.Length == 0)
       return typeRef;  // Nothing to substitute
   ```

2. **Build class generic name set:**
   ```csharp
   var classGenericNames = new HashSet<string>(classGenerics.Select(gp => gp.Name));
   ```

3. **Check if type references any class generic:**
   ```csharp
   if (ReferencesClassGeneric(typeRef, classGenericNames))
   {
       // Widen to 'unknown'
       return new NamedTypeReference {
           AssemblyName = "TypeScript",
           Namespace = "",
           Name = "unknown",
           FullName = "unknown",
           Arity = 0,
           TypeArguments = ImmutableArray<TypeReference>.Empty,
           IsValueType = false
       };
   }
   ```

4. **Return original if no class generics referenced:**
   ```csharp
   return typeRef;
   ```

**Examples:**

**Example 1: Simple substitution**
```csharp
// Field type: T
// Class generics: [T]
// Result: unknown
```

**Example 2: Array substitution**
```csharp
// Field type: T[]
// Class generics: [T]
// Result: unknown  (entire type widened, not unknown[])
```

**Example 3: Complex type substitution**
```csharp
// Field type: Dictionary<TKey, TValue>
// Class generics: [TKey, TValue]
// Result: unknown  (entire type widened)
```

**Example 4: No substitution (doesn't reference class generics)**
```csharp
// Field type: string
// Class generics: [T]
// Result: string  (unchanged, doesn't reference T)
```

**Why widen entire type:**
- TypeScript cannot express "array of some class generic"
- Widening to `unknown[]` would still cause TS2302
- Safest approach: widen entire type to `unknown`
- Alternative: Could use `any`, but `unknown` is safer (forces type checking at use site)

### Method: ReferencesClassGeneric

```csharp
private static bool ReferencesClassGeneric(TypeReference typeRef, HashSet<string> classGenericNames)
```

**Purpose:** Recursively checks if a TypeReference contains any class-level generic parameters.

**Algorithm by TypeReference kind:**

```csharp
GenericParameterReference gp =>
    classGenericNames.Contains(gp.Name)
    // Direct match: T references class generic T

ArrayTypeReference arr =>
    ReferencesClassGeneric(arr.ElementType, classGenericNames)
    // Recurse: T[] contains T

PointerTypeReference ptr =>
    ReferencesClassGeneric(ptr.PointeeType, classGenericNames)
    // Recurse: T* contains T

ByRefTypeReference byref =>
    ReferencesClassGeneric(byref.ReferencedType, classGenericNames)
    // Recurse: ref T contains T

NamedTypeReference named =>
    named.TypeArguments.Any(arg => ReferencesClassGeneric(arg, classGenericNames))
    // Recurse: List<T> contains T, Dictionary<string, T> contains T

NestedTypeReference nested =>
    nested.FullReference.TypeArguments.Any(arg => ReferencesClassGeneric(arg, classGenericNames))
    // Recurse: Outer<T>.Inner contains T

_ => false
    // Unknown types: assume no class generics
```

**Examples:**

| Type Reference | Class Generics | Result | Reason |
|---|---|---|---|
| `T` | `{T}` | `true` | Direct match |
| `string` | `{T}` | `false` | Doesn't reference T |
| `T[]` | `{T}` | `true` | Element type is T |
| `List<T>` | `{T}` | `true` | Type arg is T |
| `List<string>` | `{T}` | `false` | Type arg is string, not T |
| `Dictionary<TKey, string>` | `{TKey, TValue}` | `true` | First type arg is TKey |
| `Dictionary<string, int>` | `{TKey, TValue}` | `false` | No type args reference class generics |
| `Nullable<T>` | `{T}` | `true` | Type arg is T |
| `ref T` | `{T}` | `true` | Referenced type is T |

**Why recursive:**
- Generic type arguments can nest: `List<Dictionary<TKey, TValue>>`
- Arrays can be generic: `T[]`
- Pointers/refs wrap types: `ref T`, `T*`
- Must check all levels to find class generic references

---

## FIX D: Interface and Base Class Generic Parameter Substitution

**Purpose:** Substitutes generic parameters when members come from interfaces or base classes with different generic parameter names. Prevents "orphaned generic parameter" errors where class surface methods reference type parameters not declared in the class.

**Problem solved:**
```csharp
// C# interface with generic T
interface IEnumerable<T> {
    T First;
}

// Class implements with concrete type string
class MyCollection : IEnumerable<string> {
    // WITHOUT FIX D: Member still references "T" (orphaned - not declared in MyCollection)
    T First;  // ❌ ERROR: T is not defined

    // WITH FIX D: T is substituted with string
    string First;  // ✅ CORRECT
}
```

**Files**: ClassPrinter.cs (15 methods), InterfaceInliner.cs (5 methods)
**Lines**: ~450 lines total
**Impact**: Eliminates orphaned generic parameter references in emitted TypeScript

### Entry Points (2 methods)

#### `SubstituteMemberIfNeeded(TypeSymbol type, MethodSymbol method, BuildContext ctx, SymbolGraph graph)`

**Purpose**: Determines if method needs generic parameter substitution and dispatches to appropriate handler.

**Algorithm:**
1. **Check Source Interface**: If `method.SourceInterface != null` → call `SubstituteInterfaceMethod`
2. **Check Orphaned Generics**: If method references generics not in type → call `SubstituteBaseClassMethod`
3. **Return original**: No substitution needed

**Example:**
```csharp
// Method from IEnumerable<string> interface
method.SourceInterface = IEnumerable`1
→ SubstituteInterfaceMethod called
→ T substituted with string
```

#### `SubstituteMemberIfNeeded(TypeSymbol type, PropertySymbol prop, BuildContext ctx, SymbolGraph graph)`

**Purpose**: Property equivalent of method substitution.

**Algorithm:**
1. **Check Source Interface**: If `prop.SourceInterface != null` → call `SubstituteInterfaceProperty`
2. **Check Orphaned Generics**: If property type references generics not in type → call `SubstituteBaseClassProperty`
3. **Return original**: No substitution needed

### Interface Substitution (2 methods)

#### `SubstituteInterfaceMethod(TypeSymbol type, MethodSymbol method, BuildContext ctx, SymbolGraph graph)`

**Purpose**: Substitutes generic parameters for methods from interfaces.

**Algorithm:**
1. **Match interface**: Call `FindMatchingInterfaceForMember(type, method.SourceInterface)`
   - Matches `IEnumerable\`1` (open) to `IEnumerable<string>` (constructed)
2. **Find interface symbol**: Get interface TypeSymbol from graph to access generic parameter names
3. **Build substitution map**: Call `BuildSubstitutionMapForClass(matchedInterface, ifaceSymbol)`
   - Creates mapping: `{"T" → string}`
4. **Filter method generics**: Exclude method-level generic parameters from substitution
5. **Substitute types**: Apply to return type and all parameters
6. **Return transformed method**

**Example:**
```csharp
// Input: T First from IEnumerable<string>
// matchedInterface: IEnumerable<string>
// substitutionMap: {"T" → string}
// Output: string First
```

#### `SubstituteInterfaceProperty(TypeSymbol type, PropertySymbol prop, BuildContext ctx, SymbolGraph graph)`

**Purpose**: Property equivalent of interface method substitution.

**Algorithm**: Similar to `SubstituteInterfaceMethod` but only substitutes property type (no parameters).

### Base Class Substitution (2 methods)

#### `SubstituteBaseClassMethod(TypeSymbol type, MethodSymbol method, BuildContext ctx, SymbolGraph graph)`

**Purpose**: Substitutes generic parameters for methods inherited from base classes.

**Algorithm:**
1. **Find base class symbol**: Get base TypeSymbol from graph
2. **Build substitution map**: Map base class generic params → actual type args
3. **Substitute types**: Apply to return type and parameters
4. **Return transformed method**

**Example:**
```csharp
// Base class: List<T> with method T GetItem(int index)
// Derived class: MyList : List<string>
// Substitution map: {"T" → string}
// Output: string GetItem(int index)
```

#### `SubstituteBaseClassProperty(TypeSymbol type, PropertySymbol prop, BuildContext ctx, SymbolGraph graph)`

**Purpose**: Property equivalent of base class method substitution.

### Orphaned Generic Detection (3 methods)

#### `HasOrphanedGenericParameters(TypeSymbol type, MethodSymbol method)`

**Purpose**: Checks if method references generic parameters not declared in the type.

**Algorithm:**
1. Build set of type's generic parameter names
2. Build set of method's generic parameter names (method-level generics)
3. Call `ContainsOrphanedGenericParameter` on return type and all parameters
4. Return true if any orphaned generics found

**Example:**
```csharp
// Type MyList (no generics)
// Method: T GetItem(int index) where T from base class
// → HasOrphanedGenericParameters returns true (T is orphaned)
```

#### `HasOrphanedGenericParametersInProperty(TypeSymbol type, PropertySymbol prop)`

**Purpose**: Property equivalent of orphaned generic detection.

#### `ContainsOrphanedGenericParameter(TypeReference typeRef, HashSet<string> typeGenericParams, HashSet<string> methodGenericParams)`

**Purpose**: Recursively checks if TypeReference contains generic parameters not in type or method.

**Algorithm by TypeReference kind:**
- **GenericParameterReference**: Check if name is in typeGenericParams or methodGenericParams
- **NamedTypeReference**: Recurse into type arguments
- **NestedTypeReference**: Recurse into full reference type arguments
- **ArrayTypeReference**: Recurse into element type
- **PointerTypeReference/ByRefTypeReference**: Recurse into pointee/referenced type

### Interface Matching (2 methods)

#### `FindMatchingInterfaceForMember(TypeSymbol type, TypeReference sourceInterface)`

**Purpose**: Matches an open generic interface reference to the class's actual (constructed) interface implementation.

**Algorithm:**
1. Get interface base name: `IEnumerable` from `IEnumerable\`1`
2. Search type's interface list for matching base name
3. Return first match (constructed interface with actual type arguments)

**Example:**
```csharp
// Class implements: IEnumerable<string>, ICollection<string>
// SourceInterface: IEnumerable`1 (open)
// → Finds and returns: IEnumerable<string> (constructed)
```

#### `GetInterfaceBaseName(TypeReference typeRef)`

**Purpose**: Extracts base name without generic arity.

**Examples:**
- `IEnumerable\`1` → `IEnumerable`
- `Dictionary\`2` → `Dictionary`

### Substitution Map Building (1 method)

#### `BuildSubstitutionMapForClass(TypeReference actualInterface, TypeSymbol interfaceSymbol)`

**Purpose**: Builds mapping from interface generic parameter names to actual type arguments.

**Algorithm:**
1. Get actual type arguments from constructed interface: `IEnumerable<string>` → `[string]`
2. Get generic parameter names from interface symbol: `IEnumerable\`1<T>` → `["T"]`
3. Zip together: `{"T" → string}`

**Example:**
```csharp
// actualInterface: Dictionary<string, int>
// interfaceSymbol: IDictionary`2<TKey, TValue>
// Result: {"TKey" → string, "TValue" → int}
```

### Symbol Lookup (3 methods)

#### `FindInterfaceSymbol(SymbolGraph graph, TypeReference interfaceRef)`

**Purpose**: Finds interface TypeSymbol in graph by CLR full name.

**Algorithm:**
1. Get CLR full name from reference
2. Search all namespaces for matching interface
3. Return TypeSymbol (null if not found)

**Used for**: Getting interface's generic parameter names for substitution map building.

#### `FindTypeSymbol(SymbolGraph graph, TypeReference typeRef)`

**Purpose**: Finds any TypeSymbol in graph by CLR full name.

**Used for**: Finding base class symbols for base class substitution.

#### `GetTypeFullName(TypeReference typeRef)`

**Purpose**: Helper to extract CLR full name from TypeReference.

**Handles**: NamedTypeReference, NestedTypeReference

### Signature Changes

**PrintClass(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, SymbolGraph graph, bool instanceSuffix = false)**
- **Added parameter**: `SymbolGraph graph` (needed for FIX D lookups)

**PrintStruct(TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, SymbolGraph graph, bool instanceSuffix = false)**
- **Added parameter**: `SymbolGraph graph`

**EmitMembers(StringBuilder sb, TypeSymbol type, TypeNameResolver resolver, BuildContext ctx, SymbolGraph graph)**
- **Added parameter**: `SymbolGraph graph`
- **Calls**: `SubstituteMemberIfNeeded` for all methods and properties before emitting

### Integration

**Called from**: `EmitMembers` before emitting each method/property

```csharp
// In EmitMembers:
foreach (var method in members.Methods) {
    var methodToEmit = SubstituteMemberIfNeeded(type, method, ctx, graph); // FIX D
    sb.Append(MethodPrinter.Print(methodToEmit, type, resolver, ctx));
}

foreach (var prop in members.Properties) {
    var propToEmit = SubstituteMemberIfNeeded(type, prop, ctx, graph); // FIX D
    // ... emit property
}
```

---

## File: Printers/MethodPrinter.cs

### Purpose
Prints TypeScript method signatures from MethodSymbol. Handles generic methods, parameters, return types, and modifiers.

### Method: Print
```csharp
public static string Print(MethodSymbol method, TypeSymbol declaringType, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
1. Gets final TS name from Renamer using `ClassSurface` scope
2. Emits modifiers (`static`, `abstract`) - SKIPPED for interface members
3. Emits method name
4. Emits generic parameters with constraints
5. Emits parameters
6. Emits return type

**Key Decision:**
- Interface members don't get `static`/`abstract` modifiers (TS doesn't support static interface members)

### Method: PrintGenericParameter
```csharp
private static string PrintGenericParameter(GenericParameterSymbol gp, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Prints generic parameter with constraints
- Single constraint: `T extends IFoo`
- Multiple constraints: `T extends IFoo & IBar`

### Method: PrintParameter
```csharp
private static string PrintParameter(ParameterSymbol param, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Prints parameter name
- Adds `?` for optional parameters (has default value)
- Handles ref/out parameters: `{ value: T }` wrapper (metadata tracks semantics)
- Handles params parameters: caller adds `...` prefix

### Method: PrintWithParamsExpansion
```csharp
public static string PrintWithParamsExpansion(MethodSymbol method, TypeSymbol declaringType, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Converts params array to rest parameter syntax
- Example: `Add(items: T[])` → `Add(...items: T[])`
- Only applies to last parameter if `IsParams` is true

### Method: PrintOverloads
```csharp
public static IEnumerable<string> PrintOverloads(IEnumerable<MethodSymbol> overloads, TypeSymbol declaringType, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Prints multiple method overloads (same name, different signatures)
- Yields one string per overload

### Method: PrintAsPropertyAccessor
```csharp
public static string PrintAsPropertyAccessor(MethodSymbol method, bool isGetter, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Prints method as property getter/setter
- Extracts property name from method name (`get_Foo` → `Foo`)
- Used for interface property accessors

---

## File: Printers/TypeRefPrinter.cs

### Purpose
Prints TypeScript type references from TypeReference model. Handles all type constructs: named, generic parameters, arrays, pointers, byrefs, nested. **CRITICAL:** Uses TypeNameResolver to ensure printed names match imports (single source of truth).

### Method: Print - TS2304 FIX
```csharp
public static string Print(
    TypeReference typeRef,
    TypeNameResolver resolver,
    BuildContext ctx,
    HashSet<string>? allowedTypeParameterNames = null)
```
**What it does:**
- Dispatches to specialized printer based on type reference kind:
  - `PlaceholderTypeReference` → `PrintPlaceholder` (emits warning, returns `any`)
  - `NamedTypeReference` → `PrintNamed`
  - `GenericParameterReference` → `PrintGenericParameter`
  - `ArrayTypeReference` → `PrintArray`
  - `PointerTypeReference` → `PrintPointer`
  - `ByRefTypeReference` → `PrintByRef`
  - `NestedTypeReference` → `PrintNested`

**Addition:** Added `allowedTypeParameterNames` parameter for free type variable detection. When provided, any generic parameter NOT in this set is demoted to `unknown` to prevent TS2304 "Cannot find name" errors from leaked generics.

### Method: PrintPlaceholder
```csharp
private static string PrintPlaceholder(PlaceholderTypeReference placeholder, BuildContext ctx)
```
**What it does:**
- Emits diagnostic warning (placeholders should never reach output)
- Returns `"any"` as fallback

### Method: PrintNamed
```csharp
private static string PrintNamed(NamedTypeReference named, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
1. Tries to map CLR primitive via `TypeNameResolver.TryMapPrimitive` (short-circuit)
2. Gets final TypeScript name from `resolver.ResolveTypeName(named)`
3. Validates non-empty name (emits warning if empty)
4. Handles generic type arguments recursively
5. Returns `TypeName<T, U>` or plain `TypeName`

**Key Algorithm:**
```csharp
// 1. Map primitive (System.Int32 → int)
var primitiveType = TypeNameResolver.TryMapPrimitive(named.FullName);
if (primitiveType != null) return primitiveType;

// 1.5. Handle TypeScript built-in synthesized types
if (named.FullName == "unknown") return "unknown";

// 2. Get final name from resolver (handles both graph types and external types)
var baseName = resolver.ResolveTypeName(named);

// 3. HARDENING: Validate non-empty
if (string.IsNullOrWhiteSpace(baseName))
{
    ctx.Diagnostics.Warning(...);
    return "unknown";
}

// 4. Handle generic arguments recursively
if (named.TypeArguments.Count > 0)
{
    var args = string.Join(", ", named.TypeArguments.Select(arg => Print(arg, resolver, ctx)));
    return $"{baseName}<{args}>";
}

return baseName;
```

### Method: PrintGenericParameter - TS2304 FIX
```csharp
private static string PrintGenericParameter(
    GenericParameterReference gp,
    BuildContext ctx,
    HashSet<string>? allowedTypeParameterNames)
```
**What it does:**
- **:** Checks if generic parameter is allowed in current scope
- If `allowedTypeParameterNames` is provided and parameter is NOT in the set, demotes to `unknown` (free type variable)
- Otherwise returns generic parameter name as-is: `T`, `U`, `TKey`, `TValue`

**Problem it solves:**
```csharp
// C# code:
class MyClass<T> {
    // Implements IEnumerable<int> (explicit interface implementation)
    // But C# reflection shows generic parameter T in the interface member signatures
}

// WITHOUT free variable detection:
class MyClass_1<T> {
    // T leaks into signature even though it's not used!
    GetEnumerator: IEnumerator_1<T>  // TS2304: 'T' is a free type variable (not defined on class)
}

// WITH free variable detection:
class MyClass_1<T> {
    GetEnumerator: IEnumerator_1<unknown>  // Safe - no leaked generics
}
```

**Algorithm:**
1. **Check if filtering is enabled:**
   - If `allowedTypeParameterNames` is `null`: return `gp.Name` (no filtering)

2. **Check if parameter is allowed:**
   - If `gp.Name` is in `allowedTypeParameterNames`: return `gp.Name` (allowed)

3. **Demote free variable:**
   - Log demotion: `"Demoting unbound generic parameter '{gp.Name}' to 'unknown'"`
   - Return `"unknown"` (prevents TS2304)

**How allowedTypeParameterNames is built:**
- Class-level generic parameters: `T`, `U` from `class Foo<T, U>`
- Method-level generic parameters: `TKey` from `void Sort<TKey>`
- Combined set passed down through type reference printing

**Impact:**
- Prevented generic T leaks in interface implementations
- Contributed to eliminating structural TS2304 errors
- Ensures type parameters are only used when they're actually in scope

**Called by:** All type printing methods that recursively call `Print` with the allowed set propagated

### Method: PrintArray
```csharp
private static string PrintArray(ArrayTypeReference arr, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Single-dimensional: `T[]`
- Multi-dimensional: `Array<Array<T>>` (nested)

### Method: PrintPointer
```csharp
private static string PrintPointer(PointerTypeReference ptr, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Returns `TSUnsafePointer<T>` where T is the pointee type
- Preserves type information while being type-safe (erases to `unknown`)

### Method: PrintByRef
```csharp
private static string PrintByRef(ByRefTypeReference byref, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Returns `TSByRef<T>` where T is the referenced type
- Provides structural access via `.value` property

### Method: PrintNested
```csharp
private static string PrintNested(NestedTypeReference nested, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Nested types use resolver just like named types
- The `FullReference` is a `NamedTypeReference` that the resolver handles correctly

### Helper Methods

#### PrintList
```csharp
public static string PrintList(IEnumerable<TypeReference> typeRefs, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Prints comma-separated list of type references
- Used for generic parameter lists, method parameters

#### PrintNullable
```csharp
public static string PrintNullable(TypeReference typeRef, bool isNullable, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Prints type with optional nullability: `T | null`

#### PrintReadonlyArray
```csharp
public static string PrintReadonlyArray(TypeReference elementType, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Prints `ReadonlyArray<T>` for IEnumerable<T> mappings

#### PrintPromise
```csharp
public static string PrintPromise(TypeReference resultType, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Prints `Promise<T>` for Task<T> mappings

#### PrintTuple
```csharp
public static string PrintTuple(IReadOnlyList<TypeReference> elementTypes, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Prints tuple type: `[T1, T2, T3]`

#### PrintUnion
```csharp
public static string PrintUnion(IReadOnlyList<TypeReference> types, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Prints union type: `T1 | T2 | T3`

#### PrintIntersection
```csharp
public static string PrintIntersection(IReadOnlyList<TypeReference> types, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Prints intersection type: `T1 & T2 & T3`

#### PrintTypeof
```csharp
public static string PrintTypeof(TypeReference typeRef, TypeNameResolver resolver, BuildContext ctx)
```
**What it does:**
- Prints typeof expression: `typeof ClassName`

---

## File: PrimitiveLift.cs (- NEW)

### Purpose
Defines the primitive lifting rules for the `CLROf<T>` utility type. This is the single source of truth for which TypeScript primitives get lifted to their CLR types in generic contexts.

**Contract (PG_GENERIC_PRIM_LIFT_001):**
- Every primitive type used as a generic type argument is covered by these rules
- CLROf emitter (in InternalIndexEmitter) uses these rules to generate the conditional type mapping
- TypeRefPrinter uses these rules to determine which concrete types to wrap with CLROf
- PhaseGate validator ensures all primitive type arguments are covered

### Why Needed

**Problem:**
```typescript
// C# code:
IEquatable<int> eq = ...;

// TypeScript challenge:
// - int is branded: type int = number & { __brand: "int" }
// - IEquatable<T> expects: T extends IEquatable<T>
// - But 'int' doesn't implement IEquatable<int>!
```

**Solution:**
```typescript
// Lift int → Int32 in generic positions:
IEquatable_1<CLROf<int>>  // CLROf<int> = Int32, which implements IEquatable<Int32>
```

### Rules Array

```csharp
internal static readonly (string TsName, string ClrFullName, string ClrSimpleName)[] Rules
```

**What it contains:**
- All TypeScript primitive names and their CLR equivalents
- Order matters for CLROf conditional type (more specific types first)

**Coverage:**
- Signed integers: `sbyte`, `short`, `int`, `long`, `int128`, `nint`
- Unsigned integers: `byte`, `ushort`, `uint`, `ulong`, `uint128`, `nuint`
- Floating point: `half`, `float`, `double`, `decimal`
- Other: `char`, `boolean`, `string`

**Example entries:**
```csharp
("int", "System.Int32", "Int32"),
("string", "System.String", "String"),   // Note: imported as ClrString (conflicts with TS String)
("boolean", "System.Boolean", "Boolean"), // Note: imported as ClrBoolean (reserved keyword)
```

**Built-in type aliasing:**
For types that conflict with TypeScript built-ins (`String`, `Boolean`, `Object`), these are imported with aliases in cross-namespace imports:
```typescript
import type { String as ClrString, Boolean as ClrBoolean, Object as ClrObject, ... } from '../System/internal/index.js';
```

### Methods

#### IsLiftableClr
```csharp
internal static bool IsLiftableClr(string clrFullName)
```
**What it does:**
- Checks if a CLR type (by full name) is a liftable primitive
- Used by PhaseGate validator to detect primitive type arguments
- Example: `IsLiftableClr("System.Int32")` → `true`

#### IsLiftableTs
```csharp
internal static bool IsLiftableTs(string tsName)
```
**What it does:**
- Checks if a TypeScript type name is a liftable primitive
- Used by TypeRefPrinter to determine which concrete types to wrap with CLROf
- Example: `IsLiftableTs("int")` → `true`

#### GetClrSimpleName
```csharp
internal static string? GetClrSimpleName(string tsName)
```
**What it does:**
- Gets the CLR simple name (for emission) for a given TS primitive
- Returns `null` if not a liftable primitive
- Example: `GetClrSimpleName("int")` → `"Int32"`

### Usage in Pipeline

1. **InternalIndexEmitter** (CLROf emission):
   ```csharp
   foreach (var (tsName, _, clrSimpleName) in PrimitiveLift.Rules)
   {
       sb.AppendLine($"    T extends {tsName} ? import(\"../../System/internal/index.js\").{clrSimpleName} :");
   }
   ```

2. **TypeRefPrinter** (wrapping type arguments):
   ```csharp
   var printed = Print(arg, resolver, ctx, allowedTypeParameterNames);
   var isPrimitive = PrimitiveLift.IsLiftableTs(printed);
   return isPrimitive ? $"CLROf<{printed}>" : printed;
   ```

3. **PhaseGate** (validation):
   ```csharp
   if (PrimitiveLift.IsLiftableClr(clrFullName))
   {
       // Report PG_GENERIC_PRIM_LIFT_001 if not wrapped
   }
   ```

### Impact

- Enabled primitives in generic positions without constraint violations
- Eliminated generic constraint errors for primitive types
- Provides ergonomic TypeScript types (lowercase primitives) while maintaining CLR semantics in generics

---

## File: AliasEmit.cs (- NEW)

### Purpose
Unified type alias emission logic. Ensures consistent generic parameter handling across all alias emission sites to prevent TS2315 "Type is not generic" errors.

**Used by:**
- FacadeEmitter: Public facade re-exports
- InternalIndexEmitter: Convenience exports, view composition aliases
- Any code that emits type aliases with generics

**Why needed:** Previously, alias emission was scattered across multiple files with inconsistent generic handling, causing LHS/RHS arity mismatches.

### Method: EmitGenericAlias

```csharp
internal static void EmitGenericAlias(
    StringBuilder sb,
    string aliasName,
    TypeSymbol sourceType,
    string rhsExpression,
    TypeNameResolver resolver,
    BuildContext ctx,
    bool withConstraints = false)
```

**What it does:**
- Emits a type alias with proper generic parameter handling
- Guarantees LHS and RHS have matching arity and parameter names
- Prevents TS2315 errors by ensuring consistency

**Algorithm:**

1. **Non-generic case (no type parameters):**
   ```typescript
   export type Foo = Internal.Foo;
   ```

2. **Generic without constraints (simple re-exports):**
   ```typescript
   export type List_1<T> = Internal.System.Collections.Generic.List_1<T>;
   ```

3. **Generic with constraints (internal convenience exports):**
   ```typescript
   export type List_1<T extends IEquatable_1<CLROf<T>>> = Internal.System.Collections.Generic.List_1<T>;
   ```

**Parameters:**
- `aliasName`: LHS alias name (e.g., `"Foo"`)
- `sourceType`: Source TypeSymbol (determines arity and constraints)
- `rhsExpression`: RHS expression base (e.g., `"Internal.Foo"`)
- `withConstraints`: Whether to include constraints on LHS

**Example usage:**
```csharp
// Facade export (no constraints):
AliasEmit.EmitGenericAlias(
    sb,
    aliasName: "List_1",
    sourceType: listType,
    rhsExpression: "Internal.System.Collections.Generic.List_1",
    resolver,
    ctx,
    withConstraints: false);

// Internal convenience export (with constraints):
AliasEmit.EmitGenericAlias(
    sb,
    aliasName: "List_1",
    sourceType: listType,
    rhsExpression: "System.Collections.Generic.List_1",
    resolver,
    ctx,
    withConstraints: true);
```

### Method: GenerateTypeParametersWithConstraints

```csharp
internal static string GenerateTypeParametersWithConstraints(
    TypeSymbol sourceType,
    TypeNameResolver resolver,
    BuildContext ctx)
```

**What it does:**
- Generates generic type parameters WITH constraints for LHS of alias
- Example: `"<T extends IFoo, U extends IBar>"`
- Filters out C# special constraints (`struct`, `class`, `new`)

**Algorithm:**
1. For each generic parameter:
   - If no constraints: emit `"T"`
   - If constraints: emit `"T extends IFoo & IBar"` (intersection)
2. Filter special constraints (`System.ValueType`, `System.Object`)
3. Use TypeRefPrinter to print constraint types

### Method: GenerateTypeArguments

```csharp
internal static string GenerateTypeArguments(TypeSymbol sourceType)
```

**What it does:**
- Generates generic type arguments for RHS of alias
- Example: `"<T, U>"` (parameter names only, no constraints)

**Why separate from GenerateTypeParametersWithConstraints:**
- LHS needs constraints: `type Foo<T extends IBar>`
- RHS just needs names: `= Internal.Foo<T>`

### Impact

- **Eliminated TS2315 errors** from arity mismatches
- **Unified alias emission** across facade and internal exports
- **Prevented future bugs** by centralizing generic handling logic

**Before AliasEmit:**
```typescript
// Facade (inconsistent):
export type Foo<T> = Internal.Foo;  // TS2315: Type 'Foo' is not generic

// Internal (inconsistent):
export type Bar = Namespace.Bar<T>;  // TS2304: Cannot find name 'T'
```

**After AliasEmit:**
```typescript
// Facade (consistent):
export type Foo<T> = Internal.Foo<T>;  // ✓

// Internal (consistent):
export type Bar<T> = Namespace.Bar<T>;  // ✓
```

---

## File: ExtensionsEmitter.cs

**Purpose**: Emits C# extension methods as TypeScript bucket interfaces.

### What it emits

Extension methods are emitted to `internal/extensions/index.d.ts` with:
1. **Bucket interfaces** - One per target type (e.g., `__Ext_IEnumerable_1<T>`)
2. **ExtensionMethods<TShape> helper** - Conditional type mapping shapes to buckets

### Method: Emit

**Signature:**
```csharp
public static void Emit(BuildContext ctx, ExtensionMethodsPlan plan, SymbolGraph graph, string outputDirectory)
```

**What it does:**
- Creates `internal/extensions/` directory
- Generates extension methods file from plan
- Writes `internal/extensions/index.d.ts`

### Key Features

#### Generic Parameter Collapsing
Extension methods that use the target type's generic parameter don't re-declare it:

```csharp
// C#: IndexOf<T>(this Span<T> span, T value)
// TypeScript bucket:
export interface __Ext_Span_1<T> {
  IndexOf(value: T): int;  // No <T> - uses bucket's T
}
```

#### IEquatable Constraint Propagation
Buckets for types requiring `IEquatable<T>` get constrained:

```typescript
export interface __Ext_Span_1<T extends System.IEquatable_1<T>> {
  Contains(value: T): boolean;
  ContainsAny4(values: System_Buffers.SearchValues_1<T>): boolean;
}

// Helper propagates constraint to infer:
export type ExtensionMethods<TShape> =
  TShape extends System.Span_1<infer T0 extends System.IEquatable_1<T0>> ? __Ext_Span_1<T0>
  : {};
```

#### Full Type Safety
Uses TypeRefPrinter for all types - **NO `any` types**:

```typescript
export interface __Ext_IEnumerable_1<T> {
  Select<TResult>(selector: System.Func_2<T, TResult>): System_Collections_Generic.IEnumerable_1<TResult>;
  Where(predicate: System.Func_2<T, boolean>): System_Collections_Generic.IEnumerable_1<T>;
}
```

### FacadeEmitter Integration

Root facades re-export the helper (single source of truth):

```typescript
// Root facade (_root/index.d.ts or package root):
export type { ExtensionMethods } from './internal/extensions/index.js';
```

User code pattern:
```typescript
import { ExtensionMethods } from '@tsonic/dotnet';
type Rich<T> = T & ExtensionMethods<T>;

const nums: Rich<IEnumerable_1<int>> = ...;
nums.Where(x => x > 0);  // ✓ Fully typed
```

### Statistics (BCL .NET 9)

- Extension method buckets: 122
- Total extension methods: 1,759
- File size: ~91KB
- Largest bucket: `__Ext_IEnumerable_1<T>` (258 LINQ methods)

---

## Output File Format Examples

### 1. index.d.ts (Facade)

```typescript
// Generated by tsbindgen - tsbindgen
// Namespace: System.Collections.Generic
// Facade - Public API Surface

// Import internal declarations
import * as Internal from './internal/index.js';

// Import dependencies
import * as System from '../System/index.js';

// Re-export namespace
export import Generic = Internal.System.Collections.Generic;

// Individual type exports for convenience
export type List_1 = Internal.System.Collections.Generic.List_1;
export type Dictionary_2 = Internal.System.Collections.Generic.Dictionary_2;
export type IEnumerable_1 = Internal.System.Collections.Generic.IEnumerable_1;
```

### 2. internal/index.d.ts (Declarations)

```typescript
// Generated by tsbindgen - tsbindgen
// Namespace: System.Collections.Generic
// Assembly: System.Private.CoreLib

// Branded primitive types for CLR numeric types
export type sbyte = number & { __brand: "sbyte" };
export type byte = number & { __brand: "byte" };
export type int = number & { __brand: "int" };
// ... all primitives ...

// Import support types for unsafe CLR constructs
import type { TSUnsafePointer, TSByRef } from "../_support/types.js";

// Import types from other namespaces
import type { Object, ValueType } from "../System/internal/index.js";

export namespace System.Collections.Generic {
    export class List_1$instance<T> {
        constructor(capacity: int);
        readonly Count: int;
        Add(item: T): void;
        GetEnumerator: IEnumerator_1<T>;
    }

    export interface __List_1$views<T> {
        readonly IEnumerable_1$view: IEnumerable_1<T>;
    }

    export type List_1<T> = List_1$instance<T> & __List_1$views<T>;

    export interface IEnumerable_1<T> {
        GetEnumerator: IEnumerator_1<T>;
    }
}
```

### 3. internal/metadata.json

```json
{
  "namespace": "System.Collections.Generic",
  "contributingAssemblies": [
    "System.Private.CoreLib"
  ],
  "types": [
    {
      "clrName": "System.Collections.Generic.List`1",
      "tsEmitName": "List_1",
      "kind": "Class",
      "accessibility": "Public",
      "isAbstract": false,
      "isSealed": false,
      "isStatic": false,
      "arity": 1,
      "methods": [
        {
          "clrName": "Add",
          "tsEmitName": "Add",
          "normalizedSignature": "Add(T):System.Void",
          "provenance": "Direct",
          "emitScope": "ClassSurface",
          "isStatic": false,
          "isAbstract": false,
          "isVirtual": false,
          "isOverride": false,
          "isSealed": false,
          "arity": 0,
          "parameterCount": 1
        }
      ],
      "properties": [],
      "fields": [],
      "events": [],
      "constructors": []
    }
  ]
}
```

### 4. bindings.json

```json
{
  "namespace": "System.Collections.Generic",
  "types": [
    {
      "clrName": "System.Collections.Generic.List`1",
      "tsEmitName": "List_1",
      "assemblyName": "System.Private.CoreLib",
      "metadataToken": 0,
      "methods": [
        {
          "clrName": "Add",
          "tsEmitName": "Add",
          "metadataToken": 100663359,
          "canonicalSignature": "Add(!0):System.Void",
          "normalizedSignature": "Add(T):System.Void",
          "emitScope": "ClassSurface",
          "arity": 0,
          "parameterCount": 1
        }
      ],
      "properties": [],
      "fields": [],
      "events": [],
      "constructors": []
    }
  ]
}
```

### 5. index.js (Stub)

```javascript
// Generated by tsbindgen - tsbindgen
// Namespace: System.Collections.Generic
// Module Stub - Do Not Execute

throw new Error(
  'Cannot import CLR namespace System.Collections.Generic in JavaScript runtime. ' +
  'This module provides TypeScript type definitions only. ' +
  'Actual implementation requires .NET runtime via Tsonic compiler.'
);
```

### 6. _support/types.d.ts

```typescript
// Generated by tsbindgen - tsbindgen
// Support Types Module
//
// These are opaque placeholders for CLR constructs that have no TS equivalent.
// They intentionally erase to `unknown` to keep the API sound.

/**
 * Marker type for C# pointer types (void*, int*, T*, etc.).
 * Erases to `unknown` for type safety - forces explicit handling.
 * The generic parameter preserves information about the pointed-to type.
 * Branded with unique symbol to maintain identity for auditing.
 */
export type TSUnsafePointer<T> = unknown & { readonly __tsbindgenPtr?: unique symbol };

/**
 * Marker type for C# ref/out/in parameters.
 * Provides structural access via `.value` to match C# semantics.
 * Branded with unique symbol to maintain identity for auditing.
 * Example: `method(arg: TSByRef<int>)` → caller uses `arg.value`
 */
export type TSByRef<T> = { value: T } & { readonly __tsbindgenByRef?: unique symbol };
```

---

## Key Design Decisions

### 1. Companion Views Pattern

**Problem:** TypeScript doesn't support structural intersection for class implementation checking.

**Solution:**
- Emit `TypeName$instance` class with ClassSurface members
- Emit `__TypeName$views` interface with ViewOnly members
- Emit `type TypeName = TypeName$instance & __TypeName$views`

**Benefits:**
- Class surface remains clean (no view member clutter)
- Views are type-checked via intersection
- Consumers get both class and view members via single type

### 2. Branded Primitive Types

**Problem:** TypeScript `number` type can't distinguish `int` vs `float` vs `decimal`.

**Solution:**
- Emit branded type aliases: `type int = number & { __brand: "int" };`
- All namespaces get these (needed for cross-namespace references)

**Benefits:**
- Type safety for CLR numeric types
- No runtime overhead (erases to `number`)
- Intentional misuse detection (can't pass `float` where `int` expected)

### 3. Unsafe Type Markers

**Problem:** TypeScript has no pointers or ref parameters.

**Solution:**
- Centralized `_support/types.d.ts` with marker types
- `TSUnsafePointer<T>` for pointers (erases to `unknown`)
- `TSByRef<T>` for ref/out/in (structural `{ value: T }`)

**Benefits:**
- Type-safe (forces explicit handling)
- Preserves type information
- Branded for auditing
- Single import location

### 4. EmitScope Filtering

**Problem:** Not all members should appear in declarations (some are view-only).

**Solution:**
- `ClassSurface` members → class body
- `StaticSurface` members → static members
- `ViewOnly` members → companion views interface

**Benefits:**
- Clean separation of concerns
- No duplicate members in class surface
- Explicit interface implementations in views

### 5. TypeNameResolver Single Source of Truth

**Problem:** Name mismatches between imports and declarations cause errors.

**Solution:**
- All name resolution goes through `TypeNameResolver`
- Resolver queries `Renamer` for final names
- Never use CLR names directly in printers

**Benefits:**
- Guaranteed name consistency
- Suffix handling in one place
- Easy to audit (single code path)

---

## Summary

The **Emit Phase** is the final phase that generates all output files from the validated `EmissionPlan`. It consists of multiple specialized emitters:

1. **FacadeEmitter** - Public-facing facades with re-exports
2. **InternalIndexEmitter** - Actual TypeScript declarations with companion views
3. **MetadataEmitter** - CLR-specific metadata for Tsonic compiler
4. **BindingEmitter** - CLR-to-TypeScript name mappings for runtime
5. **ModuleStubEmitter** - JavaScript stubs that prevent execution
6. **SupportTypesEmitter** - Centralized unsafe type markers

All emitters use:
- **TypeNameResolver** for consistent type names (single source of truth)
- **Renamer** for final TypeScript identifiers (suffix handling)
- **EmitScope** for member filtering (ClassSurface/StaticSurface/ViewOnly)
- **EmitOrder** for deterministic output (stable across runs)

The output includes:
- **TypeScript declarations** - `.d.ts` files with type definitions
- **Metadata sidecars** - `.json` files with CLR information
- **Binding metadata** - `.json` files with name mappings
- **Module stubs** - `.js` files that throw at runtime
- **Support types** - Centralized marker types for unsafe constructs

All output is deterministic, type-safe, and respects the architecture's guarantees.
