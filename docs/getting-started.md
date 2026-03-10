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

### Via npm (recommended)

```bash
# Wrapper package (recommended)
npm install tsbindgen

# Or install the scoped package directly
npm install @tsonic/tsbindgen
```

For using generated declarations, also install the core types package:

```bash
npm install @tsonic/core
```

Verify:

```bash
npx tsbindgen --help
```

### From Source

```bash
git clone https://github.com/tsoniclang/tsbindgen
cd tsbindgen
dotnet build src/tsbindgen/tsbindgen.csproj
```

Verify:

```bash
dotnet run --project src/tsbindgen/tsbindgen.csproj -- --help
```

## Generating BCL Declarations

### Find Your .NET Runtime

```bash
dotnet --list-runtimes
# Microsoft.NETCore.App 10.0.0 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
```

### Generate

```bash
# Via npm
npx tsbindgen generate -d /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0 -o ./output

# Via dotnet (from source)
dotnet run --project src/tsbindgen/tsbindgen.csproj -- \
  generate -d /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0 -o ./output
```

This generates declarations for all 130 BCL namespaces.

## Understanding the Output

After generation:

```
output/
+-- System.d.ts                 # Facade (public API)
+-- System.js                   # Runtime stub (throws)
+-- System/
|   +-- bindings.json
|   +-- internal/
|       +-- index.d.ts          # Full declarations
+-- System.Collections.Generic.d.ts
+-- System.Collections.Generic.js
+-- System.Collections.Generic/
|   +-- bindings.json
|   +-- internal/
|       +-- index.d.ts
+-- ... (130 namespaces)
```

### Facade (`<Namespace>.d.ts`)

The public-facing module consumers import from. Uses curated exports (no `export *`) to prevent leaking internal `$instance`/`$views` types:

```typescript
// output/System.Collections.Generic.d.ts
import * as Internal from './System.Collections.Generic/internal/index.js';

// Value re-exports for classes (friendly names)
export { List_1 as List } from './System.Collections.Generic/internal/index.js';

// Type aliases for interfaces
export type IEnumerable<T> = Internal.IEnumerable_1<T>;
```

### Internal (`<Namespace>/internal/index.d.ts`)

Full type declarations:

```typescript
// output/System.Collections.Generic/internal/index.d.ts
export interface List_1$instance<T> {
    readonly Count: int;
    Add(item: T): void;
    // ...
}

export declare const List_1: {
    new <T>(): List_1<T>;
};

export type List_1<T> = List_1$instance<T> & __List_1$views<T>;
```

## Generating a Custom Assembly (with Dependencies)

For normal SDK/runtime installs, tsbindgen automatically discovers the runtime
reference set. Add `--ref-dir` only when your assembly references extra DLLs
outside that standard runtime closure.

```bash
npx tsbindgen generate -a ./MyLibrary.dll -o ./my-lib-types

# Add extra ref dirs only for non-runtime dependencies
npx tsbindgen generate -a ./MyLibrary.dll -o ./my-lib-types \
  --ref-dir ./libs
```

To inspect resolution without generating, use `resolve-closure`:

```bash
npx tsbindgen resolve-closure -a ./MyLibrary.dll

# Add extra ref dirs only for non-runtime dependencies
npx tsbindgen resolve-closure -a ./MyLibrary.dll --ref-dir ./libs
```

## Validating Output

Run TypeScript validation:

```bash
node test/validate/validate.js --strict
```

Expected output:
```
VALIDATION RESULTS
Total errors: 0
✓ STRICT VALIDATION PASSED - Zero TypeScript errors!
```

## Next Steps

- [CLI Reference](cli.md) - All commands and options
- [Type Mappings](type-mappings.md) - How types are mapped
- [Naming & Identifiers](naming.md) - CLR-faithful names and TS-safe identifiers
- [Library Mode](library-mode.md) - Generate for custom assemblies
