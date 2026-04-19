---
title: What tsbindgen Generates
---

# What tsbindgen Generates

`tsbindgen` reflects CLR metadata and emits TypeScript declaration packages.

## Typical flow

```text
CLR assemblies / shared frameworks
  -> reflection
  -> TypeScript declarations and binding metadata
  -> publishable npm packages
```

## Output repos

Current generated repos include:

- `dotnet`
- `aspnetcore`
- `microsoft-extensions`
- `efcore`
- `efcore-sqlite`
- `efcore-sqlserver`
- `efcore-npgsql`

## Package shape

Generated packages expose CLR namespaces as importable TypeScript modules. For
example:

```ts
import { Console } from "@tsonic/dotnet/System.js";
import { WebApplication } from "@tsonic/aspnetcore/Microsoft.AspNetCore.Builder.js";
```

Typical output structure looks like:

```text
output/
  System.d.ts
  System.js
  System/
    bindings.json
    internal/index.d.ts
```

That gives Tsonic both:

- a public TypeScript import surface
- binding metadata required for CLR-aware compilation

## What the metadata is for

The public `.d.ts` files are only part of the story.

The generated metadata is what lets the compiler reason about CLR-specific
concepts such as:

- overload families
- extension methods
- nullable/reference semantics
- generic constraints
- namespace/module ownership

## How generated repos are meant to be consumed

Generated repos such as `aspnetcore` and `efcore*` are not documented like
first-party source packages. The important user-facing questions are:

- which CLR dependency to add
- which npm binding package to install
- which namespace module to import
- what a minimal working example looks like

### Example: ASP.NET Core

```bash
tsonic add framework Microsoft.AspNetCore.App @tsonic/aspnetcore
tsonic restore
```

```ts
import { WebApplication } from "@tsonic/aspnetcore/Microsoft.AspNetCore.Builder.js";
```

### Example: EF Core + SQLite

```bash
tsonic add nuget Microsoft.EntityFrameworkCore.Sqlite 10.0.0
tsonic add npm @tsonic/efcore
tsonic add npm @tsonic/efcore-sqlite
tsonic restore
```

```ts
import { DbContext } from "@tsonic/efcore/Microsoft.EntityFrameworkCore.js";
import { SqliteDbContextOptionsBuilderExtensions } from "@tsonic/efcore-sqlite/Microsoft.EntityFrameworkCore.js";
```

## What it does not generate

The current stack does **not** treat `tsbindgen` as the source generator for:

- `@tsonic/js`
- `@tsonic/nodejs`
- `@tsonic/express`

Those are first-party TypeScript source packages with their own
`tsonic.package.json` manifests.
