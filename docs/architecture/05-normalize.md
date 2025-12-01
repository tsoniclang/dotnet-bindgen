# Normalize Phase

This document details the Normalize phase (Phase 3.5), which reserves TypeScript names and unifies method overloads.

## Overview

**Purpose:** Allocate TypeScript identifiers for all symbols and unify method overloads

**Input:** Transformed SymbolGraph (from Shape phase)
**Output:** SymbolGraph with `TsEmitName` assigned on all symbols

**Key Files:**
- `Normalize/NameReservation.cs` - Main entry point
- `Normalize/Naming/Reservation.cs` - Member name reservation
- `Normalize/Naming/Application.cs` - Apply names to graph
- `Normalize/OverloadUnifier.cs` - Unify method overloads
- `Renaming/SymbolRenamer.cs` - Central naming authority

## NameReservation

The main entry point for name allocation:

```
ReserveAllNames(ctx, graph)
    │
    ├─► Phase 1: Reserve names in Renamer
    │   │
    │   └─► For each namespace:
    │       └─► For each type:
    │           ├─► Reserve type name
    │           ├─► Reserve class surface member names
    │           └─► Reserve view member names
    │
    ├─► Audit: Verify all emitted members have decisions
    │
    └─► Phase 2: Apply names to graph
        └─► Application.ApplyNamesToGraph()
```

### Name Reservation Order

Names are reserved in a deterministic order:

1. **Types** ordered by `ClrFullName`
2. **Members** ordered by `StableId`
3. **Views** processed after class surface

This ensures reproducible output across runs.

## SymbolRenamer

The central naming authority for the entire pipeline:

```csharp
public sealed class SymbolRenamer
{
    // Per-scope reservation tables
    private readonly Dictionary<string, NameReservationTable> _tablesByScope;

    // All rename decisions (keyed by StableId + ScopeKey)
    private readonly Dictionary<(StableId, string), RenameDecision> _decisions;

    // Style transforms (configured from CLI options)
    private Func<string, string> _typeStyleTransform;
    private Func<string, string> _memberStyleTransform;
}
```

### Scope System

Names are reserved in scopes to prevent collisions:

| Scope Type | Format | Example |
|------------|--------|---------|
| Namespace | `ns:{namespace}:{area}` | `ns:System.Collections:internal` |
| Class (instance) | `type:{stableId}#instance` | `type:System.Private.CoreLib:List`1#instance` |
| Class (static) | `type:{stableId}#static` | `type:System.Private.CoreLib:List`1#static` |
| View | `view:{typeId}:{ifaceId}#instance` | `view:List`1:ICollection`1#instance` |

### Scope Factory

Create scopes correctly using `ScopeFactory`:

```csharp
// Namespace scope
var nsScope = ScopeFactory.Namespace("System.Collections", NamespaceArea.Internal);

// Class surface scopes
var instanceScope = ScopeFactory.ClassSurface(typeSymbol, isStatic: false);
var staticScope = ScopeFactory.ClassSurface(typeSymbol, isStatic: true);

// View scope
var viewScope = ScopeFactory.ViewSurface(typeSymbol, interfaceStableId, isStatic: false);
```

### Name Resolution Algorithm

```
ResolveNameWithConflicts(stableId, requested, table, scope, ...)
    │
    ├─► Check explicit overrides (user-specified renames)
    │   └─► If found and available: return explicit name
    │
    ├─► Apply style transform
    │   └─► memberStyleTransform for members (camelCase if --naming js)
    │   └─► typeStyleTransform for types (identity - no transform)
    │
    ├─► Sanitize TypeScript reserved words
    │   └─► "class" → "class_"
    │   └─► "type" → "type_"
    │   └─► etc.
    │
    ├─► Try to reserve sanitized name
    │   └─► If available: return sanitized name
    │
    └─► Conflict detected:
        │
        ├─► For explicit interface implementations:
        │   └─► Try: <base>_<InterfaceShortName>
        │   └─► Example: syncRoot_ICollection
        │
        └─► Otherwise: numeric suffix
            └─► <base>2, <base>3, ...
```

### Style Transforms

The Renamer applies style transforms based on CLI options:

**CLR Mode (default):**
```csharp
// No transform - preserve C# names
memberStyleTransform = s => s;
// GetEnumerator stays GetEnumerator
```

**JavaScript Mode (`--naming js`):**
```csharp
// camelCase transform for members
memberStyleTransform = CamelCase.ToCamelCase;
// GetEnumerator becomes getEnumerator
```

**Type names are NEVER transformed** - they stay PascalCase in both modes.

## Reserved Words

TypeScript reserved words are sanitized by appending underscore:

```csharp
// From TypeScriptReservedWords.cs
private static readonly HashSet<string> ReservedWords = new()
{
    "break", "case", "catch", "class", "const", "continue", "debugger", "default",
    "delete", "do", "else", "enum", "export", "extends", "false", "finally",
    "for", "function", "if", "import", "in", "instanceof", "new", "null",
    "return", "super", "switch", "this", "throw", "true", "try", "typeof",
    "var", "void", "while", "with", "yield",
    "let", "static", "implements", "interface", "package", "private", "protected",
    "public", "as", "async", "await", "constructor", "get", "set",
    "from", "of", "namespace", "module", "declare", "abstract", "any", "boolean",
    "never", "number", "object", "string", "symbol", "unknown", "type", "readonly"
};
```

**Examples:**
- `class` → `class_`
- `type` → `type_`
- `namespace` → `namespace_`
- `readonly` → `readonly_`

## Member Reservation

### Class Surface Members

Members with `EmitScope.ClassSurface` or `EmitScope.StaticSurface`:

```
ReserveMemberNamesOnly(ctx, type)
    │
    └─► For each member by EmitScope:
        │
        ├─► ClassSurface members
        │   ├─► Instance: reserve in type#instance scope
        │   └─► Static: reserve in type#static scope
        │
        └─► StaticSurface members
            └─► Reserve in type#static scope
```

### View Members

Members with `EmitScope.ViewOnly`:

```
ReserveViewMemberNamesOnly(ctx, graph, type, classAllNames)
    │
    └─► For each ExplicitView:
        │
        ├─► Get view scope (view:{type}:{interface}#instance)
        │
        └─► For each ViewMember:
            │
            ├─► Check class collision
            │   └─► If TsEmitName in classAllNames: add suffix
            │
            └─► Reserve in view scope
```

**View Collision Resolution:**

When a view member's name collides with a class surface member, a numeric suffix is added:

```typescript
interface Decimal$instance {
    toByte(): byte;  // Class surface member
}

interface __Decimal$As_IConvertible {
    toByte2(provider: IFormatProvider): byte;  // View member with suffix
}
```

## Overload Unification

After name reservation, the Plan phase runs `OverloadUnifier`:

```
UnifyOverloads(ctx, graph)
    │
    └─► For each type:
        └─► Group methods by final TsEmitName
            │
            ├─► Single method: keep as-is
            │
            └─► Multiple methods (overloads):
                ├─► Keep first as ClassSurface
                └─► Mark rest as Omitted (unified away)
```

TypeScript supports function overloads natively, so all overloads share the same name:

```typescript
// All overloads of Add become:
add(item: T): void;
add(index: int, item: T): void;
```

## RenameDecision

Every naming decision is recorded:

```csharp
public sealed record RenameDecision
{
    public StableId Id { get; init; }       // Symbol identifier
    public string Requested { get; init; }   // Original requested name
    public string Final { get; init; }       // Final TypeScript name
    public string From { get; init; }        // Original CLR name
    public string Reason { get; init; }      // Why this name was chosen
    public string DecisionSource { get; init; } // Which pass made decision
    public string Strategy { get; init; }    // "None", "NumericSuffix", "OverloadFamily"
    public string ScopeKey { get; init; }    // Scope where reserved
    public bool? IsStatic { get; init; }     // For member decisions
}
```

### Decision Sources

- `NameReservation` - Default source for most names
- `HiddenMemberPlanner` - C# `new` keyword handling
- `IndexerPlanner` - Indexer to method conversion
- `OverloadUnifier` - Overload family unification

## Audit

After reservation, an audit verifies completeness:

```
AuditReservationCompleteness(ctx, graph)
    │
    └─► For each type:
        └─► For each emittable member:
            └─► Verify TryGetDecision returns true
                └─► Error if no decision found
```

This ensures PhaseGate won't fail due to missing names.

## Application

Finally, names are applied to the graph:

```
ApplyNamesToGraph(ctx, graph)
    │
    └─► For each namespace:
        └─► For each type:
            ├─► Set type.TsEmitName
            │
            └─► For each member:
                └─► Set member.TsEmitName
```

This creates a new graph with all `TsEmitName` fields populated.

## Querying Names

After reservation, names are queried via the Renamer:

```csharp
// Type names
ctx.Renamer.GetFinalTypeName(type)           // "List_1"
ctx.Renamer.GetInstanceTypeName(type)        // "List_1$instance"
ctx.Renamer.GetStaticInterfaceName(type)     // "List_1$static"
ctx.Renamer.GetStaticValueName(type)         // "List_1"

// Member names (scope-aware)
var scope = ScopeFactory.ClassSurface(type, isStatic: false);
ctx.Renamer.GetFinalMemberName(member.StableId, scope)  // "add"
```

**Important:** Never construct names manually. Always go through the Renamer.

## Call Graph

```
Builder.Build()
    │
    └─► NameReservation.ReserveAllNames()
        │
        ├─► For each namespace/type/member:
        │   └─► Renamer.ReserveTypeName()
        │   └─► Renamer.ReserveMemberName()
        │
        ├─► Audit.AuditReservationCompleteness()
        │
        └─► Application.ApplyNamesToGraph()
            └─► For each symbol:
                └─► symbol with { TsEmitName = ... }

[In Plan phase]
    │
    └─► OverloadUnifier.UnifyOverloads()
        └─► For each overload family:
            └─► Renamer.RecordOverloadDecision()
```
