# Concepts

## Facade pattern

Generated packages separate:

- full internal declarations
- public facade exports

This keeps user imports stable while allowing internal structure to stay richer.

## Friendly aliases vs CLR identity

`tsbindgen` preserves CLR identity, but facades can also expose ergonomic export
names where that does not destroy determinism.

## Binding metadata

Type declarations alone are not enough. Some CLR semantics, such as parameter
modifiers and interop details, travel in binding metadata files alongside the
declarations.

## Library mode

Library mode is how tsbindgen avoids re-owning types that should come from
pre-existing binding packages.

That matters for packages built on top of:

- `@tsonic/dotnet`
- `@tsonic/aspnetcore`
- `@tsonic/microsoft-extensions`
- `@tsonic/efcore*`
