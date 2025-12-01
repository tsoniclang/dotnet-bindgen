# tsbindgen Documentation

tsbindgen generates TypeScript declaration files from .NET assemblies.

## Table of Contents

### Getting Started
1. [Getting Started](getting-started.md) - Installation and first generation
2. [CLI Reference](cli.md) - Commands and options

### Type Generation
3. [Type Mappings](type-mappings.md) - How CLR types map to TypeScript
4. [Naming Conventions](naming.md) - CLR vs JavaScript naming
5. [Library Mode](library-mode.md) - Generating for custom assemblies

### Validation
6. [Testing](testing.md) - Validation and regression tests
7. [Troubleshooting](troubleshooting.md) - Common issues

## Quick Links

- [Architecture Documentation](architecture/README.md) - For contributors
- [GitHub Repository](https://github.com/tsoniclang/tsbindgen)

## Overview

### What is tsbindgen?

tsbindgen generates TypeScript declaration files (.d.ts) from .NET assemblies:

```
.NET Assembly (DLL) -> Reflection -> TypeScript Declarations (.d.ts)
```

### Why tsbindgen?

- **Complete BCL Coverage**: All 130 namespaces, 4,296 types, 50,675+ members
- **Type Safety**: Branded primitives, generic constraints preserved
- **IDE Support**: Full IntelliSense for .NET types in TypeScript
- **Dual Naming**: CLR PascalCase or JavaScript camelCase

### Output Example

```typescript
// System.Collections.Generic
export interface List_1<T> {
    readonly count: int;
    add(item: T): void;
    remove(item: T): boolean;
    clear(): void;
}

export declare const List_1: {
    new <T>(): List_1<T>;
    new <T>(capacity: int): List_1<T>;
};
```

## Prerequisites

- **.NET 10 SDK**: For assembly reflection
- **Node.js 18+**: For validation scripts

Verify installation:

```bash
dotnet --version  # 10.0.x
node --version    # v18.0.0 or higher
```

## Quick Start

```bash
# Clone and build
git clone https://github.com/tsoniclang/tsbindgen
cd tsbindgen
dotnet build src/tsbindgen/tsbindgen.csproj

# Generate BCL declarations
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
  generate -d ~/.dotnet/shared/Microsoft.NETCore.App/10.0.0 \
  -o ./output
```
