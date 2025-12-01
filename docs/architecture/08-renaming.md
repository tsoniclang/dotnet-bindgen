# Renaming System

This document details the renaming system, which handles all TypeScript identifier generation and conflict resolution.

## Overview

**Purpose:** Allocate unique TypeScript identifiers for all symbols

The renaming system is the single source of truth for all TypeScript names in the generated output. It handles:

- Style transforms (CLR PascalCase vs JavaScript camelCase)
- Reserved word sanitization
- Conflict resolution via numeric suffixes
- Scope-based name reservation
- Deterministic output

**Key Files:**
- `Renaming/SymbolRenamer.cs` - Central naming authority
- `Renaming/TypeScriptReservedWords.cs` - Reserved word list
- `Renaming/ScopeFactory.cs` - Scope creation utilities
- `Renaming/RenameDecision.cs` - Decision record

## SymbolRenamer

The central class for all naming operations:

```csharp
public sealed class SymbolRenamer
{
    // Configuration
    private readonly Func<string, string> _typeStyleTransform;
    private readonly Func<string, string> _memberStyleTransform;

    // State
    private readonly Dictionary<string, NameReservationTable> _tablesByScope;
    private readonly Dictionary<(StableId, string), RenameDecision> _decisions;
}
```

### Configuration Methods

```csharp
// Configure style transforms
renamer.SetTypeStyleTransform(s => s);           // Identity (CLR mode)
renamer.SetMemberStyleTransform(CamelCase.ToCamelCase);  // camelCase (JS mode)
```

### Reservation Methods

```csharp
// Reserve a type name
string final = renamer.ReserveTypeName(stableId, requested, scope);

// Reserve a member name
string final = renamer.ReserveMemberName(stableId, requested, scope, isStatic, ...);
```

### Query Methods

```csharp
// Get final type name
string name = renamer.GetFinalTypeName(typeSymbol);

// Get instance type name (with $instance suffix)
string name = renamer.GetInstanceTypeName(typeSymbol);

// Get final member name
string name = renamer.GetFinalMemberName(memberStableId, scope);

// Check if decision exists
bool exists = renamer.TryGetDecision(stableId, scope, out var decision);
```

## Scope System

Names are reserved within scopes to prevent collisions:

### Scope Types

| Scope | Format | Example |
|-------|--------|---------|
| Namespace | `ns:{namespace}:{area}` | `ns:System.Collections:internal` |
| Type Instance | `type:{stableId}#instance` | `type:System.Private.CoreLib:List`1#instance` |
| Type Static | `type:{stableId}#static` | `type:System.Private.CoreLib:List`1#static` |
| View | `view:{typeId}:{ifaceId}#instance` | `view:List`1:ICollection`1#instance` |

### ScopeFactory

Use `ScopeFactory` to create scopes:

```csharp
// Namespace scope
var scope = ScopeFactory.Namespace("System.Collections", NamespaceArea.Internal);

// Type scopes
var instanceScope = ScopeFactory.ClassSurface(type, isStatic: false);
var staticScope = ScopeFactory.ClassSurface(type, isStatic: true);

// View scope
var viewScope = ScopeFactory.ViewSurface(type, interfaceStableId, isStatic: false);
```

### Namespace Areas

Namespaces have two areas:

- `Internal` - Full declarations (`internal/index.d.ts`)
- `Facade` - Public exports (`index.d.ts`)

## Name Resolution Algorithm

```
ReserveNameWithConflicts(stableId, requested, table, scope, ...)
    │
    ├─► Step 1: Check explicit overrides
    │   └─► User-specified renames take priority
    │   └─► If found and available: return explicit name
    │
    ├─► Step 2: Apply style transform
    │   └─► memberStyleTransform for members
    │   └─► typeStyleTransform for types
    │   └─► Example: GetEnumerator → getEnumerator (JS mode)
    │
    ├─► Step 3: Sanitize reserved words
    │   └─► Check TypeScriptReservedWords list
    │   └─► Append underscore: class → class_
    │
    ├─► Step 4: Try to reserve
    │   └─► Check reservation table for scope
    │   └─► If available: return name
    │
    └─► Step 5: Resolve conflict
        │
        ├─► For explicit interface implementations:
        │   └─► Append interface suffix: syncRoot_ICollection
        │
        └─► For other conflicts:
            └─► Append numeric suffix: add2, add3, ...
```

## Style Transforms

### CLR Mode (default)

No transformation - preserve C# naming:

```csharp
memberStyleTransform = s => s;

// Input: GetEnumerator
// Output: GetEnumerator
```

### JavaScript Mode (`--naming js`)

Transform members to camelCase:

```csharp
memberStyleTransform = CamelCase.ToCamelCase;

// Input: GetEnumerator
// Output: getEnumerator

// Input: XMLReader
// Output: xmlReader

// Input: ID
// Output: id
```

### Type Names

**Type names are NEVER transformed** regardless of mode:

```csharp
typeStyleTransform = s => s;  // Always identity

// List_1 stays List_1
// IEnumerable_1 stays IEnumerable_1
```

## Reserved Words

TypeScript reserved words are sanitized by appending underscore:

```csharp
private static readonly HashSet<string> ReservedWords = new()
{
    // JavaScript keywords
    "break", "case", "catch", "class", "const", "continue",
    "debugger", "default", "delete", "do", "else", "enum",
    "export", "extends", "false", "finally", "for", "function",
    "if", "import", "in", "instanceof", "new", "null", "return",
    "super", "switch", "this", "throw", "true", "try", "typeof",
    "var", "void", "while", "with", "yield",

    // TypeScript keywords
    "let", "static", "implements", "interface", "package",
    "private", "protected", "public", "as", "async", "await",
    "constructor", "get", "set", "from", "of",

    // TypeScript type keywords
    "namespace", "module", "declare", "abstract", "any",
    "boolean", "never", "number", "object", "string",
    "symbol", "unknown", "type", "readonly"
};
```

**Examples:**
```
class     → class_
type      → type_
namespace → namespace_
readonly  → readonly_
default   → default_
```

## Conflict Resolution

### Numeric Suffix Strategy

When a name is already taken:

```
Original: add
Conflict: add already reserved
Result: add2

Next conflict: add3, add4, ...
```

### Interface Suffix Strategy

For explicit interface implementations:

```
Original: syncRoot (from ICollection)
Conflict: syncRoot already reserved (class surface)
Result: syncRoot_ICollection
```

## RenameDecision

Every naming decision is recorded:

```csharp
public sealed record RenameDecision
{
    public StableId Id { get; init; }
    public string Requested { get; init; }   // Original requested name
    public string Final { get; init; }       // Final TypeScript name
    public string From { get; init; }        // Original CLR name
    public string Reason { get; init; }      // Why this name
    public string DecisionSource { get; init; }  // Which pass made decision
    public string Strategy { get; init; }    // Resolution strategy
    public string ScopeKey { get; init; }    // Scope where reserved
    public bool? IsStatic { get; init; }     // For member decisions
}
```

### Decision Sources

- `NameReservation` - Default source
- `HiddenMemberPlanner` - C# `new` keyword handling
- `IndexerPlanner` - Indexer to method conversion
- `OverloadUnifier` - Overload family unification

### Strategy Types

- `None` - Name was available
- `NumericSuffix` - Conflict resolved with numeric suffix
- `InterfaceSuffix` - Conflict resolved with interface suffix
- `OverloadFamily` - Part of method overload family

## Type Name Patterns

### Standard Types

```
ClrName          TsEmitName        InstanceName
─────────────────────────────────────────────────
List`1           List_1            List_1$instance
Dictionary`2     Dictionary_2      Dictionary_2$instance
Exception        Exception         Exception$instance
Console          Console           Console$instance
```

### Nested Types

```
ClrName                          TsEmitName
─────────────────────────────────────────────────────────────
List`1+Enumerator                List_1$Enumerator
Dictionary`2+KeyCollection       Dictionary_2$KeyCollection
```

### Special Suffixes

| Suffix | Purpose |
|--------|---------|
| `$instance` | Instance interface |
| `$static` | Static interface |
| `$views` | Views companion interface |
| `$As_IInterface` | View interface for specific interface |

## Reservation Tables

Each scope has its own reservation table:

```csharp
public sealed class NameReservationTable
{
    private readonly Dictionary<string, StableId> _reservations;

    public bool TryReserve(string name, StableId id);
    public bool IsReserved(string name);
    public StableId? GetReserver(string name);
}
```

### Thread Safety

The renaming system is NOT thread-safe. All operations must happen on a single thread, which is enforced by the pipeline architecture.

## Integration with Pipeline

```
Load Phase
    │
    ▼
Shape Phase
    │
    ▼
Normalize Phase ────────┐
    │                   │
    ▼                   ▼
Name Reservation   SymbolRenamer
    │                   │
    └───────────────────┘
            │
            ▼
    SymbolGraph with TsEmitName
            │
            ▼
       Plan Phase
            │
            ▼
       Emit Phase
            │
            ▼
    Query Renamer for names
```

## Querying Names at Emit Time

During emission, names are queried (never generated):

```csharp
// In ClassPrinter
var finalName = ctx.Renamer.GetFinalTypeName(type);
var instanceName = ctx.Renamer.GetInstanceTypeName(type);

// In MethodPrinter
var scope = ScopeFactory.ClassSurface(type, isStatic: false);
var methodName = ctx.Renamer.GetFinalMemberName(method.StableId, scope);
```

## Determinism

The renaming system guarantees deterministic output:

1. **Sorted inputs** - Types and members processed in CLR name order
2. **Stable IDs** - Decisions keyed by immutable stable identifiers
3. **Order independence** - Same result regardless of processing order
4. **No external state** - All state is internal to the renamer

This ensures that running tsbindgen twice with the same input produces identical output.
