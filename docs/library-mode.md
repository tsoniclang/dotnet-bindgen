# Library Mode

Library mode generates declarations for your assembly without duplicating BCL types.

## Use Case

When building a NuGet package with TypeScript bindings:
- BCL types come from `@tsonic/dotnet` (published package)
- Your types are generated fresh with references to BCL

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

1. **Skips BCL types** - Types from assemblies in `--lib` are not regenerated
2. **Generates imports** - Cross-references become import statements
3. **Validates references** - Ensures all referenced types exist in `--lib`

## Output Structure

Without `--lib`:
```
output/
+-- System/
+-- System.Collections.Generic/
+-- MyNamespace/
+-- ... (all namespaces)
```

With `--lib ./bcl-types`:
```
output/
+-- MyNamespace/
    +-- index.d.ts
    +-- internal/
        +-- index.d.ts
```

BCL references become imports:

```typescript
// In MyNamespace/internal/index.d.ts
import type { List_1 } from '@tsonic/dotnet/System.Collections.Generic';
import type { Exception } from '@tsonic/dotnet/System';

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
