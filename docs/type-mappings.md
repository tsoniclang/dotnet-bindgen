# Type Mappings

`tsbindgen` projects CLR type shapes into TypeScript declarations.

## Goals

- preserve CLR identity
- keep generated names stable and predictable
- expose enough shape for Tsonic typechecking and imports

## Important examples

- CLR primitives map to `@tsonic/core/types.js` aliases where appropriate
- classes and structs emit as interface-plus-value patterns
- interfaces remain interface-only
- delegates emit as callable types
- enums emit as enum-style declaration surfaces

Examples:

- `System.Int32` -> `int`
- `System.Int64` -> `long`
- `List<T>` -> `List_1<T>`
- `Dictionary<TKey, TValue>` -> `Dictionary_2<TKey, TValue>`

## Why naming discipline matters

Generated packages must stay:

- stable enough for imports
- faithful enough for CLR identity
- predictable enough for large binding waves

That is why tsbindgen does not try to “JavaScript-ify” CLR naming.

## Important distinction

These mappings belong to generated binding packages. They do not define the
semantics of first-party authored source packages like `@tsonic/js`.
