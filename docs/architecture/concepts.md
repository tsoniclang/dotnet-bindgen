# Key Concepts

This document explains the core concepts and patterns used in tsbindgen.

## Facade Pattern

### What is a Facade?

Each namespace generates two TypeScript files:
- `<Namespace>/internal/index.d.ts` - Full declarations with all details
- `<Namespace>.d.ts` - Facade that re-exports with friendly aliases (flat ESM)

### Why Facades?

1. **Clean imports**: Users import from `@tsonic/dotnet/System.js` not `@tsonic/dotnet/System/internal/index.js`
2. **Friendly aliases**: `List<T>` instead of `List_1<T>`
3. **Encapsulation**: Internal structure can change without breaking imports

### Directory Structure

```
output/
  System.Collections.Generic/
    internal/
      index.d.ts            # Full declarations
    bindings.json           # CLR bindings manifest (names + CLR semantics)
  System.Collections.Generic.d.ts  # Facade (public API)
  System.Collections.Generic.js   # Runtime stub (throws)
```

### Internal File (Full Declarations)

The internal file contains complete type definitions:

```typescript
// System.Collections.Generic/internal/index.d.ts

// Imports from other namespaces
import type { int } from "@tsonic/core/types.js";
import * as System_Internal from "../../System/internal/index.js";
import type { IEnumerable_1 } from "../../System.Collections.Generic/internal/index.js";

// Instance interface (properties and methods)
export interface List_1$instance<T> {
    readonly Count: int;
    readonly Capacity: int;
    Add(item: T): void;
    Clear(): void;
    Contains(item: T): boolean;
    IndexOf(item: T): int;
    Insert(index: int, item: T): void;
    Remove(item: T): boolean;
    RemoveAt(index: int): void;
}

// Constructor/static declaration
export declare const List_1: {
    new <T>(): List_1<T>;
    new <T>(capacity: int): List_1<T>;
    new <T>(collection: IEnumerable_1<T>): List_1<T>;
};

// Views interface (explicit interface implementations)
export interface __List_1$views<T> {
    As_ICollection(): System_Internal.ICollection;
    As_IEnumerable(): System_Internal.IEnumerable;
    As_IList(): System_Internal.IList;
}

// Combined type alias
export type List_1<T> = List_1$instance<T> & __List_1$views<T>;
```

### Facade File (Public API)

The facade provides curated exports with friendly aliases. **No `export *`** is used to prevent leaking internal `$instance` and `$views` types to consumers.

```typescript
// System.Collections.Generic.d.ts

// Import internal for type alias references
import * as Internal from './System.Collections.Generic/internal/index.js';

// Cross-namespace type imports for constraints
import type { IComparable_1 } from './System/internal/index.js';

// Public API exports (curated - no export *)
// Value re-exports for classes (TypeScript re-exports both value AND type binding)
export { List_1 as List } from './System.Collections.Generic/internal/index.js';
export { Dictionary_2 as Dictionary } from './System.Collections.Generic/internal/index.js';
export { HashSet_1 as HashSet } from './System.Collections.Generic/internal/index.js';
export { Queue_1 as Queue } from './System.Collections.Generic/internal/index.js';
export { Stack_1 as Stack } from './System.Collections.Generic/internal/index.js';

// Type aliases for interfaces (with Internal.-prefixed constraints)
export type IEnumerable<T> = Internal.IEnumerable_1<T>;
export type IList<T> = Internal.IList_1<T>;
export type ICollection<T> = Internal.ICollection_1<T>;
```

### Value vs Type-Only Exports

The facade uses different export strategies based on type kind:

| Type Kind | Export Strategy | Why |
|-----------|----------------|-----|
| Class | Value re-export | Need `new List<T>()` |
| Struct | Value re-export | Need `new Point()` |
| Enum | Value re-export | Need `ConsoleColor.Red` |
| StaticNamespace | Value re-export | Need `Console.WriteLine()` |
| Interface | Type alias | No runtime value |
| Delegate | Type alias | Function signature only |

```typescript
// Value re-export (for classes, structs, enums)
export { List_1 } from './System.Collections.Generic/internal/index.js';

// Type alias (for interfaces, delegates)
export type IEnumerable<T> = Internal.IEnumerable_1<T>;
```

### Import Paths

Facades simplify import paths for consumers:

```typescript
// ❌ Internal path (tsbindgen implementation detail)
import { List_1 } from '@tsonic/dotnet/System.Collections.Generic/internal/index.js';

// ✅ Facade (stable public surface)
import { List } from '@tsonic/dotnet/System.Collections.Generic.js';
```

## Dual-Scope Naming

### The Problem

A class member and an interface member can have the same name in the emitted TypeScript surface:

```csharp
class MyClass : ICollection {
    public void Clear() { }              // Class member
    void ICollection.Clear() { }         // Explicit interface impl
}
```

Both are named `Clear`. Without separate scopes, these would collide.

### Scope Types

The renaming system uses four scope types:

| Scope Type | Format | Purpose |
|------------|--------|---------|
| Namespace | `ns:System.Collections:internal` | Type names in namespace |
| Type (instance) | `type:System.Collections.Generic.List`1#instance` | Instance members |
| Type (static) | `type:System.Collections.Generic.List`1#static` | Static members |
| View | `view:{TypeStableId}:{InterfaceStableId}#instance` | Explicit interface members |

### Real Example: List<T>

`List<T>` implements multiple interfaces with overlapping member names:

```csharp
// C# - List<T> has both own and explicit interface members
public class List<T> : IList<T>, ICollection, IEnumerable {
    // Own member (visible on class)
    public void Clear() { }

    // Explicit ICollection.CopyTo (not visible on class)
    void ICollection.CopyTo(Array array, int index) { }

    // Explicit IEnumerable.GetEnumerator (different return type)
    IEnumerator IEnumerable.GetEnumerator() { }
}
```

**Scope assignments:**

```
Scope: type:System.Collections.Generic.List`1#instance
  - Clear           (own method)
  - Add             (own method)
  - GetEnumerator   (returns IEnumerator<T>)

Scope: view:..List`1:System.Collections.ICollection#instance
  - CopyTo          (explicit ICollection.CopyTo)

Scope: view:..List`1:System.Collections.IEnumerable#instance
  - GetEnumerator   (explicit - different return type)
```

### TypeScript Output

```typescript
// Class surface - own members
export interface List_1$instance<T> {
    clear(): void;
    add(item: T): void;
    getEnumerator(): IEnumerator_1<T>;  // Generic version
}

// View surface - explicit interface members
export interface __List_1$views<T> {
    As_ICollection(): ICollection;      // Access copyTo via view
    As_IEnumerable(): IEnumerable;      // Access non-generic GetEnumerator
}
```

### Why Static/Instance Separation?

Static and instance members can have the same name in C#:

```csharp
class MyClass {
    public static void Create() { }     // Static
    public void Create() { }            // Instance (different signature)
}
```

Separate scopes prevent false collisions:

```
Scope: type:MyClass#static
  - create (static method)

Scope: type:MyClass#instance
  - create (instance method)
```

### Scope Key Format

Scope keys are strings that uniquely identify each scope:

```
Namespace:  ns:{namespace}:{internal|public}
Type:       type:{ClrFullName}#{instance|static}
View:       view:{TypeStableId}:{InterfaceStableId}#{instance|static}
```

**Examples:**
- `ns:System.Collections.Generic:internal`
- `type:System.Collections.Generic.List`1#instance`
- `view:System.Private.CoreLib:System.Collections.Generic.List`1:System.Collections.ICollection#instance`

## Views and Explicit Interface Implementation

### What is a View?

A view provides access to interface members that don't appear on the class surface. Views are generated by the `ViewPlanner` shape pass.

### Why Views Are Needed

C# has two ways to implement interface members:

**1. Implicit implementation** - visible on class:
```csharp
class MyList : ICollection {
    public void Clear() { }  // Visible as myList.Clear()
}
```

**2. Explicit implementation** - only visible via interface:
```csharp
class MyList : ICollection {
    void ICollection.CopyTo(Array array, int index) { }  // Hidden
}

// C# usage:
var list = new MyList();
list.CopyTo(...);              // Error! Not visible
((ICollection)list).CopyTo(...);  // OK - cast to interface
```

### Real Example: Array

`System.Array` implements multiple interfaces with conflicting members:

```csharp
public abstract class Array : IList, ICollection, IEnumerable, ICloneable {
    // Explicit implementations (different Count types)
    int ICollection.Count => Length;
    int IList.Count => Length;

    // Explicit (return type conflicts)
    IEnumerator IEnumerable.GetEnumerator() { ... }

    // Own property
    public int Length { get; }
}
```

### TypeScript Solution

ViewPlanner creates `__$views` interface with `As_` accessor properties:

```typescript
// Array's own members
export interface Array_$instance {
    readonly length: int;
    getEnumerator(): IEnumerator;
}

// Static members
export declare const Array_: {
    new (length: int): Array_;
    readonly empty: Array_;
    createInstance(elementType: Type, length: int): Array_;
};

// View accessors for explicit implementations
export interface __Array_$views {
    As_IList(): IList;
    As_ICollection(): ICollection;
    As_IEnumerable(): IEnumerable;
    As_ICloneable(): ICloneable;
}

// Combined type
export type Array_ = Array_$instance & __Array_$views;
```

### Usage in TypeScript

```typescript
const arr: Array_ = Array_.createInstance(typeof(int), 10);

// Own member - direct access
console.log(arr.length);

// Explicit interface member - access via view
const collection = arr.As_ICollection();
console.log(collection.count);

// Different GetEnumerator via view
const enumerator = arr.As_IEnumerable().getEnumerator();
```

### When Views Are Created

ViewPlanner creates a view when:

1. **Explicit interface implementation** - member uses `void IFoo.Method()` syntax
2. **Signature conflict** - interface member differs from class member (different return type, parameters)
3. **Non-structural match** - after naming transform, signatures don't match

### View Structure

Each `ExplicitView` contains:

```csharp
public sealed record ExplicitView(
    TypeReference InterfaceReference,       // The interface being viewed
    string ViewPropertyName,                // "As_ICollection"
    ImmutableArray<MemberStableId> ViewMembers  // Members in this view
);
```

### Naming Convention

View property names follow the pattern `As_{InterfaceName}`:

| Interface | View Property |
|-----------|---------------|
| `ICollection` | `As_ICollection` |
| `IList<T>` | `As_IList_1` |
| `IEnumerable<T>` | `As_IEnumerable_1` |
| `IDisposable` | `As_IDisposable` |

## EmitScope

### What is EmitScope?

EmitScope is an enum that determines where a member appears in TypeScript output. Every member is assigned an EmitScope during Shape passes.

```csharp
public enum EmitScope
{
    Unspecified,    // Not yet decided (invalid after Shape)
    ClassSurface,   // On $instance interface
    StaticSurface,  // On static const declaration
    ViewOnly,       // Only in __$views interface
    Omitted         // Not emitted (tracked in bindings.json)
}
```

### EmitScope to Output Mapping

| EmitScope | Output Location | Example |
|-----------|-----------------|---------|
| ClassSurface | `$instance` interface | `list.Add(item)` |
| StaticSurface | `const` declaration | `List_1.Empty` |
| ViewOnly | `__$views` interface | `list.As_ICollection()` |
| Omitted | Only in bindings.json (`emitScope: "Omitted"`) | Indexers, generic statics |

### Real Example: List<T>

```csharp
public class List<T> : IList<T>, ICollection {
    // Instance members
    public void Add(T item) { }           // ClassSurface
    public void Clear() { }               // ClassSurface
    public int Count { get; }             // ClassSurface

    // Static members
    public static List<T> Empty { get; }  // StaticSurface (error: generic static)

    // Explicit interface implementations
    void ICollection.CopyTo(Array a, int i) { }  // ViewOnly

    // Indexer
    public T this[int index] { get; set; }  // Omitted (conflicts)
}
```

**TypeScript output:**

```typescript
// ClassSurface -> $instance interface
export interface List_1$instance<T> {
    Add(item: T): void;     // EmitScope.ClassSurface
    Clear(): void;          // EmitScope.ClassSurface
    readonly Count: int;    // EmitScope.ClassSurface
}

// StaticSurface -> const declaration
export declare const List_1: {
    new <T>(): List_1<T>;
    // Note: static Empty<T> is Omitted (generic static not supported in TS)
};

// ViewOnly -> __$views interface
export interface __List_1$views<T> {
    As_ICollection(): ICollection;  // Access CopyTo via view
}
```

### How EmitScope is Assigned

Different Shape passes assign EmitScope:

| Pass | Assigns |
|------|---------|
| Initial load | Instance -> ClassSurface, Static -> StaticSurface |
| StructuralConformance | Explicit impls -> ViewOnly |
| ExplicitImplSynthesizer | Synthesized members -> ViewOnly |
| IndexerPlanner | Conflicting indexers -> Omitted |
| ClassSurfaceDeduplicator | Duplicate losers -> ViewOnly |
| OverloadUnifier | Duplicate overloads -> Omitted |
| EnumeratorConformancePass | Reset() promoted -> ClassSurface |

### Why Members Are Omitted

Members are marked `Omitted` when they can't be safely emitted:

| Reason | Example | Why |
|--------|---------|-----|
| Generic static | `static T Default<T>` | TypeScript doesn't support class-level generics for statics |
| Indexer conflict | `this[int]` and `this[string]` | Creates duplicate `[key: T]` signatures |
| Duplicate signature | Method overloads with same erased signature | Would cause TS duplicate identifier error |
| Pointer parameter | `void Process(int* ptr)` | Requires unsafe context |

Omitted members remain present in `bindings.json` with `emitScope: "Omitted"` so compilers/tooling can treat omissions as explicit, deterministic decisions.

```json
{
  "types": [
    {
      "clrName": "System.Collections.Generic.List`1",
      "properties": [
        { "clrName": "Empty", "emitScope": "Omitted" }
      ]
    }
  ]
}
```

### EmitScope Validation

Phase Gate validates EmitScope invariants:

- TBG710: Every member must have EmitScope set (not Unspecified)
- TBG711: ViewOnly members must be in exactly one ExplicitView
- TBG702: No member can be both ClassSurface and ViewOnly

## StableId

### What is StableId?

A unique identifier for a type or member that survives all transformations. StableId is the CLR identity - it never changes regardless of naming mode or shape transformations.

### Why StableId Is Needed

During pipeline execution, symbols get renamed and transformed:

```
CLR name:     System.Collections.Generic.List`1.Add
TS name:      List_1.Add
Emitted as:   List_1$instance.Add

StableId stays constant: "System.Private.CoreLib:System.Collections.Generic.List`1::Add(T):void"
```

StableId is used as dictionary keys for:
- Rename decisions (mapping CLR -> TypeScript names)
- Bindings (mapping TypeScript -> CLR for runtime)
- Member tracking across Shape passes

### TypeStableId

For types, StableId combines assembly and CLR full name:

```csharp
public sealed record TypeStableId : StableId
{
    public required string AssemblyName { get; init; }     // "System.Private.CoreLib"
    public required string ClrFullName { get; init; }      // "System.Collections.Generic.List`1"

    // Format: "AssemblyName:ClrFullName"
    public override string ToString() => $"{AssemblyName}:{ClrFullName}";
}
```

**Real examples:**

| Type | TypeStableId |
|------|--------------|
| `List<T>` | `System.Private.CoreLib:System.Collections.Generic.List`1` |
| `Dictionary<K,V>` | `System.Private.CoreLib:System.Collections.Generic.Dictionary`2` |
| `Console` | `System.Console:System.Console` |
| `Enumerable` | `System.Linq:System.Linq.Enumerable` |
| `String` | `System.Private.CoreLib:System.String` |

### MemberStableId

For members, StableId includes declaring type, name, and signature:

```csharp
public sealed record MemberStableId : StableId
{
    public required string DeclaringClrFullName { get; init; }  // "System.Collections.Generic.List`1"
    public required string MemberName { get; init; }             // "Add"
    public required string CanonicalSignature { get; init; }     // "(T):void"
    public int? MetadataToken { get; init; }                     // 100663296 (optional)

    // Format: "AssemblyName:DeclaringType::MemberSignature"
}
```

**Real examples:**

| Member | MemberStableId |
|--------|----------------|
| `List<T>.Add(T)` | `...List`1::Add(T):void` |
| `List<T>.Count` | `...List`1::Count\|int` |
| `List<T>.Clear()` | `...List`1::Clear():void` |
| `String.IsNullOrEmpty(string)` | `...String::IsNullOrEmpty(string):bool` |
| `Console.WriteLine(string)` | `...Console::WriteLine(string):void` |

### Canonical Signature Format

The signature format disambiguates overloads:

| Member Kind | Signature Format | Example |
|-------------|------------------|---------|
| Method | `(params):return` | `(int,string):void` |
| Property | `\|type` | `\|int` |
| Indexer | `(params)\|type` | `(int)\|T` |
| Field | `\|type` | `\|int` |
| Event | `\|type` | `\|EventHandler` |

### MetadataToken

The optional `MetadataToken` is the CLR reflection token. It's used for exact runtime correlation but excluded from equality comparison (semantic identity only).

```csharp
// Two MemberStableIds are equal if CLR identity matches
// even if MetadataTokens differ (e.g., from different assembly versions)
id1 == id2  // true if name+signature match, ignores token
```

### StableId in Bindings

The `bindings.json` manifest carries StableIds for deterministic compiler/runtime correlation:

```json
{
  "types": [{
    "stableId": "System.Private.CoreLib:System.Collections.Generic.List`1",
    "clrName": "System.Collections.Generic.List`1",
    "methods": [{
      "stableId": "...List`1::Add(T):void",
      "clrName": "Add",
      "metadataToken": 100663296,
      "emitScope": "ClassSurface"
    }]
  }]
}
```

## Type Emission Pattern

### The Three-Part Pattern

Classes and structs emit as three parts in the internal index:

```typescript
// internal/index.d.ts

// 1. Instance interface - instance properties and methods
export interface List_1$instance<T> {
    readonly Count: int;
    Add(item: T): void;
    Remove(item: T): boolean;
    Clear(): void;
}

// 2. Static/constructor const - constructors and static members
export const List_1: {
    new <T>(): List_1<T>;
    new <T>(capacity: int): List_1<T>;
    new <T>(collection: IEnumerable_1<T>): List_1<T>;
};

// 3. Combined type alias - unifies instance + views
export type List_1<T> = List_1$instance<T> & __List_1$views<T>;
```

### Implementation in ClassPrinter

The ClassPrinter has separate methods for each part:

```csharp
// From ClassPrinter.cs
public static class ClassPrinter
{
    // Emits: interface T$instance { ... }
    public static string PrintInstance(TypeSymbol type, ...) { ... }

    // Emits: const T: { new(...): T; statics... };
    public static string PrintValueExport(TypeSymbol type, ...) { ... }
}

// From InternalIndexEmitter.cs - type alias
private static string EmitIntersectionTypeAlias(TypeSymbol type, ...)
{
    // Emits: export type T<...> = T$instance<...> & __T$views<...>;
    var rhsExpression = $"{finalName}$instance{typeArgs} & __{finalName}$views{typeArgs}";
    sb.Append($"export type {finalName}{typeParams} = {rhsExpression};");
}
```

### Why This Pattern?

**1. Static-side inheritance fix (TS2417)**

TypeScript checks `typeof Derived extends typeof Base` for class inheritance. But .NET static methods aren't polymorphic—derived types can have different overloads. Using `interface` + `const` avoids this:

```typescript
// ❌ class syntax - TS2417 when static signatures differ
export class List_1<T> extends Object { ... }

// ✅ interface + const - no static-side inheritance checking
export interface List_1$instance<T> extends Object$instance { ... }
export const List_1: { new<T>(): List_1<T>; };  // Returns full type, not $instance
```

**2. Separation of concerns**

- Instance interface: Type-only declaration (properties, methods)
- Const: Runtime value (constructors, static members)
- Type alias: Public API that combines everything

**3. View composition**

The type alias combines instance members with view accessors:

```typescript
// What users see:
export type List_1<T> = List_1$instance<T> & __List_1$views<T>;

// Expands to all members:
// - count, add(), remove(), clear() from $instance
// - As_ICollection(), As_IEnumerable() from $views
```

### Real Example: StringBuilder

```typescript
// 1. Instance interface
export interface StringBuilder$instance {
    readonly length: int;
    readonly capacity: int;
    append(value: string): StringBuilder;
    append(value: char): StringBuilder;
    appendLine(): StringBuilder;
    appendLine(value: string): StringBuilder;
    clear(): StringBuilder;
    toString(): string;
}

// 2. Static/constructor const
export const StringBuilder: {
    new (): StringBuilder;
    new (capacity: int): StringBuilder;
    new (value: string): StringBuilder;
    new (value: string, capacity: int): StringBuilder;
};

// 3. Type alias (no views for StringBuilder)
export type StringBuilder = StringBuilder$instance & __StringBuilder$views;
```

### Type-Specific Variations

| TypeKind | Pattern |
|----------|---------|
| Class/Struct | `interface $instance` + `const` + `type alias` |
| Interface | `interface $instance` only (no const, no alias) |
| Enum | `const enum` (single declaration) |
| Delegate | Callable + `interface $instance` + `type alias` |
| Static class | `abstract class` (special case) |

### Abstract/Protected Constructors (Extendable Bases)

TypeScript requires the base expression in `class Derived extends Base {}` to be **constructable**
(have a `new(...)` signature). This is a TypeScript rule, not a runtime rule.

.NET has several base classes that are designed to be subclassed, but are **not directly constructible**
from user code (e.g. abstract classes, or classes with only `protected`/`protected internal` constructors).
If tsbindgen emitted these as a plain `export const Base: { ...statics... }` with no constructor typing,
vanilla `tsc` would reject `extends Base`.

To make inheritance typecheck while still preventing `new Base()` in TypeScript, tsbindgen attaches an
**abstract constructor type** to the value export via intersection:

```ts
// Example: System.Attribute (abstract base, no public constructors)
export const Attribute:
  (abstract new() => Attribute) &
  {
    // ...static members...
  };
```

Notes:

- We **cannot** write `abstract new(...)` inside the `{ ... }` object type literal — TypeScript rejects
  `abstract` as a type member. The intersection form is the valid representation.
- This enables `class MyAttr extends Attribute {}` to typecheck, but `new Attribute()` remains invalid
  at the TS type level.
- We intentionally do **not** add constructor typing for CLR “magic” base types where `extends` should
  fail (e.g. `System.Delegate`, `System.MulticastDelegate`, `System.Enum`).

### Universal $instance Naming

All classes/structs use the `$instance` suffix—even those without views:

```csharp
// From SymbolRenamer.cs
public static string GetInstanceTypeName(TypeSymbol type)
{
    var stem = GetFinalTypeName(type);

    // Enums: both type and value - no split
    if (type.Kind == TypeKind.Enum) return stem;

    // Delegates: function types - no split
    if (type.Kind == TypeKind.Delegate) return stem;

    // All other types get $instance suffix
    return stem + "$instance";
}
```

This enables Phase Gate validation (TBG8A1) to verify consistent naming across the entire codebase.

## Delegate Callable Pattern

### The Problem

C# delegates are callable types with both function signature and object properties:

```csharp
Action<int> callback = (x) => Console.WriteLine(x);
callback(42);                    // Invoke delegate (callable)
Console.WriteLine(callback.Target);   // Access Target property (object)
Console.WriteLine(callback.Method);   // Access Method property (object)
```

TypeScript needs to support both calling the delegate AND accessing its properties.

### TypeScript Solution

Delegates emit as two parts:

**1. Simple type alias (in internal/index.d.ts):**

```typescript
// From ClassPrinter.PrintDelegate()
type Action_1<T> = (obj: T) => void;
type Func_2<T, TResult> = (arg: T) => TResult;
type Predicate_1<T> = (obj: T) => boolean;
```

**2. Intersection type alias (for composition):**

```typescript
// From EmitIntersectionTypeAlias() - when views exist
export type Func_2<T, TResult> =
    ((arg: T) => TResult) &          // Callable signature (from Invoke)
    Func_2$instance<T, TResult> &    // Instance members (Target, Method)
    __Func_2$views<T, TResult>;      // View accessors
```

### Implementation

The callable signature comes from the delegate's `Invoke` method:

```csharp
// From InternalIndexEmitter.BuildDelegateCallSignature()
private static string BuildDelegateCallSignature(TypeSymbol type, ...)
{
    // Find the Invoke method - this defines the delegate's signature
    var invokeMethod = type.Members.Methods
        .FirstOrDefault(m => m.ClrName == "Invoke");

    if (invokeMethod == null)
        return ""; // Fallback: no call signature

    // Build parameter list: (arg1: T, arg2: U, ...)
    var parameters = string.Join(", ", invokeMethod.Parameters.Select(p =>
        $"{p.Name}: {TypeRefPrinter.Print(p.Type, ...)}"));

    // Build return type
    var returnType = TypeRefPrinter.Print(invokeMethod.ReturnType, ...);

    // Return call signature: ((params) => returnType)
    return $"(({parameters}) => {returnType})";
}
```

### Real Examples

**Action (void return):**

```typescript
// System.Action<T>
type Action_1<T> = (obj: T) => void;

// Usage - arrow function assignable
const log: Action_1<string> = (s) => console.log(s);
log("hello");
```

**Func (with return):**

```typescript
// System.Func<T, TResult>
type Func_2<T, TResult> = (arg: T) => TResult;

// Usage - arrow function assignable
const double: Func_2<int, int> = (x) => x * 2;
const result = double(21);  // 42
```

**Predicate (boolean return):**

```typescript
// System.Predicate<T>
type Predicate_1<T> = (obj: T) => boolean;

// Usage - in LINQ methods
list.where((x) => x > 0);  // Arrow function works
```

**EventHandler:**

```typescript
// System.EventHandler<TEventArgs>
type EventHandler_1<TEventArgs> = (sender: Object, e: TEventArgs) => void;

// Usage
const handler: EventHandler_1<ClickEventArgs> = (sender, e) => {
    console.log(`Clicked at ${e.x}, ${e.y}`);
};
```

### Why the Intersection Pattern?

The intersection allows both calling AND property access:

```typescript
// Full delegate type with properties
export type Func_2<T, TResult> =
    ((arg: T) => TResult) &          // Can call: func(42)
    Func_2$instance<T, TResult> &    // Can access: func.target, func.method
    __Func_2$views<T, TResult>;      // Can view: func.As_ICloneable()

const func: Func_2<int, string> = (x) => x.toString();

// Both work:
func(42);                            // Callable
console.log(func.method?.name);      // Property access
```

### Delegate vs Regular Types

| Aspect | Delegate | Class/Struct |
|--------|----------|--------------|
| Primary alias | `type T = (params) => R` | `type T = T$instance & __T$views` |
| Callable | Yes (from `Invoke`) | No |
| $instance suffix | Not used | Used |
| Instance interface | `T$instance` for properties | `T$instance` for all members |

## Property Covariance

### The Problem

C# allows covariant property overrides—a derived class can return a more specific type:

```csharp
class RequestCachePolicy {
    public virtual RequestCacheLevel Level { get; }  // Base type
}

class HttpRequestCachePolicy : RequestCachePolicy {
    public override HttpRequestCacheLevel Level { get; }  // More specific type
}
```

TypeScript doesn't support property overloading. This causes TS2416:

```typescript
// ❌ TypeScript error TS2416
interface HttpRequestCachePolicy$instance extends RequestCachePolicy$instance {
    readonly level: HttpRequestCacheLevel;  // Incompatible with base's RequestCacheLevel
}
```

### The Solution: PropertyOverrideUnifier

The `PropertyOverrideUnifier` shape pass analyzes inheritance chains and unifies conflicting property types:

```typescript
// ✅ Both classes use union type
interface RequestCachePolicy$instance {
    readonly level: RequestCacheLevel | HttpRequestCacheLevel;
}

interface HttpRequestCachePolicy$instance extends RequestCachePolicy$instance {
    readonly level: RequestCacheLevel | HttpRequestCacheLevel;  // Same union
}
```

### Implementation

```csharp
// From PropertyOverrideUnifier.cs
public static class PropertyOverrideUnifier
{
    public static PropertyOverridePlan Build(SymbolGraph graph, BuildContext ctx)
    {
        var plan = new PropertyOverridePlan();

        // Process all types that have a base class
        var typesWithBase = graph.TypeIndex.Values
            .Where(t => t.BaseType != null);

        foreach (var type in typesWithBase)
        {
            UnifyPropertiesInHierarchy(type, graph, ctx, plan);
        }

        return plan;
    }
}

// From PropertyOverridePlan.cs
public sealed class PropertyOverridePlan
{
    // Maps (type stable ID, property stable ID) → unified TypeScript type string
    public Dictionary<(string TypeStableId, string PropertyStableId), string>
        PropertyTypeOverrides { get; init; } = new();
}
```

### Real Example: Cache Policy

```csharp
// C# BCL hierarchy
public class RequestCachePolicy {
    public virtual RequestCacheLevel Level { get; }
}

public class HttpRequestCachePolicy : RequestCachePolicy {
    public override HttpRequestCacheLevel Level { get; }  // Covariant
}
```

**Generated TypeScript:**

```typescript
// System.Net.Cache/internal/index.d.ts

// Base class - unified type
export interface RequestCachePolicy$instance {
    readonly level: RequestCacheLevel | HttpRequestCacheLevel;
}

// Derived class - same unified type
export interface HttpRequestCachePolicy$instance extends RequestCachePolicy$instance {
    readonly level: RequestCacheLevel | HttpRequestCacheLevel;
}
```

### Diagnostic Codes

| Code | Description |
|------|-------------|
| TBG300 | Property covariance detected (INFO) |
| TBG310 | Covariance summary in Phase Gate (INFO) |
| TBG903 | PropertyOverridePlan validity error (ERROR) |

### Impact in BCL

The BCL has ~12 property covariance cases:

| Base Type | Derived Type | Property |
|-----------|--------------|----------|
| `RequestCachePolicy` | `HttpRequestCachePolicy` | `Level` |
| `WebRequest` | `HttpWebRequest` | `CachePolicy` |
| `WebResponse` | `HttpWebResponse` | `Headers` |
| ... | ... | ... |

All are handled automatically by PropertyOverrideUnifier—no manual intervention needed.

### Why Union Types?

The union approach is safe:
1. **Reading**: Code expecting `Base.property` still works (union contains base type)
2. **Type narrowing**: TypeScript can narrow to specific type when needed
3. **Runtime**: Actual property value is always the most derived type

```typescript
const policy: RequestCachePolicy = getPolicy();

// Property type is RequestCacheLevel | HttpRequestCacheLevel
const level = policy.level;

// Can narrow with type guards
if (policy instanceof HttpRequestCachePolicy) {
    // TypeScript knows: level is HttpRequestCacheLevel
}
```

## Extension Methods

### The Problem

C# extension methods appear as instance methods on the target type:

```csharp
// Definition in static class
public static class Enumerable {
    public static IEnumerable<T> Where<T>(this IEnumerable<T> source, Func<T, bool> predicate) { }
    public static IEnumerable<TResult> Select<T, TResult>(this IEnumerable<T> source, Func<T, TResult> selector) { }
}

// Usage - looks like instance method
var filtered = list.Where(x => x > 0).Select(x => x * 2);
```

TypeScript doesn't have extension methods. We need a pattern that:
1. Makes extension methods callable as instance methods
2. Groups methods by target type
3. Handles generic type parameters

### TypeScript Solution: Bucket Types + ExtensionMethods Wrapper

Extension methods are emitted into `__internal/extensions/index.d.ts` in two layers:

1) **Bucket interfaces** keyed by target type (per declaring namespace)  
2) A **wrapper type** (`ExtensionMethods_<Namespace>`) that intersects a receiver shape with the applicable buckets (C# “using” semantics).

```typescript
// __internal/extensions/index.d.ts (excerpt)

import type { Rewrap } from "@tsonic/core/lang.js";

// Bucket for IEnumerable<T> extension methods in System.Linq
export interface __Ext_System_Linq_IEnumerable_1<T> {
  where(
    predicate: System.Func_2<T, boolean>
  ): Rewrap<this, System_Collections_Generic.IEnumerable_1<T>>;
  select<TResult>(
    selector: System.Func_2<T, TResult>
  ): Rewrap<this, System_Collections_Generic.IEnumerable_1<TResult>>;
  // ...
}

// Generic helper type for extension methods in namespace: System.Linq
export type ExtensionMethods_System_Linq<TShape> =
  TShape & (
    (TShape extends System_Collections_Generic.IEnumerable_1<infer T0> ? __Ext_System_Linq_IEnumerable_1<T0> : {}) &
    // ... other targets (IQueryable<T>, arrays, etc.)
    {}
  );
```

The namespace facade re-exports the wrapper as `ExtensionMethods`:

```typescript
// System.Linq.d.ts
export type { ExtensionMethods_System_Linq as ExtensionMethods } from "./__internal/extensions/index.js";
```

Usage looks like a type-level `using System.Linq;`:

```typescript
import type { ExtensionMethods as Linq } from "./System.Linq.js";
import type { IEnumerable } from "./System.Collections.Generic.js";

type LinqSeq<T> = Linq<IEnumerable<T>>;
declare const xs: LinqSeq<number>;
xs.where((x) => x > 0).select((x) => x * 2);
```

### Sticky Extension Scopes (Rewrap)

In C#, extension methods remain “in scope” across fluent chains as long as the relevant
namespaces are imported via `using`. The receiver type does not need to be re-wrapped at
each hop:

```csharp
using System.Linq;
using Microsoft.EntityFrameworkCore;

var ids = await db.Events
  .Where(e => e.IsActive)
  .Select(e => e.Id)
  .ToListAsync();
```

In TypeScript, the naive `ExtensionMethods<TShape>` model can lose scopes when you compose
multiple extension namespaces (for example LINQ + EF Core): a method in one scope returns
`TNewShape`, but the other scope’s methods are no longer present on the returned type.

To make extension scopes **sticky** across fluent chains, tsbindgen emits bucket methods with:

- `Rewrap<this, ReturnShape>` instead of just `ReturnShape`

`Rewrap` is a type-level helper in `@tsonic/core/lang.js` that preserves the “extension scopes”
carried by the receiver type and re-applies them to the return shape.

Result: users can wrap the root once and then write idiomatic fluent code without re-wrapping
after each call.

### Implementation

**Step 1: ExtensionMethodAnalyzer** groups methods by target type:

```csharp
// From ExtensionMethodAnalyzer.cs
public static ExtensionMethodsPlan Analyze(BuildContext ctx, SymbolGraph graph)
{
    // Collect all extension methods from static classes
    var allExtensionMethods = graph.Namespaces
        .SelectMany(ns => ns.Types)
        .Where(t => t.IsStatic)
        .SelectMany(t => t.Members.Methods)
        .Where(m => m.IsExtensionMethod);

    // Group by target type (first parameter's type)
    var buckets = allExtensionMethods
        .GroupBy(m => GetTargetTypeKey(m.ExtensionTarget));

    return new ExtensionMethodsPlan { Buckets = buckets.ToImmutableArray() };
}
```

**Step 2: ExtensionBucketPlan** describes each bucket:

```csharp
// From ExtensionBucketPlan.cs
public sealed record ExtensionBucketPlan
{
    public required ExtensionTargetKey Key { get; init; }       // Target type identity
    public required TypeSymbol TargetType { get; init; }        // IEnumerable_1
    public required ImmutableArray<MethodSymbol> Methods { get; init; }  // All methods

    // TypeScript interface name: "__Ext_IEnumerable_1"
    public string BucketInterfaceName => $"__Ext_{TargetType.TsEmitName}";
}
```

**Step 3: ExtensionsEmitter** generates the file:

```csharp
// From ExtensionsEmitter.cs
public static void Emit(BuildContext ctx, ExtensionMethodsPlan plan, SymbolGraph graph, string outputDirectory)
{
    // Create __internal/extensions/index.d.ts
    var extensionsDir = Path.Combine(outputDirectory, "__internal", "extensions");
    Directory.CreateDirectory(extensionsDir);

    // Generate bucket interfaces
    foreach (var bucket in plan.Buckets)
    {
        // export interface __Ext_IEnumerable_1<T> { ... }
    }
}
```

### Real Example: LINQ

**C# definition (System.Linq.Enumerable):**

```csharp
public static class Enumerable {
    public static IEnumerable<T> Where<T>(this IEnumerable<T> source, Func<T, bool> predicate);
    public static IEnumerable<TResult> Select<T, TResult>(this IEnumerable<T> source, Func<T, TResult> selector);
    public static T First<T>(this IEnumerable<T> source);
    public static List<T> ToList<T>(this IEnumerable<T> source);
    public static int Count<T>(this IEnumerable<T> source);
    // ... 100+ methods
}
```

**Generated bucket interface:**

```typescript
// __internal/extensions/index.d.ts

export interface __Ext_IEnumerable_1<T> {
    where(predicate: Func_2<T, boolean>): IEnumerable_1<T>;
    select<TResult>(selector: Func_2<T, TResult>): IEnumerable_1<TResult>;
    first(): T;
    first(predicate: Func_2<T, boolean>): T;
    toList(): List_1<T>;
    toArray(): T[];
    count(): int;
    count(predicate: Func_2<T, boolean>): int;
    any(): boolean;
    any(predicate: Func_2<T, boolean>): boolean;
    all(predicate: Func_2<T, boolean>): boolean;
    // ... all LINQ methods
}
```

### How Bucket Interfaces Are Used

The bucket interfaces are merged into target types via declaration merging:

```typescript
// Usage in code
const list: List_1<int> = new List_1<int>();
list.add(1);
list.add(2);
list.add(3);

// Extension methods available as instance methods
const filtered = list.where(x => x > 1);       // From __Ext_IEnumerable_1
const doubled = list.select(x => x * 2);       // From __Ext_IEnumerable_1
const count = list.count();                     // From __Ext_IEnumerable_1
```

### Target Type Grouping

Extension methods are grouped by their target type's generic definition:

| Extension Method | Target Type | Bucket Interface |
|-----------------|-------------|------------------|
| `Where<T>(IEnumerable<T>)` | `IEnumerable<T>` | `__Ext_IEnumerable_1<T>` |
| `ToList<T>(IEnumerable<T>)` | `IEnumerable<T>` | `__Ext_IEnumerable_1<T>` |
| `AsQueryable<T>(IEnumerable<T>)` | `IEnumerable<T>` | `__Ext_IEnumerable_1<T>` |
| `Append(string, string)` | `string` | `__Ext_String` |

### Diagnostic Codes

| Code | Description |
|------|-------------|
| TBG904 | Extension methods plan invalid |
| TBG905 | Extension method has erased 'any' type |
| TBG906 | Extension bucket name invalid |
| TBG907 | Extension import unresolved |

## Honest Emission

### The Problem

Not all C# interfaces can be safely used in TypeScript `extends`:

```typescript
// This might cause TS2430 if there are conflicting members
interface MyClass$instance extends IFoo$instance, IBar$instance { }
// Error TS2430: Interface 'MyClass$instance' incorrectly extends 'IFoo$instance'
// Error TS2320: Cannot simultaneously extend types 'IFoo$instance' and 'IBar$instance'
```

TypeScript has strict structural requirements:
- **TS2430**: Interface member signature doesn't match base
- **TS2320**: Multiple bases have conflicting member signatures

### The Solution: SafeToExtendAnalyzer

The `SafeToExtendAnalyzer` determines which interfaces are safe to extend:

```csharp
// From SafeToExtendAnalyzer.cs
public static class SafeToExtendAnalyzer
{
    public record SafeToExtendResult(
        IReadOnlyList<TypeReference> AssignableInterfaces,      // Safe for extends
        IReadOnlyList<(TypeReference Interface, string Reason)> NonAssignableInterfaces  // Must use views
    );

    public static Dictionary<string, SafeToExtendResult> Analyze(
        BuildContext ctx, SymbolGraph graph, TypeNameResolver resolver)
    {
        foreach (var type in graph.AllTypes)
        {
            // Build the type's member signature map
            var typeSurface = BuildMemberSignatureMap(type, ...);

            foreach (var iface in type.Interfaces)
            {
                // Check if interface members are compatible with type surface
                if (IsCompatible(typeSurface, ifaceSurface))
                    assignable.Add(iface);
                else
                    nonAssignable.Add((iface, reason));
            }
        }
    }
}
```

### Real Example: IEnumerator

A type implementing multiple interfaces with different `Current` property types:

```csharp
// C#: CharEnumerator implements multiple interfaces
public class CharEnumerator : IEnumerator<char>, IEnumerator {
    public char Current { get; }             // From IEnumerator<char>
    object IEnumerator.Current { get; }      // Explicit impl (different type)
}
```

**Analysis result:**

```typescript
// SafeToExtendAnalyzer output:
// - IEnumerator<char> -> SAFE (Current: char matches class surface)
// - IEnumerator -> UNSAFE (Current: object conflicts with char)

// Generated TypeScript:
export interface CharEnumerator$instance
    extends IEnumerator_1$instance<CLROf<char>> {  // Safe interface in extends
    readonly current: char;
}

export interface __CharEnumerator$views {
    As_IEnumerator(): IEnumerator;  // Unsafe interface as view
}
```

### Implementation Pattern

Two passes coordinate honest emission:

**1. HonestEmissionPlanner** - tracks unsatisfiable interfaces:

```csharp
// From HonestEmissionPlanner.cs
public static HonestEmissionPlan PlanHonestEmission(BuildContext ctx, ...)
{
    // Find interfaces where class can't structurally satisfy requirements
    var unsatisfiableByType = conformanceIssues
        .GroupBy(i => i.TypeStableId)
        .ToDictionary(g => g.Key, g => g.Select(i => i.InterfaceStableId).ToHashSet());

    return new HonestEmissionPlan { UnsatisfiableInterfaces = unsatisfiableByType };
}
```

**2. SafeToExtendAnalyzer** - determines extends vs views:

```csharp
// Safe = no conflicting member signatures
// Unsafe = member signature conflicts with type surface
```

### Why "Honest"?

The emission is "honest" because it only claims what TypeScript can verify:
- **Honest**: `extends IFoo` only if all `IFoo` members are compatible
- **Dishonest**: Claiming `extends IFoo` when signatures conflict (causes TS errors)

## SCC Buckets (Strongly Connected Components)

### The Problem

Circular namespace dependencies cause TypeScript import errors:

```typescript
// System.Collections.Generic/internal/index.d.ts
import type { Func_2 } from "../../System/internal/index.js";  // Func used in List

// System/internal/index.d.ts
import type { IEnumerable_1 } from "../../System.Collections.Generic/internal/index.js";  // IEnumerable used in Func

// ❌ Circular import! TypeScript may fail to resolve types
```

The .NET BCL has many such circular dependencies:
- `System.Collections.Generic` ↔ `System.Linq`
- `System` ↔ `System.Collections.Generic`
- `System.IO` ↔ `System.Text`

### The Solution: SCCPlanner

The `SCCPlanner` uses Tarjan's algorithm to find **Strongly Connected Components** (SCCs)—groups of namespaces that mutually depend on each other:

```csharp
// From SCCPlan.cs
public sealed record SCCPlan
{
    // All SCCs in dependency graph
    public required IReadOnlyList<SCCBucket> Buckets { get; init; }

    // Maps namespace → bucket index
    public required IReadOnlyDictionary<string, int> NamespaceToBucket { get; init; }
}

public sealed record SCCBucket
{
    public required string BucketId { get; init; }       // "scc_0" or namespace name
    public required IReadOnlyList<string> Namespaces { get; init; }
    public bool IsMultiNamespace => Namespaces.Count > 1;  // Has cycles?
}
```

### Real Example: BCL SCCs

```
SCC Analysis for .NET BCL:

Bucket 0 (multi-namespace SCC):
  - System
  - System.Collections.Generic
  - System.Linq
  - System.Threading.Tasks
  → Types within can freely reference each other

Bucket 1 (singleton SCC - no cycles):
  - System.Net.Http
  → Can import from Bucket 0, but not vice versa

Bucket 2 (singleton SCC):
  - System.Text.Json
```

### How SCCs Help

tsbindgen computes SCCs (Tarjan) over the namespace dependency graph and records them in the emission plan.

PhaseGate uses the SCC plan to:
- Detect and warn on **inter-SCC** cycles (`TBG201`)
- Suppress warnings for **intra-SCC** cycles (expected in the BCL)

### Diagnostic Code

| Code | Description |
|------|-------------|
| TBG201 | Circular inheritance/dependency detected (handled by SCC bucketing) |

## Primitive Lifting in Generic Type Arguments

### The Principle

tsbindgen emits CLR type names directly in generic type argument positions. This ensures:
1. CLR type identity is preserved at the type level
2. Generic constraints are satisfied without runtime type inference
3. The Tsonic compiler can enforce numeric correctness at compile time

### Type Emission Layers

The type system has three distinct layers:

| Layer | Responsibility | Example |
|-------|----------------|---------|
| **tsbindgen** | Emits CLR-true surface names | `Span_1<Char>`, `IEquatable_1<Int32>` |
| **@tsonic/core** | Primitive aliases + unsafe markers | `type int = number`, `type char = string & { __brand: "char" }`, `type ptr<T> = ...` |
| **Tsonic compiler** | Enforces numeric correctness | `42 as int` validates bounds |

### Implementing CLR Interfaces (Nominal Brands + `Interface<T>`)

tsbindgen must preserve **CLR identity** for interfaces and classes. A plain TypeScript
structural interface is not CLR-faithful: unrelated shapes can accidentally satisfy it,
and “duck typing” can cause ambiguous member/extension-method binding.

To prevent this, generated CLR interface types carry internal nominal brand members
(for example `__tsonic_iface_*`). These members are not intended to be implemented
by end users.

When you want to author a TypeScript class that implements a CLR interface, wrap the
interface with `Interface<T>` from `@tsonic/core/lang.js` in the `implements` clause:

```ts
import type { Interface } from "@tsonic/core/lang.js";
import type { IDesignTimeDbContextFactory } from "@tsonic/efcore/Microsoft.EntityFrameworkCore.Design.js";

export class MyFactory
  implements Interface<IDesignTimeDbContextFactory<MyDbContext>>
{
  CreateDbContext(_args: string[]): MyDbContext {
    return new MyDbContext();
  }
}
```

`Interface<T>` strips internal `__tsonic_iface_*` members at the TypeScript layer only.
It does **not** change the CLR interface that gets emitted in C#.

### How It Works

When a primitive type appears as a generic type argument, tsbindgen emits the CLR type name instead of the TypeScript primitive alias:

```typescript
// Value positions use TS aliases for ergonomics
function add(a: int, b: int): int;

// Generic type arguments use CLR names
type IntList = List_1<Int32>;  // Not List_1<int>
tryFormat(destination: Span_1<Char>): boolean;  // Not Span_1<char>
```

### Implementation: PrimitiveLift

The lifting rules are defined in `PrimitiveLift.cs`:

```csharp
// From PrimitiveLift.cs
internal static class PrimitiveLift
{
    // Rules: (TsName, ClrFullName, ClrSimpleName, TsCarrier)
    internal static readonly (string, string, string, string)[] Rules = {
        ("sbyte",   "System.SByte",   "SByte",   "number"),
        ("short",   "System.Int16",   "Int16",   "number"),
        ("int",     "System.Int32",   "Int32",   "number"),
        ("long",    "System.Int64",   "Int64",   "number"),
        ("float",   "System.Single",  "Single",  "number"),
        ("double",  "System.Double",  "Double",  "number"),
        ("decimal", "System.Decimal", "Decimal", "number"),
        ("char",    "System.Char",    "Char",    "string"),
        ("boolean", "System.Boolean", "Boolean", "boolean"),
        ("string",  "System.String",  "String",  "string"),
        // ... all primitives
    };

    // Returns CLR simple name for a TS primitive, or null if not a primitive
    public static string? GetClrSimpleName(string tsPrimitive) { ... }
}
```

### Real Example: IEquatable<int>

```typescript
// @tsonic/core defines the aliases
type int = number;
type Int32 = number;

// tsbindgen emits CLR name in generic position
interface Int32$instance extends IEquatable_1$instance<Int32> { ... }

// Interface method uses the type parameter directly
interface IEquatable_1$instance<T> {
    equals(other: T): boolean;  // T is Int32 when used with Int32
}
```

### Cross-Namespace Qualification

When CLR type names are used outside the System namespace, they are qualified with `System_Internal.`:

```typescript
// In System.Collections.Generic/internal/index.d.ts
import * as System_Internal from "../../System/internal/index.js";

// CLR names qualified to resolve correctly
interface List_1$instance<T> {
    tryFormat(destination: Span_1<System_Internal.Char>): boolean;
}
```

### Why Direct CLR Names?

The direct approach has several advantages over alternatives:

1. **No conditional type overhead**: No `CLROf<T>` wrapper to resolve at type-check time
2. **Clear semantics**: What you see is what the CLR type system expects
3. **Clean separation**: tsbindgen emits structure, @tsonic/core provides primitive aliases/markers
4. **Simpler output**: Generated declarations are more readable

## Nested Type Flattening

### The Problem

C# nested types use `+` separator in CLR naming:

```csharp
public class List<T> {
    public struct Enumerator : IEnumerator<T> { }  // CLR name: List`1+Enumerator
}

public class Dictionary<TKey, TValue> {
    public struct KeyCollection { }      // Dictionary`2+KeyCollection
    public struct ValueCollection { }    // Dictionary`2+ValueCollection
    public struct Enumerator { }         // Dictionary`2+Enumerator
}
```

TypeScript doesn't support nested type declarations like C#:

```typescript
// ❌ Not valid TypeScript
class List_1<T> {
    class Enumerator { }  // Can't nest classes in TS
}
```

### The Solution: $ Separator

Nested types are flattened to namespace level with `$` separator:

```typescript
// System.Collections.Generic/internal/index.d.ts

// Parent type
export interface List_1$instance<T> {
    getEnumerator(): List_1$Enumerator<T>;
}

// Nested type flattened with $ separator
export interface List_1$Enumerator$instance<T> extends IEnumerator_1$instance<T> {
    readonly current: T;
    moveNext(): boolean;
    reset(): void;
    dispose(): void;
}

export const List_1$Enumerator: {
    // Nested struct's constructor
};

export type List_1$Enumerator<T> = List_1$Enumerator$instance<T> & __List_1$Enumerator$views<T>;
```

### Naming Convention

| CLR Name | TypeScript Name |
|----------|-----------------|
| `List`1+Enumerator` | `List_1$Enumerator` |
| `Dictionary`2+KeyCollection` | `Dictionary_2$KeyCollection` |
| `Dictionary`2+ValueCollection` | `Dictionary_2$ValueCollection` |
| `Delegate+InvocationListEnumerator` | `Delegate$InvocationListEnumerator` |

### Real Example: Dictionary Collections

```csharp
// C# nested types
public class Dictionary<TKey, TValue> {
    public struct KeyCollection : ICollection<TKey> { }
    public struct ValueCollection : ICollection<TValue> { }
}
```

**Generated TypeScript:**

```typescript
// Dictionary itself
export interface Dictionary_2$instance<TKey, TValue> {
    readonly keys: Dictionary_2$KeyCollection<TKey, TValue>;
    readonly values: Dictionary_2$ValueCollection<TKey, TValue>;
}

// Flattened nested types
export interface Dictionary_2$KeyCollection$instance<TKey, TValue>
    extends ICollection_1$instance<TKey> {
    readonly count: int;
    contains(item: TKey): boolean;
    copyTo(array: TKey[], arrayIndex: int): void;
}

export interface Dictionary_2$ValueCollection$instance<TKey, TValue>
    extends ICollection_1$instance<TValue> {
    readonly count: int;
    contains(item: TValue): boolean;
    copyTo(array: TValue[], arrayIndex: int): void;
}
```

### Why $ Separator?

The `$` character was chosen because:
1. **Valid TypeScript identifier** - unlike `+` or `.`
2. **Visually distinct** - clearly indicates nesting relationship
3. **Not ambiguous** - `_` is used for generic arity (`List_1`)
4. **Consistent** - also used in `$instance` and `$views` suffixes

### Import Simplification

All types at namespace level simplifies imports:

```typescript
// User code - simple flat imports
import {
    List_1,
    List_1$Enumerator,
    Dictionary_2,
    Dictionary_2$KeyCollection,
    Dictionary_2$ValueCollection
} from "@dotnet/System.Collections.Generic";
```

### StableId for Nested Types

Nested types have StableIds that preserve the full path:

```
TypeStableId: System.Private.CoreLib:System.Collections.Generic.List`1+Enumerator
  → TsEmitName: List_1$Enumerator
```

The bindings.json maps TypeScript names back to CLR nested type paths.

## Multi-arity Families

### The Problem

.NET has type families with varying generic arity:
- `Action` (0 params) through `Action<T1,...,T16>` (16 params)
- `Func<TResult>` (1 param) through `Func<T1,...,T17>` (17 params)
- `Tuple<T1>` through `Tuple<T1,...,T8>`
- `ValueTuple` through `ValueTuple<T1,...,T8>`

These are separate CLR types but conceptually one "family". Users expect to write `Action<string, int>` not `Action_2<string, int>`.

### The Solution: Sentinel-Ladder Pattern

Multi-arity families use a unique sentinel symbol to detect which arity variant to dispatch to:

```typescript
// Sentinel symbol - uniquely identifies "unspecified" type parameter
declare const __unspecified: unique symbol;
export type __ = typeof __unspecified;

// Multi-arity facade with default sentinel values
export type Action<T1 = __, T2 = __, T3 = __> =
  [T1] extends [__] ? Internal.Action :           // Action()
  [T2] extends [__] ? Internal.Action_1<T1> :     // Action<T1>
  [T3] extends [__] ? Internal.Action_2<T1, T2> : // Action<T1, T2>
  Internal.Action_3<T1, T2, T3>;                  // Action<T1, T2, T3>
```

### How It Works

1. **Default sentinel**: Each type parameter defaults to `__` (the sentinel)
2. **Conditional dispatch**: `[T] extends [__]` checks if parameter was specified
3. **Ladder evaluation**: TypeScript evaluates top-to-bottom, first match wins
4. **Internal dispatch**: Routes to correct internal type with arity suffix

### Real Example: Func

```typescript
// System.d.ts (facade)
export type Func<T1 = __, T2 = __, TResult = __> =
  [T1] extends [__] ? never :                           // Func needs at least TResult
  [T2] extends [__] ? Internal.Func_1<T1> :             // Func<TResult>
  [TResult] extends [__] ? Internal.Func_2<T1, T2> :    // Func<T, TResult>
  Internal.Func_3<T1, T2, TResult>;                     // Func<T1, T2, TResult>

// Usage:
type MyCallback = Func<string>;           // → Func_1<string>
type MyMapper = Func<int, string>;        // → Func_2<int, string>
type MyReducer = Func<int, int, int>;     // → Func_3<int, int, int>
```

### Nested Constraint Guards

When internal types have generic constraints, the facade must verify constraints before dispatch:

```typescript
// SearchValues<T> requires T extends IEquatable<T>
export type SearchValues<T1 = __> =
  [T1] extends [__] ? Internal.SearchValues :
  [T1] extends [IEquatable_1<T1>] ? Internal.SearchValues_1<T1> : never;
```

For multi-parameter constraints, guards are nested:

```typescript
// JSType_Function<T1, T2, T3, T4> requires all T extends JSType
export type JSType_Function<T1 = __, T2 = __, T3 = __, T4 = __> =
  [T1] extends [__] ? Internal.JSType_Function :
  [T2] extends [__] ? [T1] extends [Internal.JSType] ? Internal.JSType_Function_1<T1> : never :
  [T3] extends [__] ? [T1] extends [Internal.JSType] ? [T2] extends [Internal.JSType] ? Internal.JSType_Function_2<T1, T2> : never : never :
  [T4] extends [__] ? [T1] extends [Internal.JSType] ? [T2] extends [Internal.JSType] ? [T3] extends [Internal.JSType] ? Internal.JSType_Function_3<T1, T2, T3> : never : never : never :
  [T1] extends [Internal.JSType] ? [T2] extends [Internal.JSType] ? [T3] extends [Internal.JSType] ? [T4] extends [Internal.JSType] ? Internal.JSType_Function_4<T1, T2, T3, T4> : never : never : never : never;
```

### Implementation Files

| File | Purpose |
|------|---------|
| `Emit/MultiArityFamilyDetect.cs` | Detects multi-arity families from CLR types |
| `Emit/MultiArityAliasEmit.cs` | Generates sentinel-ladder type aliases |
| `Emit/FamilyIndexEmitter.cs` | Emits `families.json` canonical family index |

### FacadeFamilyIndex

The `families.json` file provides a canonical index of multi-arity families for cross-package imports:

```json
{
  "System.Action": {
    "stem": "Action",
    "namespace": "System",
    "minArity": 0,
    "maxArity": 16,
    "isDelegate": true
  },
  "System.Func": {
    "stem": "Func",
    "namespace": "System",
    "minArity": 1,
    "maxArity": 17,
    "isDelegate": true
  }
}
```

This enables `ImportPlanner` to resolve cross-package multi-arity imports without runtime type inspection.

### Constraint Invariants

Three invariants are enforced by `test-facade-constraint-invariants.sh`:

1. **No `Internal.Internal.*`**: Double-qualification is never valid
2. **No `Internal.unknown/any/never`**: TypeScript built-ins shouldn't be qualified
3. **No `bigint` carrier**: Long/ULong use `number` aliases, not raw bigint
