# Output Files

Each namespace generates multiple companion files.

## Directory Structure

```
output/
  System/
    internal/
      index.d.ts          # Full declarations
    index.d.ts            # Facade (re-exports)
    metadata.json         # CLR semantics
    bindings.json         # Name mappings
    typelist.json         # Emitted types (verification)
  System.Collections.Generic/
    internal/
      index.d.ts
    index.d.ts
    metadata.json
    bindings.json
    typelist.json
```

## File Purposes

### internal/index.d.ts

Full TypeScript declarations for all public types.

```typescript
import type { IEnumerable_1 } from "../../System.Collections.Generic/internal/index.js";

export interface List_1$instance<T> {
    readonly count: int;
    add(item: T): void;
}

export declare const List_1: {
    new <T>(): List_1<T>;
};

export type List_1<T> = List_1$instance<T> & __List_1$views<T>;
```

### index.d.ts (Facade)

Re-exports from internal plus friendly aliases.

```typescript
export * from "./internal/index.js";

// Friendly alias (no arity suffix)
export type List<T> = List_1<T>;
```

### metadata.json

CLR semantics not expressible in TypeScript. Used by Tsonic compiler.

```json
{
  "namespace": "System.Collections.Generic",
  "types": {
    "List_1": {
      "clrName": "List`1",
      "tsEmitName": "List_1",
      "kind": "class",
      "isSealed": false,
      "members": {
        "Add": {
          "kind": "method",
          "isVirtual": false,
          "isStatic": false
        },
        "Count": {
          "kind": "property",
          "canRead": true,
          "canWrite": false
        }
      },
      "intentionalOmissions": {
        "indexers": [{"signature": "Item[int]"}]
      }
    }
  }
}
```

**Key fields:**
- `isVirtual`, `isOverride`, `isAbstract` - Dispatch semantics
- `isStatic` - Static vs instance
- `canRead`, `canWrite` - Property accessors
- `intentionalOmissions` - Members skipped from .d.ts

### bindings.json

CLR-to-TypeScript name mappings. Used for runtime binding.

```json
{
  "namespace": "System.Linq",
  "types": [{
    "stableId": "System.Linq:System.Linq.Enumerable",
    "clrName": "System.Linq.Enumerable",
    "tsEmitName": "Enumerable",
    "methods": [{
      "clrName": "Where",
      "tsEmitName": "Where",
      "isExtensionMethod": true,
      "metadataToken": 100663496
    }]
  }]
}
```

**Key fields:**
- `stableId` - Unique identifier (assembly:fullName)
- `metadataToken` - CLR reflection token
- `isExtensionMethod` - C# extension method flag

### typelist.json

Flat list of emitted types/members. Used for completeness verification.

```json
{
  "types": {
    "List_1": {
      "clrName": "List`1",
      "tsEmitName": "List_1",
      "members": {
        "Add": {"tsEmitName": "Add", "emitScope": "ClassSurface"},
        "Count": {"tsEmitName": "Count", "emitScope": "ClassSurface"}
      }
    }
  }
}
```

## Import Conventions

### Internal to Internal

Relative paths between namespace internals:

```typescript
// From System.Linq/internal/index.d.ts
import type { IEnumerable_1 } from "../../System.Collections.Generic/internal/index.js";
```

### Facade to Internal

Always `./internal/index.js`:

```typescript
// From System.Linq/index.d.ts
export * from "./internal/index.js";
```

### Support Types

From `@tsonic/types` package:

```typescript
import type { ptr, ref } from "@tsonic/types";
```

## Naming Conventions

| CLR | TypeScript |
|-----|------------|
| Generic arity: `List`1` | Underscore: `List_1` |
| Nested type: `Outer+Inner` | Dollar: `Outer$Inner` |
| Namespace | Directory name (with dots) |

