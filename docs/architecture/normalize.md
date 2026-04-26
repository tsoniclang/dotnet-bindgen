# Normalize Phase

The Normalize phase reserves all TypeScript identifiers and resolves naming conflicts.

## Entry Point

**File:** `Normalize/NameReservation.cs`

```csharp
public static void ReserveAll(BuildContext ctx, SymbolGraph graph)
{
    // 1. Reserve type names in namespace scopes
    foreach (var ns in graph.Namespaces)
    {
        var scope = ScopeFactory.Namespace(ns.Name);
        foreach (var type in ns.Types)
        {
            ctx.Renamer.ReserveTypeName(
                type.StableId,
                type.TsEmitName,
                scope,
                "TypeReservation");
        }
    }

    // 2. Reserve member names in type scopes
    foreach (var type in graph.AllTypes)
    {
        ReserveMembers(ctx, type);
    }
}
```

## Scope System

Names are reserved in hierarchical scopes:

```
Namespace Scope: "ns:System.Collections.Generic#internal"
    └── Type names (List_1, Dictionary_2, ...)

Type Scope: "type:System.Collections.Generic.List`1#instance"
    └── Instance member names (Add, Remove, Count, ...)

Type Scope: "type:System.Collections.Generic.List`1#static"
    └── Static member names (Empty, ...)

View Scope: "view:System.Collections.Generic.List`1:IEnumerable`1#instance"
    └── View member names (GetEnumerator, ...)
```

## Conflict Resolution

When a name is already taken in a scope, numeric suffixes are added:

```csharp
// First reservation
"Add" -> "Add"

// Conflict - add suffix
"Add" -> "Add2"

// Another conflict
"Add" -> "Add3"
```

## Naming Style

tsbindgen emits **CLR-faithful names**. The renamer’s style transforms are
identity transforms, and there is no `--naming` option.

## Reserved Word Handling

TypeScript reserved words are sanitized **only** when they appear in **Identifier** contexts (for example: parameters, locals, or other places where the token must parse as an identifier).

Member names (methods/properties/enum members) are emitted in **IdentifierName** positions, so keywords are allowed and are emitted as-is (no `_` suffix).

Examples:

- Parameter/local: `default` → `default_`
- Member: `.default(...)` stays `.default(...)`

## Rename Decisions

Every rename is recorded for bindings generation:

```csharp
public sealed record RenameDecision
{
    public StableId Id { get; }
    public string Requested { get; }      // Original name
    public string Final { get; }          // Final TypeScript name
    public string Reason { get; }         // Why renamed
    public string Strategy { get; }       // None, NumericSuffix, ReservedWord
    public string ScopeKey { get; }
    public bool? IsStatic { get; }
}
```

## Querying Final Names

After normalization, all names are queryable:

```csharp
// Type names
string finalName = ctx.Renamer.GetFinalTypeName(type);
string instanceName = ctx.Renamer.GetInstanceTypeName(type);  // T$instance
string staticName = ctx.Renamer.GetStaticInterfaceName(type); // T$static

// Member names
var scope = ScopeFactory.ClassSurface(type, isStatic: false);
string memberName = ctx.Renamer.GetFinalMemberName(member.StableId, scope);
```

## Explicit Interface Members

Explicit interface implementations get interface-suffixed names:

```csharp
// C#: void ICollection.Clear() { }
// Requested: "clear" (conflicts with own Clear method)
// Final: "clear_ICollection"
```
