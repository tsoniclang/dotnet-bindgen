# tsbindgen Pipeline Architecture (Concise)

**Condensed architectural documentation** (~50% size of full docs).

## Documentation Index

### Core Architecture
- **[00-overview.md](00-overview.md)** - System overview, principles, BuildContext
- **[01-pipeline-flow.md](01-pipeline-flow.md)** - Phase sequence, transformations

### Pipeline Phases
- **[02-phase-load.md](02-phase-load.md)** - Phase 1: Reflection/assembly loading
- **[03-phase-model.md](03-phase-model.md)** - Data structures (SymbolGraph, TypeSymbol, MemberSymbols)
- **[04-phase-shape.md](04-phase-shape.md)** - Phase 3: 23 transformation passes (including extension methods)
- **[05-phase-normalize.md](05-phase-normalize.md)** - Phase 3.5: Name reservation, overload unification
- **[06-phase-plan.md](06-phase-plan.md)** - Phase 4: Import planning, validation setup
- **[07-phasegate.md](07-phasegate.md)** - PhaseGate validation (50+ rules, 43 diagnostic codes)
- **[08-phase-emit.md](08-phase-emit.md)** - Phase 5: File generation

### Infrastructure
- **[09-renaming.md](09-renaming.md)** - Renaming system (dual-scope naming, collision handling)
- **[10-call-graphs.md](10-call-graphs.md)** - Complete call chains

## Reading Guide

### New Developers
1. **00-overview.md** - Big picture
2. **01-pipeline-flow.md** - Phase connections
3. **10-call-graphs.md** - Execution flow

### By Topic
- **Validation**: 07-phasegate.md (all rules + diagnostic codes)
- **Naming**: 09-renaming.md (dual-scope algorithm)
- **Transformations**: 04-phase-shape.md (23 passes, ordering, examples)
- **Code Generation**: 08-phase-emit.md (output formats, printers)

## Key Concepts

**StableId** - Assembly-qualified identifiers (TypeStableId, MemberStableId)

**EmitScope** - Placement decisions (ClassSurface, ViewOnly, Omitted)

**Dual-Scope Naming** - Separate scopes for class surface vs view surface

**ViewPlanner** - Explicit interface implementation (`As_IInterfaceName` properties)

**PhaseGate** - Final validation (50+ rules, 43 codes: TBG001-TBG883)

## Statistics
- 79 source files
- 23 transformation passes (including extension methods)
- 50+ validation rules
- 43 diagnostic codes (TBG001-TBG883)
- 7 output file types (.d.ts, .json, .js, extension buckets)

## Coverage
All 79 files in src/tsbindgen/ documented:
- ✅ Public/private methods with algorithms
- ✅ All validation rules & diagnostic codes
- ✅ All transformation passes
- ✅ Complete call chains
- ✅ Data structures & key algorithms

## Navigation

**By Phase:**
- Load → [02-phase-load.md](02-phase-load.md)
- Model → [03-phase-model.md](03-phase-model.md)
- Shape → [04-phase-shape.md](04-phase-shape.md)
- Normalize → [05-phase-normalize.md](05-phase-normalize.md)
- Plan → [06-phase-plan.md](06-phase-plan.md)
- PhaseGate → [07-phasegate.md](07-phasegate.md)
- Emit → [08-phase-emit.md](08-phase-emit.md)

**By Topic:**
- Infrastructure → [00-overview.md](00-overview.md)
- Naming → [09-renaming.md](09-renaming.md)
- Validation → [07-phasegate.md](07-phasegate.md)
- Execution → [10-call-graphs.md](10-call-graphs.md)
