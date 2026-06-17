# Naming

`dotnet-bindgen` favors CLR-faithful naming over cosmetic translation.

## Why

Generated binding packages are interop surfaces. Predictability against CLR
names is more important than JavaScript-style renaming.

## Result

Import paths and type names follow CLR namespace and member structure closely.

Examples:

- `System.Text.Json` stays `System.Text.Json`
- `List<T>` becomes `List_1<T>` when arity needs to stay explicit
- friendly aliases may exist, but CLR identity still wins
