---
title: CLI
---

# CLI

The `tsbindgen` CLI is used to generate binding packages from CLR inputs.

## Main commands

- `generate` — generate TypeScript bindings
- `resolve-closure` — inspect transitive assembly closure

## Typical usage

```bash
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
  generate -d /path/to/assemblies -o ./output
```

## Common options

- `--assembly <path>`
- `--assembly-dir <path>`
- `--ref-dir <path>`
- `--out-dir <path>`
- `--namespaces <list>`
- `--lib <path>`
- `--lib-type-override <ClrType=package>`
- `--namespace-map <mapping>`
- `--flatten-class <fullname>`
- `--strict`
- `--verbose`

## `resolve-closure`

```bash
npx tsbindgen resolve-closure -a ./MyLibrary.dll
```

Use this to inspect the resolved assembly closure without generating output.

## When `--lib` matters

Use `--lib` when you are generating a library package that should import
existing BCL or framework types from a pre-existing generated package instead of
re-emitting them.

That is how the stack keeps repos like `aspnetcore`, `efcore`, and
`microsoft-extensions` composable instead of duplicating the entire BCL surface.

## When to use repo scripts instead

For the first-party binding repos, prefer the repo-local generation and wave
publish scripts so versioning and package layout stay consistent.
