# tsbindgen Architecture

This documentation covers the internal architecture of tsbindgen, the TypeScript declaration generator for .NET assemblies.

## Documentation Index

### Core Architecture

| Document | Description |
|----------|-------------|
| [Overview](overview.md) | High-level architecture and design principles |
| [Pipeline](pipeline.md) | Complete pipeline flow from input to output |
| [Key Concepts](concepts.md) | Facades, views, EmitScope, dual-scope naming, etc. |

### Pipeline Phases

| Document | Description |
|----------|-------------|
| [Load Phase](load.md) | Assembly loading and reflection |
| [Model](model.md) | Symbol graph data structures (TypeReference, StableId) |
| [Shape Phase](shape.md) | 29 transformation passes |
| [Normalize Phase](normalize.md) | Name reservation and conflict resolution |
| [Plan Phase](plan.md) | Import/export planning and validation |
| [Phase Gate](phasegate.md) | Validation checkpoint (50+ rules, all diagnostic codes) |
| [Emit Phase](emit.md) | TypeScript file generation |

### Reference

| Document | Description |
|----------|-------------|
| [Output Files](output-files.md) | Generated file formats and schemas |
| [Renaming](renaming.md) | Naming system and style transforms |

## Quick Reference

### Pipeline Phases

```
┌─────────────────────────────────────────────────────────────────────┐
│                         TSBINDGEN PIPELINE                          │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌─────────┐    ┌─────────┐    ┌─────────┐    ┌─────────────────┐  │
│  │  LOAD   │ -> │  MODEL  │ -> │  SHAPE  │ -> │    NORMALIZE    │  │
│  │         │    │         │    │         │    │                 │  │
│  │ DLLs    │    │ Symbol  │    │ 23      │    │ Name            │  │
│  │ ↓       │    │ Graph   │    │ Passes  │    │ Reservation     │  │
│  │ Reflect │    │         │    │         │    │                 │  │
│  └─────────┘    └─────────┘    └─────────┘    └─────────────────┘  │
│                                                                     │
│  ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐  │
│  │      PLAN       │ -> │    PHASEGATE    │ -> │      EMIT       │  │
│  │                 │    │                 │    │                 │  │
│  │ Import Planning │    │ Validation      │    │ .d.ts Files     │  │
│  │ Export Planning │    │ (50+ rules)     │    │ Facades         │  │
│  │ SCC Analysis    │    │                 │    │                 │  │
│  └─────────────────┘    └─────────────────┘    └─────────────────┘  │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

### Key Data Structures

| Structure | Phase | Description |
|-----------|-------|-------------|
| `SymbolGraph` | Load → Emit | Central type registry with namespaces and types |
| `TypeSymbol` | All | Represents a CLR type with members |
| `NamespaceSymbol` | All | Collection of types in a namespace |
| `TypeReference` | All | Reference to a type (named, generic, array, etc.) |
| `EmissionPlan` | Plan → Emit | Complete plan for file generation |
| `ImportPlan` | Plan → Emit | Import/export statements per namespace |

### Key Components

| Component | Location | Responsibility |
|-----------|----------|----------------|
| `Builder` | `Builder.cs` | Pipeline orchestrator |
| `BuildContext` | `BuildContext.cs` | Shared services and configuration |
| `SymbolRenamer` | `Renaming/SymbolRenamer.cs` | Name reservation and conflict resolution |
| `GenerationPolicy` | `Core/Policy/GenerationPolicy.cs` | Configuration options |
| `ClassPrinter` | `Emit/Printers/ClassPrinter.cs` | Type declaration emission |
| `TypeRefPrinter` | `Emit/Printers/TypeRefPrinter.cs` | Type reference emission |

## Design Principles

1. **Immutability**: All model types are immutable records
2. **Functional style**: Static classes with pure functions (no instance state)
3. **Phase separation**: Clear boundaries between phases
4. **Determinism**: Same input always produces identical output
5. **Validation**: PhaseGate validates invariants before emission

## Source Layout

```
src/tsbindgen/
├── Builder.cs              # Pipeline orchestrator
├── BuildContext.cs         # Shared context
├── Cli/                    # Command-line interface
│   ├── Program.cs
│   └── GenerateCommand.cs
├── Core/
│   ├── Diagnostics/        # Error/warning infrastructure
│   └── Policy/             # Configuration
├── Load/                   # Phase 1: Assembly loading
│   └── AssemblyLoader.cs
├── Model/                  # Phase 2: Data structures
│   ├── SymbolGraph.cs
│   ├── Symbols/            # Type, Method, Property, etc.
│   └── Types/              # TypeReference hierarchy
├── Shape/                  # Phase 3: Transformations
│   ├── StructuralConformance.cs
│   ├── ViewPlanner.cs
│   └── ... (23 passes)
├── Normalize/              # Phase 3.5: Name reservation
│   └── NameReservation.cs
├── Plan/                   # Phase 4: Import/export planning
│   ├── ImportPlanner.cs
│   ├── EmissionPlanner.cs
│   └── ... (validators)
├── Emit/                   # Phase 5: File generation
│   ├── FacadeEmitter.cs
│   ├── InternalIndexEmitter.cs
│   └── Printers/           # TypeScript code printers
└── Renaming/               # Naming system
    ├── SymbolRenamer.cs
    ├── RenameScope.cs
    └── TypeScriptReservedWords.cs
```
