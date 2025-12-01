# Phase 5: Normalize (Name Reservation)

## Overview

The **Normalize phase** reserves all TypeScript names via central SymbolRenamer and applies them to the SymbolGraph. Handles naming conflicts, reserved words, and scope-based naming.

**Input:** SymbolGraph (from Shape, TypeScript-ready but unnamed)
**Output:** SymbolGraph (with `TsEmitName` assigned to all symbols)
**Mutability:** Side effect (populates Renamer) + pure (returns new SymbolGraph)

---

## File: NameReservation.cs

### Method: ReserveAllNames

**Purpose:** Main entry point. Reserves all type and member names through SymbolRenamer.

**Algorithm:**

**Step 1: Reserve type names**
- For each namespace in graph
- For each type in namespace (including nested)
- Apply syntax transforms (backtick → underscore, etc.)
- Call `Renamer.ReserveTypeName(stableId, requested, scope, reason)`
- Scope: `ScopeFactory.Namespace(namespaceName, NamespaceArea.Internal)`

**Step 2: Reserve member names**
- For each type
- For each member (methods, properties, fields, events, constructors):
  - Skip if already renamed by earlier passes (HiddenMemberPlanner, IndexerPlanner)
  - Apply syntax transforms (`` ` `` → `_`, `+` → `_`, etc.)
  - Apply reserved word sanitization (add `_` suffix if needed)
  - Determine scope:
    - ClassSurface members: `ScopeFactory.ClassSurface(type, isStatic)`
    - ViewOnly members: `ScopeFactory.View(type, interfaceStableId)`
  - Call `Renamer.ReserveMemberName(stableId, requested, scope, reason, isStatic)`

**Step 3: Audit completeness**
- For each member with `EmitScope != Omitted`
- Check has rename decision
- If missing: Fail fast with ERROR

**Step 4: Apply names to graph**
- Call `Application.ApplyNamesToGraph(graph, renamer)`
- Returns new SymbolGraph with `TsEmitName` set for all symbols

**Files:** `Normalize/NameReservation.cs`

---

## File: Naming/Reservation.cs

### Purpose
Core reservation logic. Applies transforms, handles conflicts, creates rename decisions.

**Key Functions:**
- **Apply syntax transforms:** Backtick → underscore, angle brackets → underscore, etc.
- **Sanitize reserved words:** TypeScript keywords get `_` suffix (class → class_, interface → interface_)
- **Handle conflicts:** Numeric suffixes (ToString → ToString2, ToString3)

---

## File: Naming/Application.cs

### Method: ApplyNamesToGraph

**Purpose:** Applies reserved names from Renamer to SymbolGraph.

**Algorithm:**
1. For each namespace
2. For each type:
   - Get final name from Renamer
   - Update `TsEmitName` via `type.WithTsEmitName(finalName)`
3. For each member in type:
   - Get final name from Renamer (with correct scope)
   - Update `TsEmitName`
4. Return new SymbolGraph with all names set

---

## File: Naming/Shared.cs

### Constants and Utilities

**Syntax transforms:**
- Backtick (`` ` ``) → Underscore (`_`)
- Angle brackets (`<`, `>`) → Underscore
- Plus (`+`) for nested types → Dollar (`$`)
- Ampersand (`&`) → Underscore

**Reserved word handling:**
- TypeScript keywords: `class, interface, function, var, let, const, ...`
- Append `_` suffix: `class` → `class_`

---

## Special Cases

### Array Renaming (Array_ Special Case)

**Problem:** System.Array shadows built-in TypeScript `Array<T>`

**Solution:**
- Rename `System.Array` → `Array_`
- Prevents TS2315 errors (Type 'Array' is not generic)
- Documented in commit c1be1c0 (C.5.1 fix)

**Implementation:**
- In NameReservation: Check if type is `System.Array`
- Request name `"Array_"` instead of `"Array"`
- Prevents shadowing of built-in Array

---

## Scopes Used

### Type Names
- **Scope:** `ScopeFactory.Namespace(namespaceName, NamespaceArea.Internal)`
- **Key format:** `"ns:System.Collections.Generic:internal"`
- **Ensures:** Unique type names within namespace

### Class Surface Members
- **Scope:** `ScopeFactory.ClassSurface(type, isStatic)`
- **Key format:** `"type:System.Decimal#instance"` or `"type:System.Decimal#static"`
- **Ensures:** Unique member names on class (separate static/instance scopes)

### View Members
- **Scope:** `ScopeFactory.View(type, interfaceStableId)`
- **Key format:** `"view:CoreLib:System.Decimal:CoreLib:IConvertible#instance"`
- **Ensures:** Unique member names per interface view (each view has separate scope)

---

## State Transformation

**Before Normalize:**
- All symbols have `TsEmitName = ""`
- Renamer has no decisions

**After Normalize:**
- All emitted symbols have `TsEmitName` assigned
- Renamer contains complete decision table
- Omitted members have no rename decisions (intentional)

---

## Summary

The Normalize phase ensures all symbols have valid TypeScript names:
1. Reserves all type names in namespace scopes
2. Reserves all member names in class/view scopes
3. Applies syntax transforms for TypeScript compatibility
4. Sanitizes reserved words
5. Resolves naming conflicts via numeric suffixes
6. Applies reserved names to SymbolGraph
7. Validates completeness (all emitted symbols have names)

**Key features:**
- **Scope-based naming:** Separate scopes for class vs view, static vs instance
- **Conflict resolution:** Deterministic numeric suffixes
- **Reserved word safety:** All TypeScript keywords handled
- **Special cases:** System.Array → Array_ to prevent shadowing
- **Completeness validation:** Fail fast if any emitted member lacks name
