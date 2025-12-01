# Key Concepts

This document explains the core concepts and patterns used in tsbindgen.

## Facade Pattern

### What is a Facade?

Each namespace generates two TypeScript files:
- `internal/index.d.ts` - Full declarations with all details
- `index.d.ts` - Facade that re-exports with friendly aliases

### Why Facades?

1. **Clean imports**: Users import from `@tsonic/dotnet/System` not `@tsonic/dotnet/System/internal/index.js`
2. **Friendly aliases**: `List<T>` instead of `List_1<T>`
3. **Encapsulation**: Internal structure can change without breaking imports

### Facade Structure

```typescript
// System.Collections.Generic/index.d.ts (Facade)
export * from "./internal/index.js";

// Friendly aliases (no arity suffix)
export type List<T> = List_1<T>;
export type Dictionary<TKey, TValue> = Dictionary_2<TKey, TValue>;
```

```typescript
// System.Collections.Generic/internal/index.d.ts (Full)
export interface List_1$instance<T> { ... }
export declare const List_1: { ... };
export type List_1<T> = List_1$instance<T> & __List_1$views<T>;
```

## Dual-Scope Naming

### The Problem

A class member and an interface member might have the same name after transformation:

```csharp
class MyClass : ICollection {
    public void Clear() { }              // Class member
    void ICollection.Clear() { }         // Explicit interface impl
}
```

Both become `clear` in JavaScript naming mode.

### The Solution

Maintain separate naming scopes:

1. **Class Surface Scope**: Names on the class itself
2. **View Scope**: Names in explicit interface views

```
Scope: type:MyClass#instance
  - clear (from class)

Scope: view:MyClass:ICollection#instance
  - clear (from ICollection)
```

Members in different scopes can have the same name without collision.

## Views and Explicit Interface Implementation

### What is a View?

A view provides access to interface members that don't appear on the class surface.

### Why Views?

C# explicit interface implementations aren't visible through the class type:

```csharp
class MyCollection : ICollection {
    void ICollection.CopyTo(Array array, int index) { }  // Explicit
}

// C# usage:
var coll = new MyCollection();
coll.CopyTo(...);           // Error! Not visible
((ICollection)coll).CopyTo(...);  // OK - cast to interface
```

### TypeScript Solution

```typescript
interface MyCollection$instance {
    // No CopyTo here - it's explicit
}

interface __MyCollection$views {
    As_ICollection(): ICollection;  // View accessor
}

type MyCollection = MyCollection$instance & __MyCollection$views;

// Usage:
const coll: MyCollection = ...;
coll.As_ICollection().CopyTo(...);  // Access via view
```

## EmitScope

### What is EmitScope?

EmitScope determines where a member appears in TypeScript output:

| EmitScope | Where it Appears |
|-----------|------------------|
| ClassSurface | On `$instance` interface |
| StaticSurface | On static `const` declaration |
| ViewOnly | Only in `__$views` interface |
| Omitted | Not emitted (tracked in metadata) |

### How EmitScope is Decided

Shape passes analyze each member:

1. **Instance methods** -> ClassSurface
2. **Static methods** -> StaticSurface
3. **Explicit interface impls** -> ViewOnly
4. **Conflicting indexers** -> Omitted
5. **Generic static members** -> Omitted (TypeScript limitation)

## StableId

### What is StableId?

A unique identifier for a type or member that survives all transformations.

### Why StableId?

During Shape passes, types and members get copied, modified, and renamed. We need a stable way to track identity:

```
Original: System.Collections.Generic.List`1.Add
After rename: List_1.add
After transform: List_1$instance.add

StableId stays the same: "System.Private.CoreLib:System.Collections.Generic.List`1::Add(T):void"
```

### StableId Format

**Type**: `AssemblyName:ClrFullName`
```
System.Private.CoreLib:System.Collections.Generic.List`1
```

**Member**: `DeclaringType::Signature (MetadataToken)`
```
System.Collections.Generic.List`1::Add(T):void (100663296)
```

## Type Emission Pattern

### The Three-Part Pattern

Classes and structs emit as three parts:

```typescript
// 1. Instance interface (properties and methods)
export interface List_1$instance<T> {
    readonly count: int;
    add(item: T): void;
}

// 2. Static/constructor declaration
export declare const List_1: {
    new <T>(): List_1<T>;
    new <T>(capacity: int): List_1<T>;
};

// 3. Combined type alias
export type List_1<T> = List_1$instance<T> & __List_1$views<T>;
```

### Why This Pattern?

1. **Separation of concerns**: Instance members vs constructors/statics
2. **TypeScript compatibility**: `const` for runtime value, `interface` for type
3. **View composition**: Type alias combines instance + views

## Delegate Callable Pattern

### The Problem

C# delegates are callable types:

```csharp
Action<int> callback = (x) => Console.WriteLine(x);
callback(42);  // Invoke delegate
```

### TypeScript Solution

Delegates emit as intersection of callable + instance:

```typescript
export type Action_1<T> =
    ((arg: T) => void) &           // Callable signature
    Action_1$instance<T> &         // Instance members (Target, Method, etc.)
    __Action_1$views<T>;           // View accessors

// Usage:
const callback: Action_1<int> = (x) => console.log(x);
callback(42);  // Works - callable
```

## Property Covariance

### The Problem

C# allows covariant property overrides:

```csharp
class Base {
    public virtual object Value { get; }
}

class Derived : Base {
    public override string Value { get; }  // More specific type
}
```

TypeScript doesn't support property overloading.

### The Solution

Emit union type covering all types in hierarchy:

```typescript
interface Derived$instance extends Base$instance {
    readonly value: string | object;  // Union
}
```

This causes ~12 TS2417 warnings in BCL (documented, expected).

## Extension Methods

### The Problem

C# extension methods appear as instance methods:

```csharp
// Definition
public static class Enumerable {
    public static IEnumerable<T> Where<T>(this IEnumerable<T> source, ...) { }
}

// Usage
var filtered = list.Where(x => x > 0);  // Looks like instance method
```

### TypeScript Solution

Use TypeScript declaration merging:

```typescript
// System.Linq.extensions/IEnumerable_1.d.ts
declare module "../System.Collections.Generic/internal/index.js" {
    interface IEnumerable_1<T> {
        where(predicate: Func_2<T, boolean>): IEnumerable_1<T>;
        select<TResult>(selector: Func_2<T, TResult>): IEnumerable_1<TResult>;
    }
}
```

Extension methods are "bucketed" by their first parameter type.

## Honest Emission

### The Problem

Not all C# interfaces can be safely used in TypeScript `extends`:

```typescript
// This might cause TS2430 if there are conflicting members
interface MyClass extends IFoo, IBar { }
```

### The Solution

"Honest emission" analyzes which interfaces are safe:

1. **Safe interfaces**: Go in `extends` clause
2. **Unsafe interfaces**: Become views instead

```typescript
// Before analysis - might cause errors
interface MyClass extends IFoo, IBar, IBaz { }

// After honest emission
interface MyClass extends IFoo {  // Only safe ones
}

interface __MyClass$views {
    As_IBar(): IBar;   // Unsafe ones become views
    As_IBaz(): IBaz;
}
```

## SCC Buckets (Strongly Connected Components)

### The Problem

Circular namespace dependencies cause TypeScript errors:

```
System.Collections.Generic -> System.Linq
System.Linq -> System.Collections.Generic
```

### The Solution

Group mutually-dependent namespaces into SCC buckets:

```
Bucket 1: [System.Collections.Generic, System.Linq]
  - Types within bucket can reference each other
  - Single combined module for the bucket
```

## CLROf Lifting

### The Problem

Generic constraints in C# reference CLR types:

```csharp
interface IEquatable<T> {
    bool Equals(T other);
}

// int implements IEquatable<int>
// But in TS, we use branded type 'int', not 'Int32'
```

### The Solution

`CLROf<T>` maps branded primitives to their CLR types:

```typescript
type CLROf<T> =
    T extends int ? Int32 :
    T extends string ? String :
    T extends boolean ? Boolean :
    T;

// Now works:
interface IEquatable_1<T> {
    equals(other: CLROf<T>): boolean;
}
```

## Nested Type Flattening

### The Problem

C# nested types use `+` separator:

```csharp
public class List<T> {
    public struct Enumerator { }  // List`1+Enumerator
}
```

### The Solution

Flatten with `$` separator:

```typescript
// List_1$Enumerator is at namespace level, not nested
export interface List_1$Enumerator<T> { ... }
```

This keeps all types at namespace scope for simpler imports.

