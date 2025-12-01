# Type Mappings

How CLR types map to TypeScript declarations.

## Primitive Types

CLR primitives map to branded TypeScript types from `@tsonic/types`:

| CLR Type | TypeScript | Underlying |
|----------|-----------|------------|
| `System.SByte` | `sbyte` | `number & { __brand: "sbyte" }` |
| `System.Byte` | `byte` | `number & { __brand: "byte" }` |
| `System.Int16` | `short` | `number & { __brand: "short" }` |
| `System.UInt16` | `ushort` | `number & { __brand: "ushort" }` |
| `System.Int32` | `int` | `number & { __brand: "int" }` |
| `System.UInt32` | `uint` | `number & { __brand: "uint" }` |
| `System.Int64` | `long` | `bigint & { __brand: "long" }` |
| `System.UInt64` | `ulong` | `bigint & { __brand: "ulong" }` |
| `System.Single` | `float` | `number & { __brand: "float" }` |
| `System.Double` | `double` | `number & { __brand: "double" }` |
| `System.Decimal` | `decimal` | `number & { __brand: "decimal" }` |
| `System.Char` | `char` | `string & { __brand: "char" }` |
| `System.Boolean` | `boolean` | (native) |
| `System.String` | `string` | (native) |
| `System.IntPtr` | `nint` | `number & { __brand: "nint" }` |
| `System.UIntPtr` | `nuint` | `number & { __brand: "nuint" }` |

### Why Branded Types?

Branded types provide type safety without runtime overhead:

```typescript
function processAge(age: int): void { ... }

const age: int = 25 as int;
processAge(age);     // OK
processAge(25);      // Error: number is not assignable to int
```

## Generic Types

Generic type names include arity suffix:

| CLR | TypeScript |
|-----|-----------|
| `List<T>` | `List_1<T>` |
| `Dictionary<K,V>` | `Dictionary_2<K, V>` |
| `Tuple<T1,T2,T3>` | `Tuple_3<T1, T2, T3>` |
| `Action` | `Action` |
| `Action<T>` | `Action_1<T>` |
| `Func<TResult>` | `Func_1<TResult>` |
| `Func<T,TResult>` | `Func_2<T, TResult>` |

### Friendly Aliases

Facades export friendly aliases without arity suffix:

```typescript
// Both work:
import { List_1 } from "@tsonic/dotnet/System.Collections.Generic";
import { List } from "@tsonic/dotnet/System.Collections.Generic";  // Alias
```

## Type Kinds

| CLR Kind | TypeScript Pattern |
|----------|-------------------|
| **Class** | `interface + const` |
| **Struct** | `interface + const` |
| **Interface** | `interface` |
| **Enum** | `const enum` |
| **Delegate** | `type` (callable) |
| **Static class** | `abstract class` |

### Class/Struct Pattern

Classes and structs emit as interface + const:

```typescript
// Instance interface
export interface List_1$instance<T> {
    readonly count: int;
    add(item: T): void;
}

// Value export (constructors + statics)
export declare const List_1: {
    new <T>(): List_1<T>;
    new <T>(capacity: int): List_1<T>;
};

// Views interface (explicit implementations)
export interface __List_1$views<T> {
    As_IEnumerable_1(): IEnumerable_1<T>;
}

// Combined type
export type List_1<T> = List_1$instance<T> & __List_1$views<T>;
```

### Interface Pattern

```typescript
export interface IEnumerable_1$instance<T> {
    getEnumerator(): IEnumerator_1<T>;
}

export type IEnumerable_1<T> = IEnumerable_1$instance<T>;
```

### Enum Pattern

```typescript
export const enum ConsoleColor {
    Black = 0,
    DarkBlue = 1,
    DarkGreen = 2,
    // ...
}
```

### Delegate Pattern

Delegates emit as callable types:

```typescript
export type Action_1<T> = ((arg: T) => void) & Action_1$instance<T> & __Action_1$views<T>;
export type Func_2<T, TResult> = ((arg: T) => TResult) & Func_2$instance<T, TResult>;
```

This allows arrow functions:

```typescript
const fn: Func_2<int, string> = (x) => x.toString();
```

### Static Class Pattern

```typescript
export abstract class Console {
    static writeLine(value: string): void;
    static readLine(): string;
    // No constructor - abstract
}
```

## Special Types

| CLR | TypeScript |
|-----|-----------|
| `void` | `void` |
| `object` | `unknown` |
| `dynamic` | `unknown` |
| `ref T` | `{ value: ref<T> }` |
| `out T` | `{ value: ref<T> }` |
| `T*` (pointer) | `ptr<T>` |

## Nullable Types

| CLR | TypeScript |
|-----|-----------|
| `int?` | `int \| null` |
| `string?` | `string \| null` |
| `T?` where T: struct | `T \| null` |

## Arrays

| CLR | TypeScript |
|-----|-----------|
| `T[]` | `T[]` |
| `T[,]` | `T[][]` |
| `ReadOnlySpan<T>` | `ReadOnlySpan_1<T>` |
| `Span<T>` | `Span_1<T>` |

## CLROf Utility

For generic constraints, `CLROf<T>` maps ergonomic primitives to CLR types:

```typescript
type CLROf<T> =
    T extends int ? Int32 :
    T extends string ? String :
    T extends boolean ? Boolean :
    T;
```

This enables:

```typescript
interface IEquatable_1<T> {
    equals(other: CLROf<T>): boolean;
}

// int satisfies IEquatable_1<int> because CLROf<int> = Int32
```
