# tsbindgen Pipeline Architecture

## 1. System Overview

### What is tsbindgen?

\**tsbindgen** is a .NET reflection-based code generation tool that transforms .NET assemblies into TypeScript declaration files with metadata sidecars. It enables the **Tsonic compiler** (a TypeScript-to-C# compiler targeting NativeAOT) to import and use .NET Base Class Library (BCL) types from TypeScript code with full IDE support and type safety.

### The Tsonic Compiler (Consumer of tsbindgen Output)

**Tsonic** is a TypeScript-to-C# compiler that produces NativeAOT executables:

- **Purpose**: Enable developers to write .NET applications using TypeScript syntax
- **Not a Node.js port**: Tsonic is .NET-first, using native .NET types directly
- **Direct .NET interop**: TypeScript code can import .NET types like `import { File } from "System.IO"`
- **NativeAOT output**: Compiles to single-file native executables via .NET CLI
- **Type safety**: Full TypeScript type checking against .NET APIs

**Example Tsonic Code**:
```typescript
import { File } from "System.IO";
import { Console } from "System";

const lines = File.ReadAllLines("data.txt");  // Returns ReadonlyArray<string>
Console.WriteLine(`Read ${lines.length} lines`);
```

**Generated C#**:
```csharp
using System;
using System.IO;

var lines = File.ReadAllLines("data.txt");
Console.WriteLine($"Read {lines.Length} lines");
```

### How tsbindgen Enables Tsonic

For Tsonic to compile TypeScript code that references .NET types, it needs three companion artifacts for each .NET assembly:

1. **Type declarations** (`*.d.ts`) - TypeScript ambient declarations for IDE IntelliSense and type checking
2. **Metadata sidecars** (`*.metadata.json`) - CLR semantics TypeScript can't express (virtual/override, struct, ref parameters)
3. **Binding manifests** (`*.bindings.json`) - Maps TypeScript names to CLR names for runtime binding

**tsbindgen generates all three artifacts automatically from .NET assembly DLLs.**

### Purpose

The primary objective is to bridge the gap between TypeScript and .NET:

- **Input**: .NET assembly DLL files (e.g., System.Private.CoreLib.dll, System.Collections.dll)
- **Process**: Reflect over assemblies, analyze type relationships, resolve naming conflicts, plan emission
- **Output**: TypeScript `.d.ts` files, JSON metadata sidecars, and binding mappings

This enables TypeScript developers using the Tsonic compiler to:
- Reference .NET types with IntelliSense support in their IDE
- Import and use .NET BCL types like `List<T>`, `Dictionary<K,V>`, `File`, `Console`
- Understand CLR-specific semantics (virtual, static, ref parameters, struct vs class)
- Maintain type safety across the TypeScript→C# boundary
- Get correct C# code generation through metadata

### Why Does This Exist?

The Tsonic compiler needs to understand .NET BCL types to:
1. **Type-check TypeScript code** against .NET APIs during compilation
2. **Generate correct C# code** with proper member calls and overload resolution
3. **Respect CLR semantics** (inheritance, interfaces, generics, constraints, struct vs class)
4. **Handle naming conflicts** (TypeScript reserved words, duplicate names, overloads)
5. **Support explicit interface implementations** (a .NET feature with no TypeScript equivalent)

Manual maintenance of 4,000+ BCL types across 130 namespaces would be infeasible. tsbindgen automates this with 100% data integrity guarantees.

### Relationship to Tsonic Project

tsbindgen is a **critical dependency** of the Tsonic compiler:

```
┌─────────────────────────────────────────────────────────┐
│              Tsonic Compiler Pipeline                   │
│                                                         │
│  TypeScript Source                                      │
│       ↓                                                 │
│  [Parse via TypeScript Compiler API]                    │
│       ↓                                                 │
│  [Type Check Against .NET Declarations] ← tsbindgen .d.ts
│       ↓                                                 │
│  [Build IR with Type Info]                              │
│       ↓                                                 │
│  [Generate C# Code] ← tsbindgen .metadata.json          │
│       ↓                                                 │
│  [Compile to NativeAOT] ← tsbindgen .bindings.json      │
│       ↓                                                 │
│  Single-File Native Executable                          │
└─────────────────────────────────────────────────────────┘
```

**Tsonic consumes tsbindgen output from configured `typeRoots`**:

```jsonc
// tsonic.config.json
{
  "rootNamespace": "MyApp",
  "entryPoint": "src/index.ts",
  "dotnet": {
    "typeRoots": [
      "node_modules/@tsonic/dotnet-types/10.0.0/types",  // ← tsbindgen output
      "../tsonic-runtime/src/Tsonic.Runtime/types"
    ],
    "packages": [
      { "name": "Tsonic.Runtime", "version": "1.0.0" }
    ]
  }
}
```

At compile time, Tsonic:
1. Loads all `.d.ts` files into the TypeScript type checker
2. Loads all `.metadata.json` files to drive C# code generation
3. Loads all `.bindings.json` files to resolve TypeScript names to CLR names
4. Generates C# code with correct assembly references and member calls

---

## 2. Architectural Principles

### Single-Pass Processing

The pipeline processes each assembly **once** through six sequential phases. No iterative refinement or multiple passes. Each phase transforms the symbol graph immutably.

### Immutable Data Structures

All data structures are **immutable records**:
- `SymbolGraph` contains `NamespaceSymbol[]` which contain `TypeSymbol[]` which contain `MemberSymbol[]`
- Transformations return **new graph instances** via `with` expressions
- No in-place mutation - all changes create new objects

This enables:
- Pure functional transformations
- Safe parallelization (future)
- Precise change tracking
- Rollback capability

### Pure Functions

All transformation logic lives in **static classes** with **pure functions**:
- No instance state
- No side effects (except I/O)
- Input → Process → Output
- Predictable, testable, composable

Example:
```csharp
public static class InterfaceInliner
{
    public static SymbolGraph Inline(BuildContext ctx, SymbolGraph graph)
    {
        // Pure transformation - returns new graph
    }
}
```

### Centralized State (BuildContext)

All shared services live in `BuildContext`:
- **Policy**: Configuration (name transforms, filters, etc.)
- **SymbolRenamer**: Centralized naming authority
- **DiagnosticBag**: Error/warning collection
- **Interner**: String deduplication for memory efficiency
- **Logger**: Progress reporting

Services are **created once** at pipeline start and passed to every phase.

### StableId-Based Identity

Every type and member has a **StableId** (assembly-qualified identifier):
- **TypeStableId**: `"System.Private.CoreLib:System.Decimal"`
- **MemberStableId**: `"System.Private.CoreLib:System.Decimal::ToString(IFormatProvider):String"`

StableIds are:
- **Immutable**: Never change during pipeline
- **Unique**: Globally distinguish symbols
- **Stable**: Same across runs for same input
- **Semantic**: Based on signature, not metadata token

Used for:
- Rename decision keys
- Cross-assembly references
- Binding metadata
- Duplicate detection

### Scope-Based Naming

TypeScript names are reserved in **scopes** to enforce uniqueness:

1. **Namespace Scope**: `ns:System.Collections.Generic:internal`
   - Types must have unique names within namespace

2. **Class Surface Scope**: `type:System.Decimal#instance`
   - Instance members must have unique names on class

3. **View Surface Scope**: `view:CoreLib:System.Decimal:CoreLib:IConvertible#instance`
   - Interface view members have separate naming scope

Scopes enable:
- Class member `ToString` and view member `ToString` to coexist
- Static and instance members with same name
- Explicit interface implementations without collisions

---

## 3. High-Level Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                        CLI Entry Point                          │
│                   (src/tsbindgen/Program.cs)                    │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Builder.Build                   │
│          (src/tsbindgen/Builder.cs)      │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
                 ┌───────────────────────┐
                 │    BuildContext       │
                 │  - Policy             │
                 │  - SymbolRenamer      │
                 │  - DiagnosticBag      │
                 │  - Interner           │
                 │  - Logger             │
                 │  - LibraryMode        │  ← Library mode flag
                 └───────────┬───────────┘
                             │
                             ▼
        ┌────────────────────────────────────────┐
        │      PHASE 1: Load (Reflection)        │
        │  ┌──────────────────────────────────┐  │
        │  │  AssemblyLoader                  │  │
        │  │  - Transitive closure loading    │  │
        │  │  - Dependency resolution          │  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  ReflectionReader                │  │
        │  │  - System.Reflection over types  │  │
        │  │  - Build SymbolGraph             │  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  InterfaceMemberSubstitution     │  │
        │  │  - Substitute closed generic     │  │
        │  │    interface members             │  │
        │  └──────────────────────────────────┘  │
        └────────────────┬───────────────────────┘
                         │
                Output: SymbolGraph (pure CLR data)
                         │
                         ▼
        ┌────────────────────────────────────────┐
        │  PHASE 2: Normalize (Build Indices)    │
        │  ┌──────────────────────────────────┐  │
        │  │  SymbolGraph.WithIndices       │  │
        │  │  - NamespaceIndex                │  │
        │  │  - TypeIndex (for lookups)       │  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  [Library Mode Only]             │  │
        │  │  LibraryContractLoader.Load      │  │
        │  │  - Load metadata.json (types)    │  │
        │  │  - Load bindings.json (members)  │  │
        │  │  - Build LibraryContract         │  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  [Library Mode Only]             │  │
        │  │  LibraryFilter.Filter            │  │
        │  │  - Remove types IN contract      │  │
        │  │  - Keep types NOT in contract    │  │
        │  └──────────────────────────────────┘  │
        └────────────────┬───────────────────────┘
                         │
                Output: SymbolGraph (filtered if --lib)
                         │
                         ▼
        ┌────────────────────────────────────────┐
        │   PHASE 3: Shape (Transformations)     │
        │  ┌──────────────────────────────────┐  │
        │  │  1. GlobalInterfaceIndex.Build   │  │
        │  │  2. InterfaceDeclIndex.Build     │  │
        │  │  3. StructuralConformance.Analyze│  │
        │  │  4. InterfaceInliner.Inline      │  │
        │  │  5. ExplicitImplSynthesizer      │  │
        │  │  6. DiamondResolver.Resolve      │  │
        │  │  7. BaseOverloadAdder            │  │
        │  │  8. StaticSideAnalyzer.Analyze   │  │
        │  │  9. IndexerPlanner.Plan          │  │
        │  │  10. HiddenMemberPlanner.Plan    │  │
        │  │  11. FinalIndexersPass.Run       │  │
        │  │  12. ClassSurfaceDeduplicator    │  │
        │  │  13. ConstraintCloser.Close      │  │
        │  │  14. OverloadReturnConflict...   │  │
        │  │  15. ViewPlanner.Plan            │  │
        │  │  16. MemberDeduplicator          │  │
        │  │  17-20. Static/override plans    │  │
        │  │  21-22. Property override plans  │  │
        │  │  23. Extension method analysis   │  │
        │  └──────────────────────────────────┘  │
        └────────────────┬───────────────────────┘
                         │
                Output: SymbolGraph + Plans (shaped for TS)
                         │
                         ▼
        ┌────────────────────────────────────────┐
        │  PHASE 3.5: Name Reservation           │
        │  ┌──────────────────────────────────┐  │
        │  │  NameReservation.ReserveAllNames │  │
        │  │  - Reserve all type names        │  │
        │  │  - Reserve all member names      │  │
        │  │  - Apply naming policy           │  │
        │  │  - Resolve conflicts             │  │
        │  └──────────────────────────────────┘  │
        └────────────────┬───────────────────────┘
                         │
        Output: SymbolGraph + RenameDecisions in Renamer
                         │
                         ▼
        ┌────────────────────────────────────────┐
        │      PHASE 4: Plan (Imports/Order)     │
        │  ┌──────────────────────────────────┐  │
        │  │  ImportGraph.Build               │  │
        │  │  - Analyze type dependencies     │  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  ImportPlanner.PlanImports       │  │
        │  │  - Determine imports/aliases     │  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  EmitOrderPlanner.PlanOrder      │  │
        │  │  - Stable namespace order        │  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  OverloadUnifier.UnifyOverloads  │  │
        │  │  - Merge overload variants       │  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  InterfaceConstraintAuditor      │  │
        │  │  - Audit constructor constraints │  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  PhaseGate.Validate              │  │
        │  │  - Pre-emission validation       │  │
        │  │  - 50+ validation rules          │  │
        │  │  - Library mode validation       │  │
        │  │    (LIB001-003)                  │  │
        │  │  - Fail fast on errors           │  │
        │  └──────────────────────────────────┘  │
        └────────────────┬───────────────────────┘
                         │
                Output: EmissionPlan (graph + imports + order)
                         │
                         ▼
        ┌────────────────────────────────────────┐
        │      PHASE 5: Emit (File Generation)   │
        │  ┌──────────────────────────────────┐  │
        │  │  InternalIndexEmitter            │  │
        │  │  - internal/index.d.ts per NS    │  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  ExtensionsEmitter               │  │
        │  │  - internal/extensions/index.d.ts│  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  FacadeEmitter                   │  │
        │  │  - facade/index.d.ts per NS      │  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  MetadataEmitter                 │  │
        │  │  - metadata.json per NS          │  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  BindingEmitter                  │  │
        │  │  - bindings.json per NS          │  │
        │  └────────────┬─────────────────────┘  │
        │               ▼                         │
        │  ┌──────────────────────────────────┐  │
        │  │  ModuleStubEmitter               │  │
        │  │  - index.js stubs per NS         │  │
        │  └──────────────────────────────────┘  │
        └────────────────┬───────────────────────┘
                         │
                Output: Files written to output directory
                         │
                         ▼
                ┌────────────────┐
                │  BuildResult   │
                │  - Success     │
                │  - Statistics  │
                │  - Diagnostics │
                └────────────────┘
```

---

## 4. Key Concepts

### StableId: Assembly-Qualified Member Identifiers

A **StableId** is the immutable identity of a type or member **before any name transformations**. It serves as the permanent key for rename decisions and bindings back to the CLR.

**Format**:
- **TypeStableId**: `{AssemblyName}:{ClrFullName}`
  - Example: `"System.Private.CoreLib:System.Collections.Generic.List\`1"`

- **MemberStableId**: `{AssemblyName}:{DeclaringType}::{MemberName}{CanonicalSignature}`
  - Example: `"System.Private.CoreLib:System.Decimal::ToString(IFormatProvider):String"`

**Properties**:
- **Immutable**: Never changes during pipeline execution
- **Unique**: Globally distinguishes symbols across assemblies
- **Stable**: Same across runs for same input
- **Semantic**: Based on signature, not metadata token

**Usage**:
- Key for rename decisions in `SymbolRenamer`
- Cross-assembly type references
- Binding metadata (TS name → CLR name)
- Duplicate detection
- PhaseGate validation

**Equality**: Two `MemberStableId` instances are equal if they have the same assembly, declaring type, member name, and canonical signature — **metadata token is excluded** from equality comparisons (semantic identity only).

### EmitScope: ClassSurface vs ViewOnly vs Omitted Placement Decisions

`EmitScope` controls **where** a member is emitted in the TypeScript output:

```csharp
public enum EmitScope
{
    ClassSurface,  // Emitted directly on class/interface
    StaticSurface, // Emitted in static section of class
    ViewOnly,      // Emitted in As_IInterface view property
    Omitted        // Not emitted (tracked in metadata)
}
```

**ClassSurface**: Default placement for public members
- Instance methods, properties, fields emitted on class body
- Example: `class Decimal { ToString: string; }`

**StaticSurface**: Static members
- Emitted in separate static section
- Example: `class Decimal { static Parse(s: string): Decimal; }`

**ViewOnly**: Explicit interface implementations
- Can't be on class surface (structural conformance failed)
- Emitted in `As_IInterface` property
- Example: `class Decimal { As_IConvertible: { ToBoolean: boolean; }; }`

**Omitted**: Intentionally not emitted
- Indexers (TypeScript limitation)
- Generic static members (TypeScript limitation)
- Internal/private members (policy decision)
- Tracked in `metadata.json` for completeness

**Decision Process**:
1. **StructuralConformance** marks members as `ViewOnly` if structural implementation fails
2. **IndexerPlanner** marks indexers as `Omitted`
3. **HiddenMemberPlanner** handles C# `new` keyword (hides base members)
4. **ClassSurfaceDeduplicator** resolves duplicate names (demotes losers to `ViewOnly`)
5. **PhaseGate** validates `EmitScope` consistency

### ViewPlanner: Explicit Interface Implementation Support

TypeScript doesn't have explicit interface implementations. C# does:

```csharp
// C# - explicit interface implementation
class Decimal : IConvertible
{
    // Implicit - available as Decimal.ToString
    public override string ToString => "...";

    // Explicit - ONLY available as ((IConvertible)d).ToBoolean
    bool IConvertible.ToBoolean(IFormatProvider? provider) => ...;
}
```

**ViewPlanner** solves this by generating **view properties**:

```typescript
// TypeScript output
class Decimal {
    ToString: string;  // ClassSurface member

    // View property for IConvertible
    As_IConvertible: {
        ToBoolean(provider: IFormatProvider | null): boolean;  // ViewOnly member
        ToInt32(provider: IFormatProvider | null): int;
        // ... other IConvertible members
    };
}
```

**How it works**:
1. **StructuralConformance** marks members as `ViewOnly` when class can't structurally implement interface
2. **ViewPlanner** groups `ViewOnly` members by source interface
3. Creates `ExplicitView` objects with view property name and member list
4. Attaches views to `TypeSymbol.ExplicitViews`
5. **FacadeEmitter** emits view properties in final output

**Benefits**:
- Preserves full CLR semantics in TypeScript
- No loss of interface members
- Type-safe casting through view properties
- Tsonic compiler can generate correct C# code

### Scope-Based Naming: Why We Need Separate Scopes

TypeScript naming rules differ from C#:

**Problem 1: Class vs View Naming**
```csharp
// C# - different members, different names
class Decimal : IConvertible, IFormattable
{
    string ToString => "1.0";                           // On class
    string IConvertible.ToString(IFormatProvider p) => "1.0";  // IConvertible only
    string IFormattable.ToString(string fmt, IFormatProvider p) => "1.0";  // IFormattable only
}
```

Without separate scopes:
```typescript
// BAD - renamer would see conflict between ToString, ToString, ToString
class Decimal {
    ToString: string;
    As_IConvertible: {
        ToString: string;  // ❌ Conflict! Would become ToString2
    };
    As_IFormattable: {
        ToString: string;  // ❌ Conflict! Would become ToString3
    };
}
```

With separate scopes:
```typescript
// GOOD - each scope has its own ToString
class Decimal {
    ToString: string;  // Scope: "type:System.Decimal#instance"
    As_IConvertible: {
        ToString: string;  // Scope: "view:CoreLib:Decimal:CoreLib:IConvertible#instance"
    };
    As_IFormattable: {
        ToString: string;  // Scope: "view:CoreLib:Decimal:CoreLib:IFormattable#instance"
    };
}
```

**Problem 2: Static vs Instance Naming**
```csharp
// C# - static and instance can have same name
class Array
{
    int Length { get; }           // Instance property
    static int Length(Array a);   // Static method (different signature)
}
```

TypeScript equivalent:
```typescript
class Array {
    readonly Length: int;           // Scope: "type:System.Array#instance"
    static Length(a: Array): int;   // Scope: "type:System.Array#static"
}
```

**Scope Format**:
- **Namespace**: `ns:System.Collections.Generic:internal`
- **Class Instance**: `type:System.Decimal#instance`
- **Class Static**: `type:System.Decimal#static`
- **View Instance**: `view:CoreLib:System.Decimal:CoreLib:IConvertible#instance`
- **View Static**: `view:CoreLib:System.Decimal:CoreLib:IConvertible#static`

**Benefits**:
- Each context has independent naming
- No artificial suffixes needed
- Preserves original names where possible
- Type-safe scope validation via `ScopeFactory`

### PhaseGate: Validation Gatekeeper Before Emission

**PhaseGate** is a comprehensive validation layer that runs **after all transformations** and **before emission**. It enforces 50+ invariants to catch bugs early.

**Purpose**:
- Fail fast on invalid state
- Prevent malformed output
- Document architectural invariants
- Enable safe refactoring

**Categories of Validation**:

1. **Finalization** (PG_FIN_001-009)
   - Every symbol has a final TypeScript name
   - No member omitted without `EmitScope == Omitted`

2. **Scope Integrity** (PG_SCOPE_001-004)
   - Scope strings are well-formed
   - Scope kind matches `EmitScope`

3. **Name Uniqueness** (PG_NAME_001-005)
   - No duplicate names in same scope
   - Class surface members are unique
   - View members are unique per view

4. **View Integrity** (PG_INT_001-003)
   - Every `ViewOnly` member belongs to a view
   - Every view has at least one member
   - No orphaned views

5. **Import/Export** (PG_IMPORT_001, PG_EXPORT_001, PG_API_001-002)
   - Every foreign type has import
   - Every import references emitted type
   - Public APIs don't expose internal types

6. **Type Resolution** (PG_LOAD_001, PG_TYPEMAP_001)
   - All external types are in TypeIndex or built-in
   - No unsupported special forms (pointers, byrefs)

7. **Overload Collision** (PG_OL_001-002)
   - Overloads with same name and arity don't collide

8. **Constraint Integrity** (PG_CNSTR_001-004)
   - Generic constraints are satisfied
   - No impossible constraints

**Error Severity**:
- **ERROR**: Blocks emission, BuildResult.Success = false
- **WARNING**: Logged but doesn't block
- **INFO**: Diagnostic information

**Output**:
- Console log with error summary
- `.diagnostics.txt` file with full details
- `validation-summary.json` for CI comparison

**Integration**:
```csharp
// In Builder.Build
PhaseGate.Validate(ctx, graph, imports, constraintFindings);
if (ctx.Diagnostics.HasErrors)
{
    return new BuildResult { Success = false, ... };
}
```

### Library Mode: Filtered Package Generation

**Library Mode** (`--lib`) enables generating TypeScript declarations for user assemblies while excluding types from a base library package (e.g., BCL), producing clean, focused declaration packages.

**Use Case**: Library authors want to publish TypeScript declarations for their .NET library without duplicating thousands of BCL type declarations.

**Example**:
```bash
# Step 1: Generate base BCL package (once)
tsbindgen generate -d ~/dotnet/.../10.0.0 -o bcl-package/

# Step 2: Generate user library, excluding BCL types
tsbindgen generate -a MyLib.dll -d ~/dotnet/.../10.0.0 \
  --lib bcl-package/ -o my-lib-package/

# Result: my-lib-package/ contains ONLY MyLib types, BCL types excluded
```

**How It Works**:

1. **Contract Loading** (Phase 2: Normalize)
   - `LibraryContractLoader.Load(libraryPath)` reads base package:
     - `metadata.json` files → Extract type StableIds
     - `bindings.json` files → Extract member StableIds
   - Builds `LibraryContract` with `HashSet<string>` of StableIds
   - Validates LIB001: Contract directory exists, files present

2. **Graph Filtering** (Phase 2: Normalize, Post-Indexing)
   - `LibraryFilter.Filter(graph, contract)` processes each type:
     - If `type.StableId` is IN contract → **REMOVE** (base library type)
     - If `type.StableId` is NOT in contract → **KEEP** (user type)
   - Returns new `SymbolGraph` with only user types
   - O(1) lookup per type via `HashSet<string>`

3. **Validation** (Phase 4: Plan, PhaseGate)
   - **LIB002 (Dangling References)**: Validates no user type references a filtered-out type
     - Walks all type references (base types, interfaces, parameters, return types, generics)
     - Confirms every referenced type is either:
       - In the contract (allowed dependency)
       - In the current graph (user type)
       - Builtin/generic parameter (TypeScript primitive)
     - Fails with actionable error if dangling reference found
   - **LIB003 (Binding Consistency)**: Disabled in library mode (bindings validated separately)

**LibraryContract Structure**:
```csharp
public sealed record LibraryContract
{
    // Types IN the base library (to be excluded from emission)
    public required HashSet<string> AllowedTypeStableIds { get; init; }

    // Members IN the base library (for validation)
    public required HashSet<string> AllowedBindingStableIds { get; init; }
}
```

**Validation Error Example** (LIB002):
```
Dangling reference detected:
  User member:     MyLib:MyCompany.Utils.Calculator::DoWork():void
  References:      System.Data.SqlClient:System.Data.SqlClient.SqlConnection
  Location:        return type
  Fix:             Add BCL types to --lib package OR remove dependency on this BCL type
```

**Key Properties**:
- **No silent fallbacks**: Either package is self-consistent or build fails loudly
- **StableId-based matching**: Exact, assembly-qualified identity
- **Pre-Shape filtering**: Shape passes only process user types (performance + cleaner errors)
- **Contract immutability**: LibraryContract is loaded once, never modified
- **Strict validation**: LIB002 ensures no dangling references (type-safe package)

**Benefits**:
- Clean, focused packages (e.g., 1 namespace vs 131 with BCL)
- No type duplication across packages
- Modular package architecture (BCL → Framework → App)
- Different BCL versions for different user libraries
- Type-safe contract enforcement via StableIds

---

## 5. Directory Structure

The `src/tsbindgen/` directory is organized by pipeline phase:

```
src/tsbindgen/
├── Builder.cs        # Main orchestrator
├── BuildContext.cs              # Shared services container
│
├── Load/                        # Phase 1: Reflection
│   ├── AssemblyLoader.cs        # Transitive closure loading
│   ├── ReflectionReader.cs      # System.Reflection → SymbolGraph
│   ├── TypeReferenceFactory.cs  # Build TypeReference objects
│   └── InterfaceMemberSubstitution.cs  # Substitute closed generic interface members
│
├── Model/                       # Immutable data structures
│   ├── SymbolGraph.cs           # Root container
│   ├── Symbols/
│   │   ├── NamespaceSymbol.cs   # Namespace container
│   │   ├── TypeSymbol.cs        # Type metadata
│   │   └── MemberSymbols/       # Method, Property, Field, Event, Constructor
│   ├── Types/
│   │   ├── TypeReference.cs     # Type usage in signatures
│   │   └── NamedTypeReference.cs, GenericTypeReference.cs, etc.
│   └── AssemblyKey.cs           # Assembly identity
│
├── Normalize/                   # Phase 2: Build indices
│   ├── SignatureNormalization.cs     # Canonicalize signatures
│   ├── OverloadUnifier.cs            # Merge overload variants
│   └── NameReservation.cs            # Reserve all names in Renamer
│
├── Shape/                       # Phase 3: Transformations (passes 1-22)
│   ├── GlobalInterfaceIndex.cs       # Index all interfaces
│   ├── InterfaceDeclIndex.cs         # Index interface member declarations
│   ├── StructuralConformance.cs      # Analyze structural conformance
│   ├── InterfaceInliner.cs           # Flatten interface hierarchy
│   ├── ExplicitImplSynthesizer.cs    # Synthesize explicit impls
│   ├── DiamondResolver.cs            # Resolve diamond inheritance
│   ├── BaseOverloadAdder.cs          # Add base overloads
│   ├── StaticSideAnalyzer.cs         # Analyze static members
│   ├── IndexerPlanner.cs             # Mark indexers as Omitted
│   ├── HiddenMemberPlanner.cs        # Handle C# 'new' keyword
│   ├── FinalIndexersPass.cs          # Remove leaked indexers
│   ├── ClassSurfaceDeduplicator.cs   # Deduplicate class surface
│   ├── ConstraintCloser.cs           # Resolve generic constraints
│   ├── OverloadReturnConflictResolver.cs  # Resolve return type conflicts
│   ├── ViewPlanner.cs                # Plan explicit interface views
│   └── MemberDeduplicator.cs         # Final deduplication
│
├── Analysis/                    # Phase 3: Planning passes (pass 23)
│   ├── ExtensionBucketPlan.cs        # Extension method plan data structures
│   └── ExtensionMethodAnalyzer.cs    # Group extension methods by target type
│
├── Renaming/                    # Phase 3.5: Naming service
│   ├── SymbolRenamer.cs         # Central naming authority
│   ├── StableId.cs              # Identity types
│   ├── RenameScope.cs           # Scope types
│   ├── ScopeFactory.cs          # Scope construction
│   ├── RenameDecision.cs        # Rename decision record
│   ├── NameReservationTable.cs  # Per-scope name tracking
│   └── TypeScriptReservedWords.cs  # Keyword sanitization
│
├── Plan/                        # Phase 4: Import planning
│   ├── ImportGraph.cs           # Build dependency graph
│   ├── ImportPlanner.cs         # Plan imports/aliases
│   ├── EmitOrderPlanner.cs      # Stable emission order
│   ├── InterfaceConstraintAuditor.cs  # Audit constraints
│   ├── PhaseGate.cs             # Pre-emission validation
│   ├── TsAssignability.cs       # TypeScript assignability rules
│   ├── TsErase.cs               # Type erasure simulation
│   └── Validation/              # PhaseGate validation modules
│       ├── Core.cs              # Core validation logic
│       ├── Names.cs             # Name uniqueness validation
│       ├── Views.cs             # View integrity validation
│       ├── Scopes.cs            # Scope validation
│       ├── Types.cs             # Type resolution validation
│       ├── ImportExport.cs      # Import/export validation
│       ├── Constraints.cs       # Constraint validation
│       ├── Finalization.cs      # Finalization validation
│       └── Context.cs           # Validation context
│
└── Emit/                        # Phase 5: File generation
    ├── InternalIndexEmitter.cs  # internal/index.d.ts per namespace
    ├── ExtensionsEmitter.cs     # internal/extensions/index.d.ts (extension methods)
    ├── FacadeEmitter.cs         # facade/index.d.ts per namespace
    ├── MetadataEmitter.cs       # metadata.json per namespace
    ├── BindingEmitter.cs        # bindings.json per namespace
    ├── ModuleStubEmitter.cs     # index.js stubs per namespace
    ├── TypeMap.cs               # TypeReference → TypeScript type string
    └── TypeNameResolver.cs      # Resolve type names with imports
```

**Key Design Decisions**:

1. **Load/**: Pure reflection, no TypeScript concepts
2. **Model/**: Shared data structures across all phases
3. **Normalize/**: Index building and canonicalization
4. **Shape/**: 22 sequential transformations, each returns new graph
5. **Analysis/**: Planning passes (Pass 23: extension methods)
6. **Renaming/**: Centralized naming service, used by all phases
7. **Plan/**: Import analysis and validation gate
8. **Emit/**: File generation, no logic beyond string building

---

## 6. Build Context Services

`BuildContext` is the **immutable container** for all shared services. Created once at pipeline start, passed to every phase.

### Policy (Configuration)

```csharp
public sealed class GenerationPolicy
{
    // Naming transforms
    public NameTransformStrategy TypeNameTransform { get; init; }      // PascalCase, camelCase, etc.
    public NameTransformStrategy MemberNameTransform { get; init; }
    public Dictionary<string, string> ExplicitMap { get; init; }       // CLR name → TS name overrides

    // Emission filters
    public bool IncludeInternalTypes { get; init; }     // Emit internal types?
    public bool EmitDocumentation { get; init; }        // Emit XML doc comments?
    public bool EmitDebugInfo { get; init; }            // Emit metadata tokens?

    // Branded types
    public bool UseBrandedPrimitives { get; init; }     // int vs number?

    // Import style
    public ImportStyle ImportStyle { get; init; }       // ES6 vs namespace
}
```

**Creation**:
```csharp
var policy = PolicyDefaults.Create;
var ctx = BuildContext.Create(policy, logger, verboseLogging, logCategories);
```

### SymbolRenamer (Naming Service)

The **central naming authority** for all TypeScript identifiers.

**Responsibilities**:
1. **Reserve names** in scopes (namespace, class, view)
2. **Apply style transforms** (PascalCase, camelCase, etc.)
3. **Resolve conflicts** via numeric suffixes (ToString2, ToString3)
4. **Sanitize reserved words** (class → class_, interface → interface_)
5. **Track rename decisions** with full provenance

**Key Methods**:
```csharp
// Reserve type name
void ReserveTypeName(StableId id, string requested, NamespaceScope scope, string reason);

// Reserve member name
void ReserveMemberName(StableId id, string requested, RenameScope scope, string reason, bool isStatic);

// Lookup finalized name
string GetFinalTypeName(TypeSymbol type, NamespaceArea area);
string GetFinalMemberName(StableId id, RenameScope scope);

// Query
bool HasFinalTypeName(StableId id, NamespaceScope scope);
bool HasFinalMemberName(StableId id, TypeScope scope);
```

**Scope Separation**:
- Class surface: `type:System.Decimal#instance`
- View surface: `view:CoreLib:System.Decimal:CoreLib:IConvertible#instance`
- Each scope has independent naming

**Decision Recording**:
```csharp
public sealed record RenameDecision
{
    public StableId Id { get; init; }           // What was renamed
    public string Requested { get; init; }       // What was asked for
    public string Final { get; init; }           // What was decided
    public string From { get; init; }            // Original CLR name
    public string Reason { get; init; }          // Why this decision
    public string DecisionSource { get; init; }  // Which component decided
    public string Strategy { get; init; }        // "None", "NumericSuffix", "Sanitize"
    public string ScopeKey { get; init; }        // Which scope
    public bool? IsStatic { get; init; }         // Static vs instance
}
```

Decisions are emitted to `bindings.json` for runtime binding.

### DiagnosticBag (Error Collection)

Collects errors, warnings, and info messages throughout pipeline.

**Methods**:
```csharp
void Error(string code, string message);
void Warning(string code, string message);
void Info(string code, string message);

bool HasErrors;
IReadOnlyList<Diagnostic> GetAll;
```

**Diagnostic Format**:
```csharp
public sealed record Diagnostic
{
    public DiagnosticSeverity Severity { get; init; }  // Error, Warning, Info
    public string Code { get; init; }                   // "PG_FIN_003", "TS2417"
    public string Message { get; init; }
    public string? Location { get; init; }              // File/type/member
}
```

**Usage**:
```csharp
if (member.TsEmitName == "")
{
    ctx.Diagnostics.Error("PG_FIN_003",
        $"Member {member.StableId} has empty TsEmitName");
}
```

### Interner (String Deduplication)

Reduces memory usage by interning common strings.

**Method**:
```csharp
string Intern(string value);
```

**Usage**:
```csharp
var internedName = ctx.Interner.Intern(type.ClrFullName);
```

Typical savings: 30-40% memory reduction for large BCL assemblies.

### Logger (Progress Reporting)

Optional logging for debugging and progress tracking.

**Method**:
```csharp
void Log(string category, string message);
```

**Categories**:
- `"Build"` - High-level pipeline progress
- `"Load"` - Assembly loading
- `"Shape"` - Transformation passes
- `"ViewPlanner"` - View planning
- `"PhaseGate"` - Validation
- `"Emit"` - File generation

**Filtering**:
```csharp
// Enable all logging
var ctx = BuildContext.Create(policy, logger, verboseLogging: true);

// Enable specific categories
var logCategories = new HashSet<string> { "PhaseGate", "ViewPlanner" };
var ctx = BuildContext.Create(policy, logger, verboseLogging: false, logCategories);
```

**Usage**:
```csharp
ctx.Log("ViewPlanner", $"Planning views for {type.ClrFullName}");
```

---

## Current Validation Metrics

### TypeScript Validation Results

Full BCL generation (4,047 types, 130 namespaces, 50,720 members):

**Total TypeScript errors**: **198** (down from 1,471, **-86.5%** reduction)

**Error breakdown**:
- **0 syntax errors** (TS1xxx) - All output is valid TypeScript ✓
- **198 semantic errors** (TS2xxx) - Known .NET/TypeScript impedance mismatches

**Semantic error categories**:
- **146 TS2416** (73.7%) - Property covariance (C# allows covariant returns, TypeScript doesn't)
- **25 TS2417** (12.6%) - Override mismatches (C# virtual/override model != TypeScript structural checking)
- **24 TS2344** (12.1%) - Generic constraint violations (F-bounded types like `INumber<TSelf>`)
- **2 TS2315** (1.0%) - Type not generic (Array/System.Array shadowing edge case)
- **1 TS2440** (0.5%) - Abstract member in concrete class (edge case)

**Errors eliminated in recent work**:
- **TS2304**: 212 → **0** (100% eliminated) - "Cannot find name" errors
- **TS2420**: 579 → **0** (100% eliminated) - "Class incorrectly implements interface" errors
- **TS2552**: 5 → **0** (100% eliminated) - "Cannot find name" errors (different context)
- **TS2416**: 794 → 146 (81.6% reduction) - Property assignability issues

**Key fixes implemented**:
1. **Non-public interface filtering** - Eliminated TS2420 cascade by filtering non-public interfaces from implements clauses
2. **Cross-namespace qualification** - Fixed TS2304 in facades, view interfaces, and nested types
3. **Free type variable detection** - Demoted leaked generic parameters to `unknown`
4. **CLROf<T> primitive lifting** - Enabled primitives in generic positions with proper constraint satisfaction
5. **Recursive nested type analysis** - Ensured imports for nested type members (e.g., `ImmutableArray<T>.Builder`)
6. **Constructor parameter checking** - Added support type detection for constructor parameters
7. **Unified alias emission** - Prevented TS2315 errors from LHS/RHS arity mismatches

**Data integrity**: **100%** - All reflected types accounted for in emission (verified via completeness checks)

**Remaining 198 errors represent genuine .NET/TypeScript impedance**:
- Property covariance (C# feature TypeScript doesn't support)
- Override patterns (C# virtual/override semantics differ from TypeScript structural checking)
- F-bounded generic constraints (`INumber<TSelf>` patterns)
- Known edge cases (array shadowing, abstract member handling)

These errors are **documented as acceptable impedance** and do not block emission or functionality.

---

## Summary

The tsbindgen pipeline is a **deterministic, pure functional transformation** from .NET assemblies to TypeScript declarations:

1. **Load**: System.Reflection → SymbolGraph (pure CLR data)
2. **Normalize**: Build indices for fast lookup + library mode filtering (if `--lib`)
3. **Shape**: 23 transformations to handle .NET/TypeScript impedance mismatches (includes extension method analysis)
4. **Name Reservation**: Reserve all names through SymbolRenamer
5. **Plan**: Analyze dependencies, plan imports, validate everything (PhaseGate + library mode validation)
6. **Emit**: Generate TypeScript files with metadata sidecars and extension method buckets

**Core Principles**:
- **Immutability**: All data structures are immutable records
- **Purity**: All transformations are pure functions
- **Centralization**: All shared state lives in BuildContext
- **Identity**: StableIds provide stable identity across pipeline
- **Scoping**: Separate naming scopes for class vs view surfaces
- **Validation**: PhaseGate enforces 50+ invariants before emission
- **Modularity**: Library mode enables filtered package generation

**Result**: 100% data integrity, zero data loss, type-safe TypeScript declarations for the entire .NET BCL (4,047 types, 130 namespaces, **198 known impedance errors**).

**Library Mode**: Optionally filter base library types for clean, focused user library packages with strict contract validation.
