---
title: dotnet-bindgen
---

# dotnet-bindgen

`dotnet-bindgen` generates TypeScript declaration packages from .NET assemblies and
frameworks.

## Role in the stack

`dotnet-bindgen` is the generator for CLR binding packages. It is **not** the
source-of-truth for first-party authored packages like `@tsonic/js`,
`@tsonic/nodejs`, or `@tsonic/express`.

## Pages

- [Getting Started](getting-started.md)
- [CLI](cli.md)
- [What dotnet-bindgen Generates](what-dotnet-bindgen-generates.md)
- [Type Mappings](type-mappings.md)
- [Naming](naming.md)
- [Library Mode](library-mode.md)
- [Testing](testing.md)
- [Troubleshooting](troubleshooting.md)
- [Architecture](architecture/)
- [Workflow and Publish Discipline](workflow.md)

## Output families

Generated package families include:

- `@tsonic/dotnet`
- `@tsonic/aspnetcore`
- `@tsonic/microsoft-extensions`
- `@tsonic/efcore`
- `@tsonic/efcore-sqlite`
- `@tsonic/efcore-sqlserver`
- `@tsonic/efcore-npgsql`

## How to use generated binding packages

Use generated packages when you need CLR libraries that are not part of the
ambient surface or first-party authored source packages.

### ASP.NET Core

```bash
tsonic add framework Microsoft.AspNetCore.App @tsonic/aspnetcore
tsonic restore
```

```ts
import { WebApplication } from "@tsonic/aspnetcore/Microsoft.AspNetCore.Builder.js";
import type { ExtensionMethods } from "@tsonic/aspnetcore/Microsoft.AspNetCore.Builder.js";

export function main(): void {
  const builder = WebApplication.CreateBuilder();
  const app = builder.Build() as ExtensionMethods<WebApplication>;
  app.MapGet("/", () => "Hello");
  app.Run("http://localhost:8080");
}
```

### EF Core

```bash
tsonic add nuget Microsoft.EntityFrameworkCore.Sqlite 10.0.0
tsonic add npm @tsonic/efcore
tsonic add npm @tsonic/efcore-sqlite
tsonic restore
```

```ts
import { DbContext } from "@tsonic/efcore/Microsoft.EntityFrameworkCore.js";
import { SqliteDbContextOptionsBuilderExtensions } from "@tsonic/efcore-sqlite/Microsoft.EntityFrameworkCore.js";

export class AppDbContext extends DbContext {
}

export function configure(builder: any): void {
  SqliteDbContextOptionsBuilderExtensions.UseSqlite(builder, "Data Source=app.db");
}
```

These repos are generated binding packages, not first-party authored source
packages, so the docs focus on installation, import shape, and usage patterns
rather than package-by-package API narration.

## What makes it important

`dotnet-bindgen` sits at the boundary between CLR ecosystems and Tsonic authoring.
It is responsible for:

- reflecting CLR assemblies and frameworks
- generating TypeScript declarations and binding metadata
- maintaining publishable package structure for binding repos
- participating in release-wave preflight and publish discipline

## Ownership boundary

Authored first-party source packages and generated CLR binding packages have
different owners, inputs, package metadata, and validation gates. This section
keeps that distinction explicit.
