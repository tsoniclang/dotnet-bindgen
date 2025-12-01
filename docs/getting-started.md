# Getting Started

This guide walks you through installing tsbindgen and generating your first declarations.

## Prerequisites

### .NET 10 SDK

Download from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0):

```bash
# Linux (Ubuntu/Debian)
sudo apt-get install dotnet-sdk-10.0

# macOS
brew install dotnet-sdk

# Verify
dotnet --version
```

### Node.js 18+ (for validation)

```bash
node --version  # v18.0.0 or higher
```

## Installation

### Building from Source

```bash
git clone https://github.com/tsoniclang/tsbindgen
cd tsbindgen
dotnet build src/tsbindgen/tsbindgen.csproj
```

Verify:

```bash
dotnet run --project src/tsbindgen/tsbindgen.csproj -- --help
```

### Release Build

For better performance:

```bash
dotnet build src/tsbindgen/tsbindgen.csproj -c Release
```

## Generating BCL Declarations

### Find Your .NET Runtime

```bash
dotnet --list-runtimes
# Microsoft.NETCore.App 10.0.0 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
```

### Generate

```bash
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
  generate \
  -d /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0 \
  -o ./output
```

This generates declarations for all 130 BCL namespaces.

## Understanding the Output

After generation:

```
output/
+-- System/
|   +-- index.d.ts              # Public facade
|   +-- internal/
|       +-- index.d.ts          # Full declarations
+-- System.Collections.Generic/
|   +-- index.d.ts
|   +-- internal/
|       +-- index.d.ts
+-- ... (130 namespaces)
```

### Facade (index.d.ts)

The public-facing module consumers import from:

```typescript
// Imports and re-exports
import * as Internal from './internal/index.js';
export * from './internal/index.js';

// Friendly aliases
export type List<T> = Internal.List_1<T>;
```

### Internal (internal/index.d.ts)

Full type declarations:

```typescript
export interface List_1$instance<T> {
    readonly count: int;
    add(item: T): void;
    // ...
}

export declare const List_1: {
    new <T>(): List_1<T>;
};

export type List_1<T> = List_1$instance<T> & __List_1$views<T>;
```

## Validating Output

Run TypeScript validation:

```bash
node test/validate/validate.js
```

Expected output:
```
Generated 130 namespaces
0 syntax errors
16 semantic errors (expected - property covariance)
```

## Next Steps

- [CLI Reference](cli.md) - All commands and options
- [Type Mappings](type-mappings.md) - How types are mapped
- [Naming Conventions](naming.md) - CLR vs JavaScript naming
- [Library Mode](library-mode.md) - Generate for custom assemblies
