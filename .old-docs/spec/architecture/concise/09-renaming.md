# Phase 9: Renaming System

## Overview

The **Renaming system** provides centralized naming authority for all TypeScript identifiers. Manages scope-based naming, conflict resolution, reserved word sanitization, and rename decision tracking.

**Key Components:**
- **SymbolRenamer:** Central naming authority
- **StableId:** Permanent identity (TypeStableId, MemberStableId)
- **RenameScope:** Scope types (namespace, class, view)
- **RenameDecision:** Rename decision record with full provenance

---

## File: SymbolRenamer.cs

### Purpose
Central naming authority. All TypeScript identifiers reserved through this service.

**Responsibilities:**
1. Reserve names in scopes (namespace, class, view)
2. Apply style transforms (PascalCase, camelCase, etc.)
3. Resolve conflicts via numeric suffixes (ToString2, ToString3)
4. Sanitize reserved words (class → class_, interface → interface_)
5. Track rename decisions with full provenance

### Key Methods

**`ReserveTypeName(StableId id, string requested, NamespaceScope scope, string reason): void`**
- Reserve type name in namespace scope
- Apply transforms and conflict resolution
- Create RenameDecision with provenance

**`ReserveMemberName(StableId id, string requested, RenameScope scope, string reason, bool isStatic): void`**
- Reserve member name in class/view scope
- Separate scopes for static vs instance
- Create RenameDecision with provenance

**`GetFinalTypeName(TypeSymbol type, NamespaceArea area): string`**
- Lookup finalized TypeScript name for type
- Throws if not reserved

**`GetFinalMemberName(StableId id, RenameScope scope): string`**
- Lookup finalized TypeScript name for member
- Throws if not reserved

**`HasFinalTypeName(StableId id, NamespaceScope scope): bool`**
- Check if type has been renamed

**`HasFinalMemberName(StableId id, TypeScope scope): bool`**
- Check if member has been renamed

### Algorithm

**Step 1: Check if already reserved**
- If StableId already has decision in scope → throw (duplicate reservation)

**Step 2: Apply syntax transforms**
- Backtick (`` ` ``) → Underscore (`_`)
- Angle brackets (`<`, `>`) → Underscore
- Plus (`+`) for nested → Dollar (`$`)

**Step 3: Apply naming policy**
- Policy.TypeNameTransform: PascalCase, camelCase, etc.
- Policy.MemberNameTransform: Same options
- Policy.ExplicitMap: Manual overrides

**Step 4: Sanitize reserved words**
- Check if transformed name is TypeScript reserved word
- If yes: Append `_` suffix
- Examples: `class` → `class_`, `interface` → `interface_`

**Step 5: Resolve conflicts**
- Check if name already used in scope
- If conflict: Append numeric suffix (name2, name3, etc.)
- Find first available suffix via incremental search

**Step 6: Create and store RenameDecision**
- Record: StableId, Requested, Final, From (original CLR), Reason, DecisionSource, Strategy, ScopeKey, IsStatic
- Store in decision table (keyed by StableId + ScopeKey)

**Files:** `Renaming/SymbolRenamer.cs`

---

## File: StableId.cs

### Record Struct: TypeStableId

**Format:** `"{AssemblyName}:{ClrFullName}"`

**Properties:**
- `AssemblyName: string` - Assembly simple name
- `ClrFullName: string` - Full CLR type name with backtick arity

**Methods:**
- `ToString(): string` - Full identity string
- `static Parse(string): TypeStableId` - Parse from string

**Example:** `"System.Private.CoreLib:System.Collections.Generic.List\`1"`

### Record Struct: MemberStableId

**Format:** `"{AssemblyName}:{DeclaringClrFullName}::{MemberName}{CanonicalSignature}"`

**Properties:**
- `AssemblyName: string`
- `DeclaringClrFullName: string`
- `MemberName: string`
- `CanonicalSignature: string`
- `MetadataToken: int` (excluded from equality)

**Methods:**
- `ToString(): string` - Full identity string
- `static Parse(string): MemberStableId` - Parse from string

**Example:** `"System.Private.CoreLib:System.Decimal::ToString(IFormatProvider):String"`

**Key:** MetadataToken excluded from equality comparisons (semantic identity only).

**Files:** `Renaming/StableId.cs`

---

## File: RenameScope.cs

### Purpose
Defines scope types for name reservation. Each scope has independent naming.

### Abstract Record: RenameScope

Base class for all scopes.

**Subclasses:**
- **NamespaceScope** - Namespace-level scope
- **TypeScope** - Type-level scope (class surface)
- **ViewScope** - Interface view scope

### Record: NamespaceScope (inherits RenameScope)

**Format:** `"ns:{NamespaceName}:{Area}"`

**Properties:**
- `NamespaceName: string` - Namespace name
- `Area: NamespaceArea` - Internal or Facade

**Example:** `"ns:System.Collections.Generic:internal"`

**ToString:** Returns formatted scope string

### Record: TypeScope (inherits RenameScope)

**Format:** `"type:{TypeClrFullName}#{StaticOrInstance}"`

**Properties:**
- `TypeClrFullName: string` - Type full CLR name
- `IsStatic: bool` - Static vs instance scope

**Example:** `"type:System.Decimal#instance"` or `"type:System.Decimal#static"`

**ToString:** Returns formatted scope string with #instance or #static

### Record: ViewScope (inherits RenameScope)

**Format:** `"view:{OwningAssembly}:{OwningType}:{InterfaceAssembly}:{InterfaceName}#{StaticOrInstance}"`

**Properties:**
- `OwningTypeStableId: TypeStableId` - Type that implements interface
- `InterfaceStableId: TypeStableId` - Interface for this view
- `IsStatic: bool` - Static vs instance scope

**Example:** `"view:CoreLib:System.Decimal:CoreLib:System.IConvertible#instance"`

**ToString:** Returns formatted scope string with full interface qualification

**Why complex:** View scopes must be unique per (type, interface) pair. Same interface implemented by different types = different scopes.

**Files:** `Renaming/RenameScope.cs`

---

## File: ScopeFactory.cs

### Purpose
Factory for creating scope instances with correct format.

### Key Methods

**`Namespace(string namespaceName, NamespaceArea area): NamespaceScope`**
- Create namespace scope
- Example: `ScopeFactory.Namespace("System.Collections.Generic", NamespaceArea.Internal)`

**`ClassSurface(TypeSymbol type, bool isStatic): TypeScope`**
- Create class surface scope
- Example: `ScopeFactory.ClassSurface(decimalType, false)` → `"type:System.Decimal#instance"`

**`View(TypeSymbol owningType, TypeStableId interfaceStableId, bool isStatic): ViewScope`**
- Create view scope for interface
- Example: `ScopeFactory.View(decimalType, iconvertibleId, false)`

**`GetInterfaceStableId(TypeReference iface): TypeStableId`**
- Extract interface StableId from TypeReference
- Handles NamedTypeReference with InterfaceStableId stamped

**Files:** `Renaming/ScopeFactory.cs`

---

## File: RenameDecision.cs

### Record: RenameDecision

**Purpose:** Records the rename decision with full provenance for diagnostics and bindings.

**Properties:**
- **`Id: StableId`** - What was renamed (TypeStableId or MemberStableId)
- **`Requested: string`** - What was requested (after transforms, before conflict resolution)
- **`Final: string`** - What was decided (after conflict resolution)
- **`From: string`** - Original CLR name (before any transforms)
- **`Reason: string`** - Why this rename happened (e.g., "Reserved in namespace", "Conflict with ToString")
- **`DecisionSource: string`** - Which component made the decision (e.g., "NameReservation", "HiddenMemberPlanner")
- **`Strategy: string`** - Strategy used: "None", "NumericSuffix", "Sanitize"
- **`ScopeKey: string`** - Which scope (for debugging)
- **`IsStatic: bool?`** - Static vs instance (null for types)

**Example:**
```csharp
new RenameDecision
{
    Id = TypeStableId.Parse("System.Private.CoreLib:System.Array"),
    Requested = "Array_",
    Final = "Array_",
    From = "Array",
    Reason = "Prevent shadowing built-in Array<T>",
    DecisionSource = "NameReservation",
    Strategy = "ManualOverride",
    ScopeKey = "ns:System:internal",
    IsStatic = null
}
```

**Usage:**
- Emitted to `bindings.json` for runtime binding
- Used for diagnostic output
- Enables debugging of rename conflicts

**Files:** `Renaming/RenameDecision.cs`

---

## File: NameReservationTable.cs

### Purpose
Per-scope name tracking. Prevents collisions within each scope.

**Data Structure:**
```csharp
Dictionary<string, HashSet<string>>  // ScopeKey → Set of used names
```

**Methods:**
- `Reserve(scope, name): bool` - Reserve name in scope, returns false if conflict
- `IsReserved(scope, name): bool` - Check if name already used
- `GetUsedNames(scope): HashSet<string>` - Get all names in scope

**Files:** `Renaming/NameReservationTable.cs`

---

## File: TypeScriptReservedWords.cs

### Purpose
List of TypeScript reserved words and sanitization logic.

**Reserved Words:**
- Keywords: `break, case, catch, class, const, continue, debugger, default, delete, do, else, enum, export, extends, false, finally, for, function, if, import, in, instanceof, let, new, null, return, super, switch, this, throw, true, try, typeof, var, void, while, with, yield`
- Future reserved: `implements, interface, package, private, protected, public, static, await, async`
- Strict mode: `arguments, eval`

**Methods:**
- `IsReserved(name): bool` - Check if name is reserved word
- `SanitizeName(name): string` - Append `_` if reserved
- `SanitizeParameterName(name): string` - Sanitize parameter names specifically

**Files:** `Renaming/TypeScriptReservedWords.cs`

---

## Scope-Based Naming Examples

### Example 1: Class Surface vs View

```csharp
class Decimal : IConvertible, IFormattable
{
    string ToString => "1.0";                                    // ClassSurface
    string IConvertible.ToString(IFormatProvider p) => "1.0";    // ViewOnly (IConvertible)
    string IFormattable.ToString(string fmt, IFormatProvider p) => "1.0";  // ViewOnly (IFormattable)
}
```

**Scopes:**
- `ToString` (ClassSurface): Scope = `"type:System.Decimal#instance"`
- `ToString` (IConvertible): Scope = `"view:CoreLib:System.Decimal:CoreLib:IConvertible#instance"`
- `ToString` (IFormattable): Scope = `"view:CoreLib:System.Decimal:CoreLib:IFormattable#instance"`

**Result:** All three `ToString` methods coexist with same name (different scopes).

### Example 2: Static vs Instance

```csharp
class Array
{
    int Length { get; }           // Instance property
    static int Length(Array a);   // Static method
}
```

**Scopes:**
- `Length` (instance): Scope = `"type:System.Array#instance"`
- `Length` (static): Scope = `"type:System.Array#static"`

**Result:** Both `Length` members coexist with same name (different scopes).

### Example 3: Conflict Resolution

```csharp
class Example
{
    void ToString() {}        // Original
    void ToString(int x) {}   // Conflict → ToString2
    void ToString(string x) {} // Conflict → ToString3
}
```

**Scopes:** All in `"type:Example#instance"`

**Decisions:**
1. `ToString()`: Final = "ToString" (first)
2. `ToString(int)`: Final = "ToString2" (numeric suffix)
3. `ToString(string)`: Final = "ToString3" (numeric suffix)

---

## Summary

The Renaming system provides:
1. **Central naming authority** (SymbolRenamer) for all TypeScript identifiers
2. **Stable identities** (TypeStableId, MemberStableId) for permanent tracking
3. **Scope-based naming** (namespace, class surface, view) for independent naming contexts
4. **Conflict resolution** via deterministic numeric suffixes
5. **Reserved word sanitization** for TypeScript compatibility
6. **Decision tracking** with full provenance for diagnostics and bindings
7. **Special cases** (System.Array → Array_) to prevent shadowing

**Key Benefits:**
- **Independent scopes:** Class, view, static, instance all separate
- **Deterministic:** Same input always produces same output
- **Traceable:** Full provenance for every rename decision
- **TypeScript-safe:** All reserved words handled
- **Conflict-free:** Numeric suffixes prevent all collisions
