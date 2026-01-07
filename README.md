# tsbindgen

**TypeScript type declaration generator for .NET assemblies**

tsbindgen generates TypeScript declaration files (`.d.ts`) from .NET assemblies using reflection. It creates fully-typed, IDE-friendly TypeScript definitions that map the entire .NET Base Class Library (BCL) to TypeScript.

## Features

- **Complete BCL coverage** - Generates declarations for all 130 BCL namespaces, 4,047 types
- **Zero TypeScript errors** - Output validates cleanly with `tsc --strict`
- **Nullable reference types** - NRT support for output positions (returns, properties, fields)
- **CLR primitives** - Numeric type aliases (`int`, `long`, `decimal`, etc.) via `@tsonic/core`
- **Dual naming modes** - CLR PascalCase (`GetEnumerator`) or JavaScript camelCase (`getEnumerator`)
- **Generic type preservation** - Full generic type parameter support with constraints
- **Metadata sidecars** - CLR-specific information (static, virtual, override, ref/out/in) in companion JSON files
- **Library mode** - Generate only your assembly's types, importing BCL types from pre-existing packages
- **Namespace mapping** - Customize output directory names with `--namespace-map`
- **Class flattening** - Export static class methods as top-level functions with `--flatten-class`

## Installation

### Via npm (recommended)

```bash
npm install tsbindgen
# or
npm install @tsonic/tsbindgen
```

Requires .NET 10 runtime installed.

### From source

```bash
git clone https://github.com/tsoniclang/tsbindgen
cd tsbindgen
dotnet build src/tsbindgen/tsbindgen.csproj
```

## Quick Start

### Generate BCL declarations

```bash
# Via npm
npx tsbindgen generate -d ~/.dotnet/shared/Microsoft.NETCore.App/10.0.0 -o ./output

# Via dotnet (from source)
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
  generate -d ~/.dotnet/shared/Microsoft.NETCore.App/10.0.0 -o ./output
```

### Generate for a custom assembly

```bash
# Via npm
npx tsbindgen generate -a ./MyLibrary.dll -d $DOTNET_RUNTIME -o ./output

# Via dotnet (from source)
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
  generate -a ./MyLibrary.dll -d $DOTNET_RUNTIME -o ./output
```

### Library mode (exclude BCL types)

```bash
# First, generate BCL types
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./bcl-types

# Then generate your library, importing BCL from the pre-existing package
npx tsbindgen generate -a ./MyLibrary.dll -d $DOTNET_RUNTIME -o ./my-lib --lib ./bcl-types
```

## CLI Reference

### Commands

| Command | Description |
|---------|-------------|
| `generate` | Generate TypeScript declarations from .NET assemblies |

### Generate Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--assembly` | `-a` | Path to assembly file(s) to process | - |
| `--assembly-dir` | `-d` | Directory containing .NET runtime assemblies | - |
| `--out-dir` | `-o` | Output directory for generated files | `out` |
| `--namespaces` | `-n` | Namespace filter (reserved; currently ignored) | (all) |
| `--naming` | - | Naming convention: `js` (camelCase) or `clr` (PascalCase) | `clr` |
| `--lib` | - | Path to pre-existing tsbindgen package (repeatable) | - |
| `--namespace-map` | - | Map CLR namespace to output name (repeatable) | - |
| `--flatten-class` | - | Flatten static class to top-level exports (repeatable) | - |
| `--verbose` | `-v` | Enable detailed progress output | false |
| `--logs` | - | Enable specific log categories (repeatable; space-separated values) | - |
| `--strict` | - | Enable strict mode validation | false |

### Examples

```bash
# Generate BCL with JavaScript naming
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out --naming js

# Verbose output with specific log categories
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out -v --logs ImportPlanner FacadeEmitter

# Strict mode (additional validation)
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out --strict
```

## Output Structure

tsbindgen emits a flat ESM facade per namespace plus a per-namespace directory for internals:

```
output/
├── families.json                      # Multi-arity family index
├── __internal/extensions/index.d.ts   # Extension method buckets
├── System.d.ts                        # Facade (public API)
├── System.js                          # Runtime stub (throws)
├── System/
│   ├── bindings.json
│   └── internal/
│       ├── index.d.ts                 # Full declarations
│       └── metadata.json              # CLR semantics
├── System.Collections.Generic.d.ts
├── System.Collections.Generic.js
└── System.Collections.Generic/
    ├── bindings.json
    └── internal/
        ├── index.d.ts
        └── metadata.json
```

### Output Files

| File | Description |
|------|-------------|
| `<Namespace>.d.ts` | Public facade with curated exports and friendly aliases |
| `<Namespace>.js` | Module stub that throws if executed in Node |
| `<Namespace>/internal/index.d.ts` | Full type declarations (`$instance` + `__views`) |
| `<Namespace>/internal/metadata.json` | CLR semantics (virtual/override, ref/out/in, etc.) |
| `<Namespace>/bindings.json` | CLR↔TS name mappings for the Tsonic compiler |

## Extension Methods (C# `using` Semantics)

tsbindgen collects C# extension methods into bucket helper types under `__internal/extensions/index.d.ts` and re-exports a per-namespace helper from each facade:

```ts
// System.Linq.d.ts
export type { ExtensionMethods_System_Linq as ExtensionMethods } from "./__internal/extensions/index.js";
```

Use the helper as a type-level “using” wrapper:

```ts
import type { ExtensionMethods as Linq } from "@tsonic/dotnet/System.Linq.js";
import type { IEnumerable } from "@tsonic/dotnet/System.Collections.Generic.js";

type LinqSeq<T> = Linq<IEnumerable<T>>;

declare const xs: LinqSeq<number>;
xs.where((x) => x > 0).select((x) => x * 2);
```

Naming mode applies to extension method member names too:

- `--naming clr`: `.Where(...)`, `.Select(...)`
- `--naming js`: `.where(...)`, `.select(...)`

## Type Mapping

### Primitive Types

CLR primitive types map to type aliases from `@tsonic/core`:

| CLR Type | TypeScript Type |
|----------|----------------|
| `System.Int32` | `int` (alias for `number`) |
| `System.Int64` | `long` (alias for `number`) |
| `System.Single` | `float` (alias for `number`) |
| `System.Double` | `double` (alias for `number`) |
| `System.Decimal` | `decimal` (alias for `number`) |
| `System.Byte` | `byte` (alias for `number`) |
| `System.Boolean` | `bool` (branded) |
| `System.String` | `string` |
| `System.Char` | `char` (branded) |

### Generic Types

Generic types use underscore suffix for arity:

| CLR Type | TypeScript Type |
|----------|----------------|
| `List<T>` | `List_1<T>` |
| `Dictionary<TKey, TValue>` | `Dictionary_2<TKey, TValue>` |
| `Func<T, TResult>` | `Func_2<T, TResult>` |

Friendly aliases are also exported:
```typescript
// Both work:
import { List_1 } from "@tsonic/dotnet/System.Collections.Generic.js";
import { List } from "@tsonic/dotnet/System.Collections.Generic.js";  // Friendly alias
```

### Type Kind Mapping

| CLR Kind | TypeScript Pattern |
|----------|-------------------|
| Class | `interface + const` (instance + static sides) |
| Struct | `interface + const` |
| Interface | `interface` |
| Enum | `const enum` |
| Delegate | `type` (function signature + CLR type intersection) |
| Static class | `abstract class` (static methods only) |

## Naming Conventions

### Default (CLR) Naming
```typescript
list.GetEnumerator();  // PascalCase
Console.WriteLine();   // PascalCase
```

### JavaScript Naming (`--naming js`)
```typescript
list.getEnumerator();  // camelCase
Console.writeLine();   // camelCase
```

## Testing

```bash
# Run all regression tests
bash test/scripts/run-all.sh

# Run validation (TypeScript compilation check)
node test/validate/validate.js

# Individual tests
bash test/scripts/test-strict-mode.sh
bash test/scripts/test-determinism.sh
bash test/scripts/test-surface-manifest.sh
bash test/scripts/test-lib.sh
```

## Development

### Project Structure

```
tsbindgen/
├── src/tsbindgen/
│   ├── Cli/                 # Command-line interface
│   ├── Load/                # Assembly loading and reflection
│   ├── Model/               # Symbol graph data structures
│   ├── Shape/               # Type transformation passes
│   ├── Normalize/           # Name reservation
│   ├── Plan/                # Import/export planning
│   ├── Emit/                # TypeScript file generation
│   └── Renaming/            # Name conflict resolution
├── test/
│   ├── scripts/             # Test scripts
│   ├── validate/            # Validation scripts
│   ├── baselines/           # Surface manifest baseline
│   └── fixtures/            # Test fixtures
└── docs/                    # Documentation
```

### Build Commands

```bash
# Build
dotnet build src/tsbindgen/tsbindgen.csproj

# Build release
dotnet build src/tsbindgen/tsbindgen.csproj -c Release

# Run
dotnet run --project src/tsbindgen/tsbindgen.csproj -- <args>
```

## Documentation

- [Docs Index](docs/README.md) - Getting started, CLI, troubleshooting
- [Architecture](docs/architecture/README.md) - Internal architecture documentation

## Related Projects

- **[@tsonic/dotnet](https://www.npmjs.com/package/@tsonic/dotnet)** - Pre-generated BCL types with JavaScript naming
- **[@tsonic/dotnet-pure](https://www.npmjs.com/package/@tsonic/dotnet-pure)** - Pre-generated BCL types with CLR naming
- **[@tsonic/core](https://www.npmjs.com/package/@tsonic/core)** - Tsonic runtime types and primitives

## License

MIT
