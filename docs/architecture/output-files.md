# Output Files

Each namespace generates multiple companion files.

## Directory Structure

```
output/
  families.json           # Multi-arity family index (root)
  __internal/
    extensions/
      index.d.ts          # Extension method buckets
  System.d.ts             # Facade (re-exports)
  System.js               # Runtime stub (throws if executed)
  System/
    bindings.json         # CLR bindings manifest (names + CLR semantics)
    internal/
      index.d.ts          # Full declarations
  System.Collections.Generic.d.ts
  System.Collections.Generic.js
  System.Collections.Generic/
    bindings.json
    internal/
      index.d.ts
```

## File Purposes

### internal/index.d.ts

Full TypeScript declarations for all public types.

```typescript
import type { IEnumerable_1 } from "../../System.Collections.Generic/internal/index.js";

export interface List_1$instance<T> {
    readonly Count: int;
    Add(item: T): void;
}

export declare const List_1: {
    new <T>(): List_1<T>;
};

export type List_1<T> = List_1$instance<T> & __List_1$views<T>;
```

### <Namespace>.d.ts (Facade)

Provides curated exports with friendly aliases. **No `export *`** is used to prevent leaking internal `$instance` and `$views` types.

```typescript
// System.Collections.Generic.d.ts
import * as Internal from './System.Collections.Generic/internal/index.js';

// Public API exports (curated - no export *)
// Value re-exports for classes
export { List_1 as List } from './System.Collections.Generic/internal/index.js';

// Type aliases for interfaces
export type IEnumerable<T> = Internal.IEnumerable_1<T>;
```

### bindings.json

CLR semantics not expressible in TypeScript. Used by the Tsonic compiler for correct C# interop and runtime binding.

```json
{
  "namespace": "System",
  "contributingAssemblies": ["System.Private.CoreLib"],
  "types": [
    {
      "stableId": "System.Private.CoreLib:System.Int32",
      "clrName": "System.Int32",
      "assemblyName": "System.Private.CoreLib",
      "kind": "Struct",
      "accessibility": "Public",
      "isStatic": false,
      "methods": [
        {
          "clrName": "TryParse",
          "isStatic": true,
          "parameterModifiers": [{ "index": 1, "modifier": "out" }]
        }
      ]
    }
  ]
}
```

**Key fields:**
- `kind` / `accessibility` / `isAbstract` / `isStatic` - Type semantics
- `emitScope` - Where the member is emitted (`ClassSurface`, `ViewOnly`, etc.)
- `isVirtual` / `isOverride` / `isAbstract` - Dispatch semantics
- `parameterModifiers` - Vector of ref/out/in modifiers for byref parameters

### Parameter Modifiers

The `parameterModifiers` vector tracks C# `ref`, `out`, and `in` parameter modifiers:

```json
{
  "clrName": "TryParse",
  "parameterModifiers": [{ "index": 1, "modifier": "out" }]
}
```

- `null` - Normal by-value parameter
- `"ref"` - By-reference, must be initialized
- `"out"` - By-reference, assigned by method
- `"in"` - By-reference, read-only

This metadata is required by the Tsonic compiler for correct C# interop since TypeScript has no concept of by-reference parameters.

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

## Import Conventions

### Internal to Internal

Relative paths between namespace internals:

```typescript
// From System.Linq/internal/index.d.ts
import type { IEnumerable_1 } from "../../System.Collections.Generic/internal/index.js";
```

### Facade to Internal

Facades import from `./<Namespace>/internal/index.js` but use curated exports (no `export *`):

```typescript
// From System.Linq.d.ts
import * as Internal from './System.Linq/internal/index.js';
export { Enumerable } from './System.Linq/internal/index.js';
export type IQueryable<T> = Internal.IQueryable_1<T>;
```

### Support Types

From `@tsonic/core` package:

```typescript
import type { ptr } from "@tsonic/core/types.js";
```

## Naming Conventions

| CLR | TypeScript |
|-----|------------|
| Generic arity: `List`1` | Underscore: `List_1` |
| Nested type: `Outer+Inner` | Underscore: `Outer_Inner` |
| Namespace | Directory name (with dots) |
