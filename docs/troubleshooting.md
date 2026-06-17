# Troubleshooting

## A generated package compiles but consumers fail

That usually means the package shape is syntactically valid but semantically
wrong for the compiler or a downstream application. Treat it as a real bug in
the binding wave.

Common sources:

- wrong overload family ownership
- incorrect nullable projection
- incorrect import source in library mode
- binding metadata drift

## A first-party source package issue looks like a binding issue

Keep the ownership line clear:

- `dotnet-bindgen` owns generated binding packages
- `js`, `nodejs`, and `express` own their own source packages

## Closure resolution looks wrong

Use `resolve-closure` before debugging generation output itself. Missing or
mis-owned assemblies are often a closure input problem, not an emitter problem.
