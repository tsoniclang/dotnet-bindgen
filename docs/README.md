---
title: tsbindgen
---

# tsbindgen

`tsbindgen` generates TypeScript declaration packages from .NET assemblies and
frameworks.

## Current role in the stack

`tsbindgen` is the generator for CLR binding packages. It is **not** the
source-of-truth for first-party authored packages like `@tsonic/js`,
`@tsonic/nodejs`, or `@tsonic/express`.

## Pages

- [Getting Started](getting-started.md)
- [CLI](cli.md)
- [What tsbindgen Generates](what-tsbindgen-generates.md)
- [Type Mappings](type-mappings.md)
- [Naming](naming.md)
- [Library Mode](library-mode.md)
- [Testing](testing.md)
- [Troubleshooting](troubleshooting.md)
- [Architecture](architecture/)
- [Workflow and Publish Discipline](workflow.md)

## Output families

The current generated package families include:

- `@tsonic/dotnet`
- `@tsonic/aspnetcore`
- `@tsonic/microsoft-extensions`
- `@tsonic/efcore`
- `@tsonic/efcore-sqlite`
- `@tsonic/efcore-sqlserver`
- `@tsonic/efcore-npgsql`

## What makes it important

`tsbindgen` sits at the boundary between CLR ecosystems and Tsonic authoring.
It is responsible for:

- reflecting CLR assemblies and frameworks
- generating TypeScript declarations and binding metadata
- maintaining publishable package structure for binding repos
- participating in release-wave preflight and publish discipline

## Why this section exists

The older site blurred authored packages and generated binding packages
together. That is now a source of confusion. This section keeps the distinction
explicit.
