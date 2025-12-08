# Type Mappings

How CLR types map to TypeScript declarations.

## Primitive Types

CLR primitives map to type aliases from `@tsonic/types`:

| CLR Type | TypeScript | Underlying |
|----------|-----------|------------|
| `System.SByte` | `sbyte` | `number` |
| `System.Byte` | `byte` | `number` |
| `System.Int16` | `short` | `number` |
| `System.UInt16` | `ushort` | `number` |
| `System.Int32` | `int` | `number` |
| `System.UInt32` | `uint` | `number` |
| `System.Int64` | `long` | `number` |
| `System.UInt64` | `ulong` | `number` |
| `System.Single` | `float` | `number` |
| `System.Double` | `double` | `number` |
| `System.Decimal` | `decimal` | `number` |
| `System.Char` | `char` | `string & { __brand: "char" }` |
| `System.Boolean` | `bool` | `boolean & { __brand: "bool" }` |
| `System.String` | `string` | (native) |
| `System.IntPtr` | `nint` | `number` |
| `System.UIntPtr` | `nuint` | `number` |

### Why Simple Aliases?

Numeric types are simple `number` aliases because TypeScript's structural typing doesn't enforce numeric bounds at runtime. Tsonic enforces numeric correctness at compile time via a proof system:

```typescript
const age: int = 42 as int;    // Tsonic validates 42 fits in Int32
const temp: float = 98.6 as float; // Tsonic validates for Single
```

The `char` and `bool` types remain branded for semantic distinction.

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

## Primitive Lifting in Generic Type Arguments

For generic type arguments, tsbindgen emits CLR type names directly instead of TS primitive aliases:

```typescript
// Value positions use TS aliases for ergonomics
function add(a: int, b: int): int;

// Generic type arguments use CLR names
type List = List_1<Int32>;  // Not List_1<int>
tryFormat(destination: Span_1<Char>): boolean;  // Not Span_1<char>
```

This ensures:
1. CLR type identity is preserved in type-level positions
2. Generic constraints are satisfied without runtime type inference
3. Tsonic compiler can enforce numeric correctness at compile time
