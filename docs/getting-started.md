# Getting Started

`dotnet-bindgen` turns CLR assemblies into TypeScript declaration packages.

## Requirements

- .NET 10 SDK
- Node.js for surrounding validation scripts

## Install

```bash
npm install dotnet-bindgen
```

or:

```bash
npm install @tsonic/dotnet-bindgen
```

## Basic flow

```bash
dotnet build src/DotnetBindgen/DotnetBindgen.csproj
dotnet run --project src/DotnetBindgen/DotnetBindgen.csproj -- \
  generate -d /path/to/assemblies -o ./output
```

Or via npm:

```bash
npx dotnet-bindgen generate -d /path/to/assemblies -o ./output
```

For .NET runtime assemblies, derive the runtime path from the installed SDK:

```bash
DOTNET_RUNTIME=$(dirname "$(dotnet --list-runtimes | awk '/Microsoft.NETCore.App 10\\./ { print $3; exit }')")
npx dotnet-bindgen generate -d "$DOTNET_RUNTIME" -o ./output
```

## Typical output shape

Generated packages normally include:

- facade declaration files
- internal declaration trees
- binding metadata files
- runtime stub `.js` files where required by the package shape

## Use it for

- BCL and framework bindings
- third-party CLR packages
- internal CLR assemblies you want to expose to Tsonic

## When not to use it

Do not use `dotnet-bindgen` as the source generator for:

- `@tsonic/js`
- `@tsonic/nodejs`
- `@tsonic/express`

Those are authored first-party TypeScript source packages, not generated CLR
binding packages.
