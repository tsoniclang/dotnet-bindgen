# Output Directory Layout Specification

This document defines the file structure and organization of tsbindgen output.

## Directory Structure

```
<output-dir>/
  <Namespace>/
    internal/
      index.d.ts                       # Internal declarations (full API)
    index.d.ts                         # Facade (re-exports from internal/)
    metadata.json                      # CLR semantics for Tsonic compiler
    bindings.json                      # Name mappings (if transforms active)
    typelist.json                      # Emitted types (for verification)
  <Namespace.Nested>/
    internal/
      index.d.ts
    index.d.ts
    metadata.json
    bindings.json
    typelist.json
  [... one directory per namespace ...]
```

## File Purposes

### Support Types (from @tsonic/types package)
**Purpose**: Centralized support types for unsafe CLR constructs.

**Package**: `@tsonic/types` - separate npm package installed as dependency

**Contents**:
- `ptr<T>`: Marker type for C# pointer types (`void*`, `int*`, `T*`)
- `ref<T>`: Phantom type for C# ref/out/in parameters

**Example**:
```typescript
// From @tsonic/types package:
export type ptr<T> = unknown & { readonly __ptr: unique symbol };
export type ref<T> = T & { __ref?: never };
```

**Imports**: Namespaces import on-demand:
```typescript
import type { ptr, ref } from "@tsonic/types";
```

### `<Namespace>/internal/index.d.ts`
**Purpose**: Full TypeScript declarations for all public types in the namespace.

**Contents**:
- Namespace declaration wrapping all types
- Classes, interfaces, enums, delegates
- Import statements for cross-namespace dependencies
- Import statements for support types from @tsonic/types (if used)

**Example**:
```typescript
// Import external dependencies
import type { IEnumerable_1 } from "../../System.Collections.Generic/internal/index.js";

// Import support types (if namespace uses pointers/byrefs)
import type { ptr, ref } from "@tsonic/types";

export namespace System.Linq {
  export class Enumerable {
    static SelectMany<TSource, TResult>(
      source: IEnumerable_1<TSource>,
      selector: (item: TSource) => IEnumerable_1<TResult>
    ): IEnumerable_1<TResult>;
  }
}
```

### `<Namespace>/index.d.ts`
**Purpose**: Facade that re-exports everything from `internal/index.d.ts`.

**Pattern**: ALWAYS imports from `./internal/index.js` (relative, not `../internal/index.js`).

**Example**:
```typescript
export * from "./internal/index.js";
```

**Rationale**: Provides clean public API while keeping internal declarations separate.

### `<Namespace>/metadata.json`
**Purpose**: CLR-specific semantics not expressible in TypeScript.

**Used by**: Tsonic compiler for C# code generation.

**See**: [metadata.md](metadata.md) for complete schema.

**Key data**:
- Virtual/override/abstract modifiers
- Accessibility (public/protected/internal)
- Static vs instance members
- Indexer tracking (intentionally omitted from .d.ts)
- Generic static member tracking

### `<Namespace>/bindings.json`
**Purpose**: CLR name → TypeScript name mappings when naming transforms are active.

**Generated when**: Any `--*-names camelCase` option is used.

**Used by**: Tsonic compiler for name resolution.

**See**: [bindings-consumer.md](bindings-consumer.md) for complete schema.

**Example**:
```json
{
  "SelectMany": {
    "kind": "method",
    "name": "SelectMany",
    "alias": "selectMany",
    "fullName": "System.Linq.Enumerable.SelectMany"
  }
}
```

### `<Namespace>/typelist.json`
**Purpose**: Flat list of all types and members actually emitted to .d.ts.

**Generated when**: `--debug-typelist` flag is used, or always in Single-Phase pipeline.

**Used by**: Completeness verification script (`scripts/verify-completeness.js`).

**See**: Verification section below for schema.

## Import Path Conventions

### Internal → Internal (Same Assembly)
**Pattern**: Relative path to sibling namespace's `internal/index.js`

**Example**: From `System.Linq/internal/index.d.ts` to `System.Collections.Generic/internal/index.d.ts`:
```typescript
import type { IEnumerable_1 } from "../../System.Collections.Generic/internal/index.js";
```

### Internal → @tsonic/types
**Pattern**: Package import from `@tsonic/types`

**Example**: From `System/internal/index.d.ts`:
```typescript
import type { ptr, ref } from "@tsonic/types";
```

### Facade → Internal (Same Namespace)
**Pattern**: ALWAYS `./internal/index.js` (NOT `../internal/index.js`)

**Example**: From `System.Linq/index.d.ts`:
```typescript
export * from "./internal/index.js";  // ✅ CORRECT
```

**Wrong**:
```typescript
export * from "../System.Linq/internal/index.js";  // ❌ WRONG
```

### External Packages (Planned)
**Pattern**: Module specifier from external-map.json

**Example**: From user assembly to BCL package:
```typescript
import type { String, int } from "@dotnet/bcl/System/internal/index.js";
```

## Directory Naming

### Namespace → Directory Mapping
**Rule**: Namespace name directly maps to directory name, preserving dots.

**Examples**:
- `System` → `System/`
- `System.Linq` → `System.Linq/`
- `System.Collections.Generic` → `System.Collections.Generic/`

**No nesting**: `System.Linq` is NOT inside `System/Linq/` directory.

### Special Directories
- `_root` – Root namespace (if types exist without namespace)

## File Existence Rules

### Always Generated
For every namespace with public types:
- `<Namespace>/internal/index.d.ts` (always)
- `<Namespace>/index.d.ts` (always)
- `<Namespace>/metadata.json` (always)

### Conditionally Generated
- `<Namespace>/bindings.json` (only if naming transforms active)
- `<Namespace>/typelist.json` (only if `--debug-typelist` or Single-Phase pipeline)

### Debug Output (with `--debug-snapshot`)
```
<output-dir>/
  assemblies/
    <Assembly>.snapshot.json           # Post-reflection snapshot
    assemblies-manifest.json           # Summary of all assemblies
  namespaces/
    <Namespace>.snapshot.json          # Post-aggregation snapshot
```

## Bundle Mode vs Lean Mode

### Bundle Mode (BCL Generation)
**Characteristics**:
- Emits ALL types from transitive closure
- All imports are relative (within output directory)
- No external package dependencies
- Support types from @tsonic/types package if any namespace uses unsafe types

### Lean Mode (User Assembly, Planned)
**Characteristics**:
- Emits ONLY seed assembly types
- BCL types imported from external packages (e.g., `@dotnet/bcl`)
- Requires `--external-map` for foreign type resolution
- Support types from @tsonic/types package only if seed assembly uses unsafe types

## Verification Files

### `typelist.json` Schema
**Purpose**: Enable verification that all reflected types were emitted.

**Structure**: Flat dictionary keyed by `tsEmitName` (matches `snapshot.json` format).

**Example**:
```json
{
  "types": {
    "Enumerable": {
      "clrName": "Enumerable",
      "tsEmitName": "Enumerable",
      "kind": "class",
      "members": {
        "SelectMany": {
          "clrName": "SelectMany",
          "tsEmitName": "SelectMany",
          "kind": "method",
          "emitScope": "StaticSurface"
        }
      }
    }
  }
}
```

**Verification**: `scripts/verify-completeness.js` compares `snapshot.json` (what was reflected) with `typelist.json` (what was emitted) to ensure zero data loss.

## Example Output Tree

```
bcl-types/
  System/
    internal/
      index.d.ts
    index.d.ts
    metadata.json
    typelist.json
  System.Collections/
    internal/
      index.d.ts
    index.d.ts
    metadata.json
    typelist.json
  System.Collections.Generic/
    internal/
      index.d.ts
    index.d.ts
    metadata.json
    typelist.json
  System.Linq/
    internal/
      index.d.ts
    index.d.ts
    metadata.json
    bindings.json                      # (if camelCase transform used)
    typelist.json
  [... more namespaces ...]
```

## Platform Compatibility

### Path Separators
**Rule**: Always use forward slashes (`/`) in import paths, regardless of OS.

**Example**:
```typescript
// ✅ CORRECT (works on Windows, Linux, macOS)
import type { IEnumerable_1 } from "../../System.Collections.Generic/internal/index.js";

// ❌ WRONG (breaks on Linux/macOS)
import type { IEnumerable_1 } from "..\\..\\System.Collections.Generic\\internal\\index.js";
```

### Case Sensitivity
**Rule**: Preserve exact CLR namespace casing in directory names.

**Example**:
- Namespace `System.IO` → Directory `System.IO/` (capital I, capital O)
- Namespace `System.Linq.Expressions` → Directory `System.Linq.Expressions/`

**Rationale**: TypeScript module resolution is case-sensitive on Linux/macOS.
