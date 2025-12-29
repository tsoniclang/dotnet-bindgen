# Output Files

Each namespace generates multiple companion files.

## Directory Structure

```
output/
  families.json           # Multi-arity family index (root)
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

Provides curated exports with friendly aliases. **No `export *`** is used to prevent leaking internal `$instance` and `$views` types.

```typescript
import * as Internal from './internal/index.js';

// Public API exports (curated - no export *)
// Value re-exports for classes
export { List_1 as List } from './internal/index.js';

// Type aliases for interfaces
export type IEnumerable<T> = Internal.IEnumerable_1<T>;
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
        "TryGetValue": {
          "kind": "method",
          "isVirtual": false,
          "isStatic": false,
          "parameterModifiers": [null, "out"]
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
- `parameterModifiers` - Array of ref/out/in modifiers per parameter (null = none)
- `intentionalOmissions` - Members skipped from .d.ts

### Parameter Modifiers

The `parameterModifiers` array tracks C# `ref`, `out`, and `in` parameter modifiers:

```json
{
  "TryParse": {
    "kind": "method",
    "parameterModifiers": [null, "out"]
  },
  "Exchange": {
    "kind": "method",
    "parameterModifiers": ["ref", "ref"]
  },
  "ReadSpan": {
    "kind": "method",
    "parameterModifiers": ["in"]
  }
}
```

- `null` - Normal by-value parameter
- `"ref"` - By-reference, must be initialized
- `"out"` - By-reference, assigned by method
- `"in"` - By-reference, read-only

This metadata is required by the Tsonic compiler for correct C# interop since TypeScript has no concept of by-reference parameters.

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

### families.json

Canonical index of multi-arity families for cross-package imports.

```json
{
  "System.Action": {
    "stem": "Action",
    "namespace": "System",
    "minArity": 0,
    "maxArity": 16,
    "isDelegate": true
  },
  "System.Func": {
    "stem": "Func",
    "namespace": "System",
    "minArity": 1,
    "maxArity": 17,
    "isDelegate": true
  }
}
```

**Key fields:**
- `stem` - Base name without arity suffix
- `namespace` - CLR namespace containing the family
- `minArity` / `maxArity` - Range of generic parameter counts
- `isDelegate` - Whether the family members are delegate types

Used by `ImportPlanner` for drift-proof multi-arity family resolution across packages.

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

Facades import from `./internal/index.js` but use curated exports (no `export *`):

```typescript
// From System.Linq/index.d.ts
import * as Internal from './internal/index.js';
export { Enumerable } from './internal/index.js';
export type IQueryable<T> = Internal.IQueryable_1<T>;
```

### Support Types

From `@tsonic/core` package:

```typescript
import type { ptr, ref } from "@tsonic/core/types.js";
```

## Naming Conventions

| CLR | TypeScript |
|-----|------------|
| Generic arity: `List`1` | Underscore: `List_1` |
| Nested type: `Outer+Inner` | Dollar: `Outer$Inner` |
| Namespace | Directory name (with dots) |

