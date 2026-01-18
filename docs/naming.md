# Naming & Identifiers

tsbindgen emits **CLR-faithful names**. There are no casing transforms (no camelCase/PascalCase modes).

## Member names (methods / properties / fields)

- Members keep their **CLR name** exactly.
- This is required for “airplane-grade” correctness: casing transforms can mask real collisions and can silently redirect calls.

```ts
list.GetEnumerator();
Console.WriteLine("hello");
String.IsNullOrEmpty(s);
Array.Sort(array);
```

If you want JavaScript-style names, define them in the library itself (e.g. `console.log` in `@tsonic/js`, `fs.readFile` in `@tsonic/nodejs`). tsbindgen will emit those names as-is because they are the CLR names.

## Type names

Type names are derived deterministically from CLR names to be valid TS identifiers:

- Generic arity: ``List`1`` → `List_1`
- Nested types: `Outer+Inner` → `Outer_Inner`

Examples:

```ts
export type List_1<T> = List_1$instance<T> & __List_1$views<T>;
export type Dictionary_2<TKey, TValue> = Dictionary_2$instance<TKey, TValue> & __Dictionary_2$views<TKey, TValue>;
```

## Reserved words

Type names and binding identifiers (vars/params) are sanitized in contexts where TS forbids them.

- Type names: `type string = ...` is illegal, so `string` would become `string_` if it ever appeared as a type alias name.
- Parameter names: `switch` becomes `switch_`.

Member names (methods/properties) are emitted in `IdentifierName` positions, so keywords are allowed and are emitted as-is.

## Collisions

If the CLR surface contains a collision that TypeScript cannot represent directly (e.g., same name used for incompatible member kinds), tsbindgen resolves it deterministically using numeric suffixes (`Foo`, `Foo2`, ...).

These suffixes are a last resort and are validated so they do not leak onto “normal” surfaces unless unavoidable.

