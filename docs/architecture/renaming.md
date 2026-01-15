# Renaming System

The renaming system manages all TypeScript identifier generation.

## SymbolRenamer

**File:** `Renaming/SymbolRenamer.cs`

Central naming authority:

```csharp
public sealed class SymbolRenamer
{
    // Style transforms
    public void AdoptTypeStyleTransform(Func<string, string> transform);
    public void AdoptMemberStyleTransform(Func<string, string> transform);

    // Reservation
    public void ReserveTypeName(StableId id, string requested, RenameScope scope, ...);
    public void ReserveMemberName(StableId id, string requested, RenameScope scope, ...);

    // Querying
    public string GetFinalTypeName(TypeSymbol type);
    public string GetInstanceTypeName(TypeSymbol type);   // T$instance
    public string GetStaticInterfaceName(TypeSymbol type); // T$static
    public string GetFinalMemberName(StableId id, RenameScope scope);
}
```

## Scopes

**File:** `Renaming/RenameScope.cs`

Names are reserved within scopes:

```csharp
public abstract record RenameScope(string ScopeKey);

public sealed record NamespaceScope(
    string NamespaceName,
    NamespaceArea Area     // Internal or Facade
) : RenameScope($"ns:{NamespaceName}#{Area}");

public sealed record TypeScope(
    string TypeClrName,
    bool IsStatic,
    bool IsView,
    string? ViewInterfaceId
) : RenameScope(...);
```

**Scope examples:**
- `ns:System.Collections.Generic#internal` - Namespace scope
- `type:System.Collections.Generic.List\`1#instance` - Instance members
- `type:System.Collections.Generic.List\`1#static` - Static members
- `view:System.Collections.Generic.List\`1:IEnumerable\`1#instance` - View members

## ScopeFactory

**File:** `Renaming/ScopeFactory.cs`

Creates properly-formatted scopes:

```csharp
public static class ScopeFactory
{
    public static NamespaceScope Namespace(string name, NamespaceArea area = Internal);
    public static TypeScope ClassSurface(TypeSymbol type, bool isStatic);
    public static TypeScope ViewSurface(TypeSymbol type, StableId interfaceId, bool isStatic);
}
```

## Name Reservation Table

**File:** `Renaming/NameReservationTable.cs`

Tracks reserved names per scope:

```csharp
public sealed class NameReservationTable
{
    public bool TryReserve(string name, StableId owner);
    public bool IsReserved(string name);
    public int AllocateNextSuffix(string baseName);
}
```

## Conflict Resolution

When a name is taken, numeric suffixes are added:

```
add -> add       (first)
add -> add2      (conflict)
add -> add3      (conflict)
```

For explicit interface implementations, interface name is used:

```
clear -> clear             (own method)
clear -> clear_ICollection (explicit impl)
```

## TypeScript Reserved Words

**File:** `Renaming/TypeScriptReservedWords.cs`

```csharp
private static readonly HashSet<string> Reserved = new()
{
    // Keywords
    "break", "case", "catch", "class", "const", "continue",
    "debugger", "default", "delete", "do", "else", "enum",
    "export", "extends", "false", "finally", "for", "function",
    "if", "import", "in", "instanceof", "new", "null", "return",
    "super", "switch", "this", "throw", "true", "try", "typeof",
    "var", "void", "while", "with",

    // Strict mode
    "implements", "interface", "let", "package", "private",
    "protected", "public", "static", "yield",

    // Future reserved
    "await", "async"
};

public static (string Sanitized, bool WasSanitized) Sanitize(string name)
{
    if (Reserved.Contains(name))
        return (name + "_", true);
    return (name, false);
}
```

## Style Transforms

### CLR Style (default)

No transformation - PascalCase preserved:

```csharp
Renamer.AdoptMemberStyleTransform(name => name);

// GetEnumerator -> GetEnumerator
// WriteLine -> WriteLine
```

### JavaScript Style (`--naming js`)

camelCase transformation:

```csharp
Renamer.AdoptMemberStyleTransform(name => ToCamelCase(name));

// GetEnumerator -> getEnumerator
// WriteLine -> writeLine
// XMLReader -> xmlReader
```

## Rename Decisions

Every rename is recorded:

```csharp
public sealed record RenameDecision
{
    public StableId Id { get; }
    public string Requested { get; }      // Original name
    public string Final { get; }          // Final TypeScript name
    public string From { get; }           // Base name (without suffixes)
    public string Reason { get; }         // Why this decision
    public string DecisionSource { get; } // Which component made decision
    public string Strategy { get; }       // None, NumericSuffix, OverloadFamily
    public string ScopeKey { get; }
    public bool? IsStatic { get; }
}
```

**Strategies:**
- `None` - No rename needed
- `NumericSuffix` - Added numeric suffix (add2, add3)
- `ReservedWord` - Sanitized an Identifier with `_` (example: parameter `default` → `default_`; member names can be keywords and are emitted as-is)
- `OverloadFamily` - Shares name with overload family
