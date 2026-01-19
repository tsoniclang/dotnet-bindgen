# CLI Reference

Complete reference for the tsbindgen command-line interface.

## Installation

```bash
# Via npm (wrapper, recommended)
npm install tsbindgen

# Or install globally
npm install -g tsbindgen

# Or install the scoped package directly
npm install @tsonic/tsbindgen
```

Requires .NET 10 runtime installed.

## Commands

### generate

Generate TypeScript declarations from .NET assemblies.

```bash
# Via npm
npx tsbindgen generate [options]

# Via dotnet (from source)
dotnet run --project src/tsbindgen/tsbindgen.csproj -- generate [options]
```

### resolve-closure

Resolve the transitive closure of assembly references and emit machine-readable JSON.

```bash
# Resolve closure for a local library (requires runtime dirs in --ref-dir)
npx tsbindgen resolve-closure -a ./MyLibrary.dll \
  --ref-dir $DOTNET_RUNTIME
```

`resolve-closure` options:

| Option | Description |
|--------|-------------|
| `--strict-versions <bool>` | Error on major version drift for the same assembly name. Default: `true`. |

## Options

### Assembly Input

| Option | Short | Description |
|--------|-------|-------------|
| `--assembly <path>` | `-a` | Path to assembly file. Can be specified multiple times. |
| `--assembly-dir <path>` | `-d` | Directory containing assemblies to process. |
| `--ref-dir <path>` | - | Additional directory to search for referenced assemblies (repeatable). |

At least one of `--assembly` or `--assembly-dir` is required.

**Examples:**

```bash
# Generate from runtime directory (BCL)
npx tsbindgen generate -d ~/.dotnet/shared/Microsoft.NETCore.App/10.0.0

# Generate from specific assembly
npx tsbindgen generate -a ./MyLibrary.dll -d $DOTNET_RUNTIME

# Multiple assemblies
npx tsbindgen generate -a ./Lib1.dll -a ./Lib2.dll -d $DOTNET_RUNTIME
```

### Output

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `--out-dir <path>` | `-o` | `out` | Output directory for generated files. |

**Example:**

```bash
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./declarations
```

### Filtering

| Option | Short | Description |
|--------|-------|-------------|
| `--namespaces <list>` | `-n` | Comma-separated list of namespaces to include. |

**Example:**

```bash
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out -n System,System.Linq
```

### Naming

tsbindgen emits **CLR-faithful names**. There are no casing transforms and no `--naming` flag.

### Library Mode

| Option | Description |
|--------|-------------|
| `--lib <path>` | Path to pre-existing tsbindgen package. Can be specified multiple times. |

See [Library Mode](library-mode.md) for details.

**Examples:**

```bash
# Single library reference
npx tsbindgen generate -a ./MyLib.dll -d $DOTNET_RUNTIME -o ./out \
  --lib node_modules/@tsonic/dotnet

# Multiple library references
npx tsbindgen generate -a ./MyLib.dll -d $DOTNET_RUNTIME -o ./out \
  --lib node_modules/@tsonic/dotnet \
  --lib node_modules/@tsonic/microsoft-extensions
```

### Namespace Mapping

| Option | Description |
|--------|-------------|
| `--namespace-map <mapping>` | Maps CLR namespace to different output name. Format: `CLRNamespace=outputName`. Can be specified multiple times. |

Use this to customize output directory names without changing the actual namespace in generated code.

**Examples:**

```bash
# Map System.Runtime.InteropServices to "interop"
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out \
  --namespace-map "System.Runtime.InteropServices=interop"

# Multiple mappings
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out \
  --namespace-map "System.Collections.Generic=collections" \
  --namespace-map "System.Threading.Tasks=async"
```

### Class Flattening

| Option | Description |
|--------|-------------|
| `--flatten-class <fullname>` | Flattens a static class to top-level function exports. Format: `Namespace.ClassName`. Can be specified multiple times. |

Static classes like `System.Console` or `System.Math` can be flattened so their methods become top-level exports instead of being accessed through a class.

**Examples:**

```bash
# Flatten Console class
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out \
  --flatten-class "System.Console"

# Result: export function WriteLine(...) instead of Console.WriteLine(...)

# Flatten multiple classes
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out \
  --flatten-class "System.Console" \
  --flatten-class "System.Math"
```

### Diagnostics

| Option | Short | Description |
|--------|-------|-------------|
| `--verbose` | `-v` | Enable detailed progress output. |
| `--logs <categories>` | - | Enable specific log categories. |
| `--strict` | - | Enable strict mode validation. |
| `--allow-constructor-constraint-loss` | - | Allow generic `new()` constraint loss (downgrade to warning). |

**Log categories:**

- `ImportPlanner` - Import statement planning
- `FacadeEmitter` - Facade file generation
- `InternalIndexEmitter` - Internal declaration generation
- `ViewPlanner` - Explicit interface view planning
- `StructuralConformance` - Interface conformance analysis
- `SafeToExtendAnalyzer` - LINQ assignability analysis

**Examples:**

```bash
# Verbose output
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out -v

# Specific log categories
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out --logs ImportPlanner FacadeEmitter

# Strict mode
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out --strict
```

## Full Example

```bash
# Complete example with all common options
npx tsbindgen generate \
  -d ~/.dotnet/shared/Microsoft.NETCore.App/10.0.0 \
  -o ./output \
  -v
```

## Environment Variables

| Variable | Description |
|----------|-------------|
| `DOTNET_RUNTIME` | Default runtime directory path |

**Example:**

```bash
export DOTNET_RUNTIME=~/.dotnet/shared/Microsoft.NETCore.App/10.0.0
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out
```
