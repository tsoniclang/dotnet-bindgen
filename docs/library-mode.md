# Library Mode

Library mode generates declarations for your assembly without duplicating types from existing packages.

## Use Case

When building a TypeScript package with .NET bindings:
- BCL types come from `@tsonic/dotnet` (published package)
- Runtime types come from `@tsonic/core` (Tsonic runtime primitives)
- Your types are generated fresh with references to these packages

## Workflow

### Step 1: Have BCL Types

Either generate BCL or use the published package:

```bash
# Option A: Generate BCL yourself
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./bcl-types

# Option B: Use published package
npm install @tsonic/dotnet
# Types are in node_modules/@tsonic/dotnet
```

### Step 2: Generate Your Library

```bash
npx tsbindgen generate \
  -a ./MyLibrary.dll \
  -d $DOTNET_RUNTIME \
  -o ./my-lib-types \
  --lib ./bcl-types
```

## What --lib Does

1. **Skips library types** - Types from assemblies in `--lib` packages are not regenerated
2. **Generates imports** - Cross-references become import statements to the library package
3. **Validates references** - Ensures all referenced types exist in one of the `--lib` packages
4. **Merges contracts** - Multiple `--lib` options combine their type registries

## Multiple Library References

You can specify `--lib` multiple times to reference several packages:

```bash
npx tsbindgen generate \
  -a ./MyLibrary.dll \
  -d $DOTNET_RUNTIME \
  -o ./my-lib-types \
  --lib ./node_modules/@tsonic/dotnet \
  --lib ./node_modules/@tsonic/core
```

When a type is referenced, tsbindgen checks each library in order and imports from the first one that provides it. This allows layered package structures:

- `@tsonic/core` - Tsonic runtime types (primitives, Array, etc.)
- `@tsonic/dotnet` - .NET BCL types (references `@tsonic/core` for primitives)
- Your package - Your types (references both)

## Output Structure

Without `--lib`:
```
output/
+-- System.d.ts
+-- System.js
+-- System/
+-- System.Collections.Generic.d.ts
+-- System.Collections.Generic.js
+-- System.Collections.Generic/
+-- MyNamespace.d.ts
+-- MyNamespace.js
+-- MyNamespace/
+-- ... (all emitted namespaces)
```

With `--lib ./bcl-types`:
```
output/
+-- MyNamespace.d.ts
+-- MyNamespace.js
+-- MyNamespace/
    +-- bindings.json
    +-- internal/
        +-- index.d.ts
        +-- metadata.json
```

BCL references become imports:

```typescript
// In MyNamespace/internal/index.d.ts
import type { List_1 } from '@tsonic/dotnet/System.Collections.Generic.js';
import type { Exception } from '@tsonic/dotnet/System.js';

export interface MyClass$instance {
    getItems(): List_1<string>;
    getError(): Exception;
}
```

## Example: Custom Library

### Your C# Library

```csharp
// MyLibrary/MyClass.cs
namespace MyCompany.Utils
{
    public class DataProcessor
    {
        public List<string> Process(string[] items) { ... }
        public void HandleError(Exception ex) { ... }
    }
}
```

### Generate

```bash
npx tsbindgen generate \
  -a ./MyLibrary.dll \
  -d $DOTNET_RUNTIME \
  -o ./my-lib-types \
  --lib ./node_modules/@tsonic/dotnet
```

### Output

```typescript
// my-lib-types/MyCompany.Utils/internal/index.d.ts
import type { List_1 } from '@tsonic/dotnet/System.Collections.Generic';
import type { Exception } from '@tsonic/dotnet/System';

export interface DataProcessor$instance {
    process(items: string[]): List_1<string>;
    handleError(ex: Exception): void;
}

export declare const DataProcessor: {
    new (): DataProcessor;
};

export type DataProcessor = DataProcessor$instance;
```

## Validation Errors

Library mode performs strict validation:

| Error | Description | Fix |
|-------|-------------|-----|
| `LIB001` | Type not found in library | Ensure BCL is complete |
| `LIB002` | Member signature mismatch | Regenerate with same options |
| `LIB003` | Generic constraint mismatch | Check constraint compatibility |

## Best Practices

1. **Use same --naming** - Match the naming mode of your BCL package
2. **Keep BCL updated** - Regenerate when .NET version changes
3. **Pin versions** - Use specific @tsonic/dotnet version in package.json
