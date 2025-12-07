# CLI Reference

Complete reference for the tsbindgen command-line interface.

## Installation

```bash
# Via npm (recommended)
npm install tsbindgen

# Or install globally
npm install -g tsbindgen
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

## Options

### Assembly Input

| Option | Short | Description |
|--------|-------|-------------|
| `--assembly <path>` | `-a` | Path to assembly file. Can be specified multiple times. |
| `--assembly-dir <path>` | `-d` | Directory containing .NET runtime assemblies. |

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
| `--namespaces <list>` | `-n` | Comma-separated namespace filter. |

**Example:**

```bash
# Only generate System and System.Collections.Generic
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out \
  -n System,System.Collections.Generic
```

### Naming

| Option | Values | Default | Description |
|--------|--------|---------|-------------|
| `--naming` | `js`, `clr` | `clr` | Member naming convention. |

**Values:**

- `clr`: PascalCase (C# convention)
  ```typescript
  list.GetEnumerator();
  Console.WriteLine("hello");
  ```

- `js`: camelCase (JavaScript convention)
  ```typescript
  list.getEnumerator();
  console.writeLine("hello");
  ```

**Example:**

```bash
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out --naming js
```

### Library Mode

| Option | Description |
|--------|-------------|
| `--lib <path>` | Path to pre-existing BCL declarations. |

See [Library Mode](library-mode.md) for details.

**Example:**

```bash
npx tsbindgen generate -a ./MyLib.dll -d $DOTNET_RUNTIME -o ./out --lib ./bcl-types
```

### Diagnostics

| Option | Short | Description |
|--------|-------|-------------|
| `--verbose` | `-v` | Enable detailed progress output. |
| `--logs <categories>` | - | Enable specific log categories. |
| `--strict` | - | Enable strict mode validation. |

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
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out --logs ImportPlanner,FacadeEmitter

# Strict mode
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out --strict
```

## Full Example

```bash
# Complete example with all common options
npx tsbindgen generate \
  -d ~/.dotnet/shared/Microsoft.NETCore.App/10.0.0 \
  -o ./output \
  --naming js \
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
