# tsbindgen

**TypeScript type declaration generator for .NET assemblies**

tsbindgen generates TypeScript declaration files (`.d.ts`) from .NET assemblies using reflection. It creates fully-typed, IDE-friendly TypeScript definitions that map the entire .NET Base Class Library (BCL) to TypeScript.

## Features

- **Complete BCL coverage** - Generates declarations for all 130 BCL namespaces, 4,296 types, and 50,675+ members
- **Zero TypeScript errors** - Output validates cleanly with `tsc --strict`
- **Branded primitives** - Type-safe numeric types (`int`, `long`, `decimal`, etc.) via `@tsonic/types`
- **Dual naming modes** - CLR PascalCase (`GetEnumerator`) or JavaScript camelCase (`getEnumerator`)
- **Generic type preservation** - Full generic type parameter support with constraints
- **Metadata sidecars** - CLR-specific information (static, virtual, override) in companion JSON files
- **Library mode** - Generate only your assembly's types, importing BCL types from a pre-existing package

## Installation

```bash
# Clone and build
git clone https://github.com/tsoniclang/tsbindgen
cd tsbindgen
dotnet build src/tsbindgen/tsbindgen.csproj
```

## Quick Start

### Generate BCL declarations

```bash
# Generate TypeScript declarations for .NET BCL
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
  generate -d ~/.dotnet/shared/Microsoft.NETCore.App/10.0.0 \
  -o ./output
```

### Generate for a custom assembly

```bash
# Generate for your own assembly
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
  generate -a ./MyLibrary.dll \
  -d ~/.dotnet/shared/Microsoft.NETCore.App/10.0.0 \
  -o ./output
```

### Library mode (exclude BCL types)

```bash
# First, generate BCL types
dotnet run -- generate -d $DOTNET_RUNTIME -o ./bcl-types

# Then generate your library, importing BCL from the pre-existing package
dotnet run -- generate -a ./MyLibrary.dll -d $DOTNET_RUNTIME -o ./my-lib --lib ./bcl-types
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
| `--namespaces` | `-n` | Comma-separated namespace filter | (all) |
| `--naming` | - | Naming convention: `js` (camelCase) or `clr` (PascalCase) | `clr` |
| `--lib` | - | Path to pre-existing BCL types (library mode) | - |
| `--verbose` | `-v` | Enable detailed progress output | false |
| `--logs` | - | Enable specific log categories (comma-separated) | - |
| `--strict` | - | Enable strict mode validation | false |

### Examples

```bash
# Generate BCL with JavaScript naming
dotnet run -- generate -d $DOTNET_RUNTIME -o ./out --naming js

# Generate specific namespaces only
dotnet run -- generate -d $DOTNET_RUNTIME -o ./out -n System,System.Collections.Generic

# Verbose output with specific log categories
dotnet run -- generate -d $DOTNET_RUNTIME -o ./out -v --logs ImportPlanner,FacadeEmitter

# Strict mode (additional validation)
dotnet run -- generate -d $DOTNET_RUNTIME -o ./out --strict
```

## Output Structure

For each namespace, tsbindgen generates:

```
output/
├── System/
│   ├── index.d.ts              # Public facade (imports + re-exports)
│   └── internal/
│       └── index.d.ts          # Type declarations
├── System.Collections.Generic/
│   ├── index.d.ts
│   └── internal/
│       └── index.d.ts
└── ... (130 namespaces)
```

### Output Files

| File | Description |
|------|-------------|
| `index.d.ts` | Public facade with imports and friendly re-exports |
| `internal/index.d.ts` | Full type declarations with $instance pattern |

## Type Mapping

### Primitive Types

CLR primitive types map to branded TypeScript types from `@tsonic/types`:

| CLR Type | TypeScript Type |
|----------|----------------|
| `System.Int32` | `int` |
| `System.Int64` | `long` |
| `System.Single` | `float` |
| `System.Double` | `double` |
| `System.Decimal` | `decimal` |
| `System.Byte` | `byte` |
| `System.Boolean` | `boolean` |
| `System.String` | `string` |
| `System.Char` | `char` |

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
import { List_1 } from "@tsonic/dotnet/System.Collections.Generic";
import { List } from "@tsonic/dotnet/System.Collections.Generic";  // Friendly alias
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
console.WriteLine();   // PascalCase
```

### JavaScript Naming (`--naming js`)
```typescript
list.getEnumerator();  // camelCase
console.writeLine();   // camelCase
```

## Testing

```bash
# Run all regression tests
bash test/scripts/run-all.sh

# Run validation (TypeScript compilation check)
node test/validate/validate.js

# Run completeness verification
node test/validate/verify-completeness.js

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

- [User Guide](docs/user-guide.md) - Detailed usage instructions
- [Architecture](docs/architecture/) - Internal architecture documentation

## Related Projects

- **[@tsonic/dotnet](https://www.npmjs.com/package/@tsonic/dotnet)** - Pre-generated BCL types with JavaScript naming
- **[@tsonic/dotnet-pure](https://www.npmjs.com/package/@tsonic/dotnet-pure)** - Pre-generated BCL types with CLR naming
- **[@tsonic/types](https://www.npmjs.com/package/@tsonic/types)** - Branded primitive types

## License

MIT
