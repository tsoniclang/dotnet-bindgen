# Architecture Overview

tsbindgen transforms .NET assemblies into TypeScript declaration files through a multi-phase pipeline.

## Design Goals

1. **Complete fidelity** - Preserve all type information from .NET reflection
2. **Type safety** - Generate TypeScript that passes `--strict` compilation
3. **Deterministic output** - Same input always produces identical output
4. **IDE support** - Full IntelliSense for all BCL types
5. **Interop ergonomics** - Branded primitives, friendly aliases, callable delegates

## Pipeline Summary

```
Input (.NET DLLs)
    |
    v
+----------+   +----------+   +----------+   +------------+
|   LOAD   | > |  MODEL   | > |  SHAPE   | > | NORMALIZE  |
+----------+   +----------+   +----------+   +------------+
                                                   |
    +----------------------------------------------+
    v
+----------+   +------------+   +----------+
|   PLAN   | > |  PHASEGATE | > |   EMIT   |
+----------+   +------------+   +----------+
    |
    v
Output (.d.ts files)
```

See [Pipeline Flow](pipeline.md) for detailed phase descriptions.

## Core Components

### BuildContext

Shared context passed through all phases:

- `Policy` - CLI options converted to generation policy
- `Renamer` - Central naming authority
- `Diagnostics` - Error/warning collection

### SymbolGraph

Central type registry holding all namespaces and types with O(1) lookup by CLR name.

### SymbolRenamer

Single source of truth for all TypeScript identifiers. Handles name reservation, conflict resolution, and style transforms.

## Source Layout

```
src/tsbindgen/
+-- Builder.cs              # Pipeline orchestrator
+-- BuildContext.cs         # Shared context
+-- Cli/                    # Command-line interface
+-- Load/                   # Phase 1: Assembly loading
+-- Model/                  # Phase 2: Data structures
+-- Shape/                  # Phase 3: Transformations
+-- Normalize/              # Phase 3.5: Name reservation
+-- Plan/                   # Phase 4: Planning
+-- Emit/                   # Phase 5: File generation
+-- Renaming/               # Naming system
```

## Functional Programming Style

All code follows functional programming principles:

- **Static classes only** - No instance state
- **Pure functions** - No side effects (except I/O)
- **Immutable data** - Records and readonly collections
