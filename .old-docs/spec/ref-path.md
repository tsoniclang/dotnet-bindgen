# External Package Resolution via `--ref-path`

**Status**: Planned feature

This specification defines how tsbindgen resolves references to external .NET types provided by third-party tsbindgen packages.

## Overview

The `--ref-path` flag points to directories containing **installed tsbindgen packages** (e.g., `node_modules/@vendor/pkg`). Each external package must contain a `tsbindgen.manifest.json` file that maps CLR types to TypeScript module specifiers.

## CLI Interface

### `--ref-path <dir>` (repeatable)

Points to a directory containing installed tsbindgen packages.

**Usage**:
```bash
# Single reference path
tsbindgen generate -a MyApp.dll \
  --ref-path ./node_modules \
  --out-dir ./types

# Multiple reference paths
tsbindgen generate -a MyApp.dll \
  --ref-path ./node_modules \
  --ref-path ./vendor-libs \
  --out-dir ./types
```

**Behavior by Mode**:
- **Bundle mode** (BCL): Emits full closure, `--ref-path` not needed
- **Lean mode** (user assemblies): Emits only seed assemblies, all foreign types must come from `--ref-path` packages

## External Package Structure

Each external tsbindgen package MUST follow this structure:

```
<package-root>/
  package.json                 # npm package metadata
  tsbindgen.manifest.json      # ← CLR → TS type mappings (required)
  System/
    internal/
      index.d.ts               # TypeScript declarations
    index.d.ts                 # Facade
    metadata.json              # CLR semantics
  System.Collections.Generic/
    internal/
      index.d.ts
    index.d.ts
    metadata.json
```

**Key requirement**: Must have `tsbindgen.manifest.json` in package root.

## Manifest Schema (`tsbindgen.manifest.json`)

```json
{
  "package": "@dotnet/bcl",
  "version": "10.0.0",
  "entries": [
    {
      "assembly": "System.Private.CoreLib",
      "fullName": "System.String",
      "tsName": "String",
      "kind": "class",
      "arity": 0,
      "module": "@dotnet/bcl/System/internal/index.js"
    },
    {
      "assembly": "System.Private.CoreLib",
      "fullName": "System.Collections.Generic.List`1",
      "tsName": "List_1",
      "kind": "class",
      "arity": 1,
      "module": "@dotnet/bcl/System.Collections.Generic/internal/index.js"
    },
    {
      "assembly": "System.Runtime",
      "fullName": "System.IDisposable",
      "tsName": "IDisposable",
      "kind": "interface",
      "arity": 0,
      "module": "@dotnet/bcl/System/internal/index.js"
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `package` | string | npm package name (must match `package.json` "name" field) |
| `version` | string | Package version (informational) |
| `entries` | ManifestEntry[] | Array of type mappings |

### ManifestEntry

| Field | Type | Description |
|-------|------|-------------|
| `assembly` | string | CLR assembly simple name (e.g., `"System.Runtime"`) |
| `fullName` | string | CLR full type name with backtick arity (e.g., `"System.Collections.Generic.List`1"`) |
| `tsName` | string | Exact TypeScript identifier as exported (e.g., `"List_1"`) |
| `kind` | string | Type classification: `"class"`, `"interface"`, `"enum"`, `"delegate"` |
| `arity` | number | Generic type parameter count (0 for non-generic) |
| `module` | string | Full module specifier for importing (e.g., `"@dotnet/bcl/System.Collections.Generic/internal/index.js"`) |

**Notes**:
- `fullName` uses CLR conventions: backtick for generics (`` `1 ``), plus sign for nested types (`Outer+Inner`)
- `tsName` uses TypeScript conventions: underscore for generics (`_1`), dollar sign for nested types (`Outer$Inner`)
- `module` is the exact string used in `import type { ... } from "..."` statements

## Resolution Algorithm

### Phase 1: Manifest Discovery (at startup)

1. Scan each `--ref-path` directory recursively for `tsbindgen.manifest.json` files
2. Parse each manifest and validate structure
3. Build **ExternalIndex**: In-memory dictionary keyed by `(assembly, fullName)` → `ManifestEntry`

**ExternalIndex structure**:
```typescript
{
  "System.Private.CoreLib:System.String": {
    "package": "@dotnet/bcl",
    "assembly": "System.Private.CoreLib",
    "fullName": "System.String",
    "tsName": "String",
    "kind": "class",
    "arity": 0,
    "module": "@dotnet/bcl/System/internal/index.js"
  },
  "System.Private.CoreLib:System.Collections.Generic.List`1": {
    "package": "@dotnet/bcl",
    "assembly": "System.Private.CoreLib",
    "fullName": "System.Collections.Generic.List`1",
    "tsName": "List_1",
    "kind": "class",
    "arity": 1,
    "module": "@dotnet/bcl/System.Collections.Generic/internal/index.js"
  }
}
```

### Phase 2: Type Reference Resolution (during emit)

When emitting a type reference in **lean mode**:

1. **Check if type is in emit set** (our own types being generated)
   - If yes → use internal import (relative path)
   - If no → proceed to external resolution

2. **Look up type in ExternalIndex** by `(assembly, fullName)`
   - If found → emit external import: `import type { <tsName> } from "<module>"`
   - If not found → **PG_EXT_001 ERROR**

3. **Validate arity matches** manifest entry (**PG_EXT_002**)

### Phase 3: Import Deduplication

Track imported external types per output file to avoid duplicates:

```typescript
// ✅ CORRECT (deduplicated)
import type { IEnumerable_1, CancellationToken } from "@dotnet/bcl/System.Collections.Generic/internal/index.js";

// ❌ WRONG (duplicate imports)
import type { IEnumerable_1 } from "@dotnet/bcl/System.Collections.Generic/internal/index.js";
import type { IEnumerable_1 } from "@dotnet/bcl/System.Collections.Generic/internal/index.js";
```

## PhaseGate Guards

### PG_EXT_001: External Type Resolution (ERROR)

**Triggers when**:
- Emitting a type reference in lean mode
- Type is not in emit set (foreign type)
- Type is not found in ExternalIndex

**Error message**:
```
PG_EXT_001: External type 'System.Collections.Generic.List`1' from assembly 'System.Runtime'
is referenced in signature but not found in any --ref-path package manifest.
Referenced in: MyCompany.Feature.Uploader.GetItems()
```

**Resolution**:
1. Install the package containing the type
2. Add `--ref-path` pointing to package location
3. Verify package has valid `tsbindgen.manifest.json`

### PG_EXT_002: External Type Arity Mismatch (ERROR)

**Triggers when**:
- External type found in ExternalIndex
- But generic arity doesn't match manifest

**Error message**:
```
PG_EXT_002: External type 'System.Collections.Generic.List`1' has arity mismatch.
Expected: 1 (from manifest), Actual: 0 (in signature)
Referenced in: MyCompany.Feature.Uploader.GetItems()
```

**Resolution**:
1. Fix CLR type usage (ensure correct generic arity)
2. Re-generate external package if manifest is stale

## Example Output (Lean Mode)

**Input**: `MyCompany.Feature.dll` references BCL types

**Command**:
```bash
tsbindgen generate -a MyCompany.Feature.dll \
  --ref-path ./node_modules \
  --out-dir ./types
```

**Generated `MyCompany.Feature/internal/index.d.ts`**:
```typescript
// External type imports (from @dotnet/bcl package)
import type { IEnumerable_1 } from "@dotnet/bcl/System.Collections.Generic/internal/index.js";
import type { CancellationToken } from "@dotnet/bcl/System.Threading/internal/index.js";

// Support type imports (from @tsonic/types package)
import type { ref } from "@tsonic/types";

export namespace MyCompany.Feature {
  export class Uploader {
    upload(
      items: IEnumerable_1<string>,
      cancel: CancellationToken,
      result: ref<int>
    ): void;
  }
}
```

**Notes**:
- `IEnumerable_1` and `CancellationToken` imported from `@dotnet/bcl` package
- `ref` imported from `@tsonic/types` package
- `int` is a branded type imported from `@tsonic/types`

## Bundle Mode vs Lean Mode

### Bundle Mode (BCL Generation)
```bash
tsbindgen generate --assembly-dir /path/to/bcl --out-dir ./bcl-types
```

**Behavior**:
- Emits ALL types from transitive closure
- ALL imports are relative (no external packages)
- `--ref-path` not needed
- Self-contained output

### Lean Mode (User Assembly)
```bash
tsbindgen generate -a MyApp.dll \
  --ref-path ./node_modules \
  --out-dir ./types
```

**Behavior**:
- Emits ONLY `MyApp.dll` types
- BCL types imported from `@dotnet/bcl` via manifest
- `--ref-path` required for foreign types
- Minimal output

## Manifest Generation

tsbindgen automatically generates `tsbindgen.manifest.json` during emit:

**Process**:
1. Enumerate all emitted types
2. For each type, create manifest entry with:
   - `assembly`: Assembly simple name
   - `fullName`: CLR full name (with backtick arity)
   - `tsName`: TypeScript emit name
   - `kind`, `arity`: From type metadata
   - `module`: Generated module path
3. Write manifest to output root directory

**Generated manifest location**:
```
<output-dir>/
  tsbindgen.manifest.json    # ← Generated manifest
  System/
    internal/
      index.d.ts
    ...
```

## Publishing tsbindgen Packages

**Steps**:
1. Generate declarations: `tsbindgen generate -a Assembly.dll --out-dir ./pkg`
2. Verify manifest exists: `pkg/tsbindgen.manifest.json`
3. Create `package.json`:
   ```json
   {
     "name": "@company/assembly-types",
     "version": "1.0.0",
     "main": "index.js",
     "types": "index.d.ts",
     "files": [
       "**/*.d.ts",
       "**/*.json"
     ]
   }
   ```
4. Publish: `npm publish`

**Consumers**:
```bash
npm install @company/assembly-types
tsbindgen generate -a MyApp.dll --ref-path ./node_modules --out-dir ./types
```

## Rationale

### Why manifest instead of parsing .d.ts?

1. **Performance**: Manifest is simple JSON lookup; parsing .d.ts requires TypeScript compiler
2. **Correctness**: Manifest provides exact CLR → TS mapping that .d.ts doesn't encode
3. **Simplicity**: Single source of truth, no ambiguity
4. **Future-proof**: Can add metadata (versioning, deprecation) without .d.ts changes

### Why not support arbitrary TypeScript packages?

tsbindgen generates **bindings for .NET types**. External packages must also be tsbindgen output to maintain CLR → TS mapping integrity. Supporting arbitrary TS would require:
- Reverse-mapping TS types to CLR (impossible in general)
- Loss of metadata (virtual/override, ref/out, etc.)
- Breaking the binding contract that Tsonic compiler relies on

### Why `--ref-path` instead of `--external-package`?

`--ref-path` is more flexible:
- Points to directory containing multiple packages
- Works with standard `node_modules` layout
- Repeatable for multiple search locations
- No need to list individual packages

## Success Criteria

Feature is complete when:

- ✅ BCL can be generated in bundle mode (no `--ref-path`)
- ✅ User assembly can be generated in lean mode with `--ref-path ./node_modules`
- ✅ All BCL type references resolve to external imports
- ✅ PG_EXT_001 triggers for missing external types
- ✅ PG_EXT_002 triggers for arity mismatches
- ✅ Zero PhaseGate errors for valid lean mode generation
- ✅ Generated code passes TypeScript validation
- ✅ Manifest generation works automatically
