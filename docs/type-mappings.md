# Type Mappings

`dotnet-bindgen` projects CLR type shapes into TypeScript declarations.

## Goals

- preserve CLR identity
- keep generated names stable and predictable
- expose enough shape for Tsonic typechecking and imports

## Important examples

- CLR primitives map to `@tsonic/core/types.js` aliases where appropriate
- `System.Object` maps to TypeScript `unknown`
- value-type constraints map to `NonNullable<unknown>`
- classes and structs emit as interface-plus-value patterns
- interfaces remain interface-only
- delegates emit as callable types
- enums emit as enum-style declaration surfaces

Examples:

- `System.Int32` -> `int`
- `System.Int64` -> `long`
- `System.Object` -> `unknown`
- `System.ValueType` constraints -> `NonNullable<unknown>`
- `List<T>` -> `List_1<T>`
- `Dictionary<TKey, TValue>` -> `Dictionary_2<TKey, TValue>`

## Broad object slots

`unknown` is the public TypeScript projection for broad CLR object positions.
It communicates that a value exists but its useful shape is not proven at the
declaration boundary. Consumers must narrow, cast through an explicit API, or
pass the value to another broad slot.

Generated binding packages do not use `JsValue` for CLR object slots. `JsValue`
is a first-party JavaScript runtime declaration carrier, not the CLR-wide object
model.

## Generic constraints

Generic parameters are emitted with explicit TypeScript constraints so the
output remains valid under `tsc --strict`.

Examples:

- unconstrained CLR generic parameter -> `T extends unknown`
- class/object-like generic parameter -> `T extends unknown`
- struct/value-type generic parameter -> `T extends NonNullable<unknown>`

## Why naming discipline matters

Generated packages must stay:

- stable enough for imports
- faithful enough for CLR identity
- predictable enough for large binding waves

That is why dotnet-bindgen does not try to “JavaScript-ify” CLR naming.

## Important distinction

These mappings belong to generated binding packages. They do not define the
semantics of first-party authored source packages like `@tsonic/js`.
