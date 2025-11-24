# Phase 8: Emit (File Generation)

## Overview

The **Emit phase** generates all output files from validated EmissionPlan. Uses Shape plans for plan-based emission. Only executes if PhaseGate validation passes.

**Input:** EmissionPlan (from Plan, validated)
**Output:** File I/O (*.d.ts, *.json, *.js)
**Mutability:** Side effects (file system writes)

**Critical Rule:** Only executes if `ctx.Diagnostics.HasErrors == false`

---

## Support Types from @tsonic/types

### Purpose
Support types are imported from the `@tsonic/types` npm package.

**Package:** `@tsonic/types`

**Contents:**
- Branded primitive types (int, uint, byte, decimal, etc.)
- Unsafe marker types (ptr<T>, ref<T>, out<T>, In<T>)
- CLROf<T> utility type for lifting primitives to generic positions

**Example:**
```typescript
// Branded primitives
export type int = number & { __brand: "int" };
export type uint = number & { __brand: "uint" };
export type byte = number & { __brand: "byte" };
export type decimal = number & { __brand: "decimal" };

// CLROf utility
export type CLROf<T> =
    T extends number ? int :
    T extends string ? string :
    T;
```

**Files:** `Emit/SupportTypesEmitter.cs`

---

## File: InternalIndexEmitter.cs

### Purpose
Emits `<namespace>/internal/index.d.ts` with all type declarations (internal surface).

**Output:** `<namespace>/internal/index.d.ts` for each namespace

**Contents:**
- All type declarations (classes, interfaces, enums, delegates)
- All members (methods, properties, fields, events, constructors)
- Imports from other namespaces (with aliases if needed)
- Import from @tsonic/types package (if needed)

**Plan-Based Emission:**
Uses EmissionPlan.StaticFlatteningPlan, StaticConflictPlan, OverrideConflictPlan, PropertyOverridePlan

**Example:**
```typescript
import type { ptr, ref } from "@tsonic/types";
import { Stream } from "../System.IO/internal/index.js";
import { IDisposable } from "../System/internal/index.js";

export class FileStream extends Stream implements IDisposable {
    // ... members
}
```

**Files:** `Emit/InternalIndexEmitter.cs`

---

## File: ExtensionsEmitter.cs

### Purpose
Emits `internal/extensions/index.d.ts` with extension method bucket interfaces (once per build, not per namespace).

**Output:** `internal/extensions/index.d.ts`

**Input:** `ExtensionMethodsPlan` (Pass 4.11)

**Algorithm:**
1. For each bucket in plan:
   - Emit bucket interface: `export interface __Ext_TargetType<T> { ... }`
   - Collapse method generics matching target generics
   - Detect IEquatable constraints → add to infer clause
2. Emit helper type: `export type ExtensionMethods<TShape> = ...`
   - Map shapes to buckets with constrained infer clauses
   - Example: `TShape extends IEnumerable_1<infer T> ? __Ext_IEnumerable_1<T> : ...`

**Key Features:**
- Generic parameter collapsing (method `T` matching bucket `T`)
- IEquatable constraint propagation to infer clauses
- TypeRefPrinter for all type emission (NO 'any' types)
- Single source of truth (FacadeEmitter re-exports)

**Statistics:** 122 bucket interfaces, 1,759 extension methods (BCL .NET 9)

**Files:** `Emit/ExtensionsEmitter.cs`

---

## File: FacadeEmitter.cs

### Purpose
Emits `<namespace>/index.d.ts` with public facade (re-exports from internal).

**Output:** `<namespace>/index.d.ts` for each namespace

**Contents:**
- Re-exports from internal surface: `export * from "./internal";`
- **Delegate aliases** (System namespace only): Lambda-compatible `Action<...>` and `Func<...>` convenience types
- Keeps public API clean and simple

**Delegate Aliases (AppendDelegateAliases):**

For System namespace only, generates lambda-compatible convenience aliases:

```typescript
// Unspecified marker for conditional type routing
declare const __unspecified: unique symbol;
export type __ = typeof __unspecified;

// Action<T1, T2, ...> with default parameters
export type Action<T1 = __, T2 = __, ...> =
  [T1] extends [__] ? ((() => void) | Internal.Action) :
  [T2] extends [__] ? (((arg1: T1) => void) | Internal.Action_1<T1>) :
  // ... routes to correct arity (0-16 params)

// Func<T1, T2, ..., TResult> with default parameters
export type Func<T1 = __, T2 = __, ...> =
  [T2] extends [__] ? ((() => T1) | Internal.Func_1<T1>) :
  [T3] extends [__] ? (((arg1: T1) => T2) | Internal.Func_2<T1, T2>) :
  // ... routes to correct arity (0-16 input params + 1 result)
```

**Why:** Internal delegates are classes (preserve CLR members), can't accept TS lambdas. Facade unions call signatures with delegate classes, enabling:

```typescript
✅ const f: Func<int, int> = x => x + 1;  // Works via facade
❌ const f: Func_2<int, int> = x => x + 1;  // Fails (internal class)
```

**Files:** `Emit/FacadeEmitter.cs:188-293`

---

## File: MetadataEmitter.cs

### Purpose
Emits `<namespace>/metadata.json` with CLR-specific metadata.

**Output:** `<namespace>/metadata.json` for each namespace

**Contents:**
- Virtual/override status for methods/properties
- Static vs instance distinction
- Ref/out/params parameter modifiers
- Struct vs class distinction
- Intentional omissions (indexers, generic statics)

**Example:**
```json
{
  "types": {
    "FileStream": {
      "isValueType": false,
      "members": {
        "Read": {
          "isVirtual": true,
          "isOverride": true,
          "parameters": [
            { "name": "buffer", "isRef": false, "isOut": false }
          ]
        }
      }
    }
  }
}
```

**Files:** `Emit/MetadataEmitter.cs`

---

## File: BindingEmitter.cs

### Purpose
Emits `<namespace>/bindings.json` with CLR → TS name mappings.

**Output:** `<namespace>/bindings.json` for each namespace

**Contents:**
- Type name bindings (StableId, CLR name → TS emit name)
- Member name bindings (StableId, CLR signature → TS emit name)
- Rename decisions (requested → final, strategy)
- **StableId field**: Immutable assembly-qualified identifier for types/members (used by library mode)

**Example:**
```json
{
  "types": {
    "List`1": {
      "stableId": "System.Private.CoreLib:System.Collections.Generic.List`1",
      "clrName": "List`1",
      "tsEmitName": "List_1",
      "members": {
        "Add": {
          "stableId": "System.Private.CoreLib:System.Collections.Generic.List`1::Add(T):Void",
          "clrSignature": "Add(T):Void",
          "tsEmitName": "Add"
        }
      }
    }
  }
}
```

**Files:** `Emit/BindingEmitter.cs`

---

## File: ModuleStubEmitter.cs

### Purpose
Emits `<namespace>/index.js` with ES module stubs.

**Output:** `<namespace>/index.js` for each namespace

**Contents:**
- Empty ES module stub for runtime (no actual implementation)
- Enables module resolution in TypeScript

**Example:**
```javascript
// ES module stub for System.Collections.Generic
export {};
```

**Files:** `Emit/ModuleStubEmitter.cs`

---

## File: Printers/ClassPrinter.cs

### Purpose
Prints class/interface/struct declarations with plan-based emission.

**Plan-Based Emission:**

**1. StaticFlatteningPlan (Pass 4.7):**
- Check if type in StaticFlatteningPlan
- If yes: Emit inherited static members from plan
- For each inherited member:
  - Emit with source comment: `// Inherited from BaseClass`
  - Use declaring type from plan

**2. StaticConflictPlan (Pass 4.8):**
- Check if member name in StaticConflictPlan for type
- If yes: Suppress emission (skip member)
- Prevents TS2417 errors from static shadowing

**3. OverrideConflictPlan (Pass 4.9):**
- Check if member name in OverrideConflictPlan for type
- If yes: Suppress emission (skip member)
- Prevents TS2416 errors from incompatible overrides

**4. PropertyOverridePlan (Pass 4.10):**
- Check if property in PropertyOverridePlan
- If yes: Emit unified union type instead of original type
- Example: `level: HttpRequestCacheLevel | HttpCacheAgeControl`
- Eliminates TS2416 errors from property type variance

**Algorithm:**
1. **Emit type header:**
   - `export class ClassName<T>`
   - `extends BaseClass`
   - `implements Interface1, Interface2`

2. **Emit instance members:**
   - For each property:
     - **Check PropertyOverridePlan:** Use union type if in plan
     - Else: Use original property type
   - For each method:
     - **Check OverrideConflictPlan:** Skip if in conflict set
     - Else: Emit method signature
   - For each field:
     - Emit field declaration

3. **Emit static members:**
   - **Check StaticFlatteningPlan:** Emit inherited statics if type in plan
   - For each static property/method/field:
     - **Check StaticConflictPlan:** Skip if in conflict set
     - Else: Emit static member

4. **Emit explicit views:**
   - For each ExplicitView:
     - Emit view property: `As_IInterface: { ... }`
     - Emit all ViewOnly members for that interface

**Example with plans:**
```typescript
export class HttpRequestCachePolicy {
    // PropertyOverridePlan: Unified union type
    level: HttpRequestCacheLevel | HttpCacheAgeControl;

    // OverrideConflictPlan: Suppressed (conflict with base)
    // ToString(): string;  // SUPPRESSED

    // Explicit view
    As_ICacheable: {
        GetCacheKey(): string;
    };
}

export class Task_1<T> {
    // StaticConflictPlan: Suppressed (shadows Task.Factory)
    // static Factory: TaskFactory_1<T>;  // SUPPRESSED
}

export class Vector128_1<T> {
    // StaticFlatteningPlan: Inherited from base Vector128
    // Inherited from Vector128
    static Zero: Vector128_1<T>;
    // Inherited from Vector128
    static AllBitsSet: Vector128_1<T>;
}
```

**Files:** `Emit/Printers/ClassPrinter.cs`

---

## File: Printers/EnumPrinter.cs

### Purpose
Prints enum declarations.

**Algorithm:**
1. Emit enum header: `export enum EnumName`
2. For each enum value:
   - Emit: `ValueName = NumericValue,`

**Example:**
```typescript
export enum FileMode {
    CreateNew = 1,
    Create = 2,
    Open = 3,
    OpenOrCreate = 4,
}
```

**Files:** `Emit/Printers/EnumPrinter.cs`

---

## File: Printers/DelegatePrinter.cs

### Purpose
Prints delegate declarations as TypeScript type aliases.

**Algorithm:**
1. Emit type alias: `export type DelegateName = (params) => returnType;`

**Example:**
```typescript
export type Action_1<T> = (obj: T) => void;
export type Func_2<T, TResult> = (arg: T) => TResult;
```

**Files:** `Emit/Printers/DelegatePrinter.cs`

---

## File: TypeMap.cs

### Purpose
Maps TypeReference → TypeScript type string.

**Mappings:**
- `System.Boolean` → `boolean`
- `System.String` → `string`
- `System.Int32` → `int` (branded)
- `System.Object` → `object`
- `System.Void` → `void`
- `T[]` → `ReadonlyArray<T>`
- `T*` → `never` (unsupported)
- `ref T` → `T` (lose ref semantics)

**Files:** `Emit/TypeMap.cs`

---

## File: TypeNameResolver.cs

### Purpose
Resolves type names with imports/aliases.

**Algorithm:**
1. Check if type is in current namespace → use simple name
2. Check if type imported with alias → use alias
3. Check if type imported without alias → use simple name
4. Else → qualify with namespace (shouldn't happen if ImportPlan correct)

**Files:** `Emit/TypeNameResolver.cs`

---

## Output File Structure

```
output/
├── System/
│   ├── internal/
│   │   └── index.d.ts                # Internal declarations
│   ├── index.d.ts                    # Public facade
│   ├── metadata.json                 # CLR metadata
│   ├── bindings.json                 # Name bindings
│   └── index.js                      # ES module stub
├── System.Collections.Generic/
│   ├── internal/
│   │   └── index.d.ts
│   ├── index.d.ts
│   ├── metadata.json
│   ├── bindings.json
│   └── index.js
└── ... (130 namespaces total)
```

---

## Plan-Based Emission Summary

The Emit phase uses 4 Shape plans for error-free emission:

**1. StaticFlatteningPlan (Pass 4.7):**
- **When:** Type is static-only (no instance members)
- **Action:** Emit inherited static members from base classes
- **Benefit:** Eliminates TS2417 errors for static-only hierarchies
- **Example:** SIMD intrinsics (Vector128<T>)

**2. StaticConflictPlan (Pass 4.8):**
- **When:** Member name in static conflict set for type
- **Action:** Suppress emission (skip member)
- **Benefit:** Eliminates TS2417 errors for hybrid types (static + instance)
- **Example:** Task<T>.Factory shadowing Task.Factory

**3. OverrideConflictPlan (Pass 4.9):**
- **When:** Member name in override conflict set for type
- **Action:** Suppress emission (skip member)
- **Benefit:** Reduces TS2416 errors for incompatible instance overrides
- **Example:** Properties with incompatible signatures vs base

**4. PropertyOverridePlan (Pass 4.10):**
- **When:** Property in property override plan
- **Action:** Emit unified union type instead of original type
- **Benefit:** Eliminates final TS2416 errors → zero TypeScript errors
- **Example:** `level: HttpRequestCacheLevel | HttpCacheAgeControl`

**Result:** Plan-based emission achieves **zero TypeScript errors** for full BCL (4,295 types, 130 namespaces).

---

## Summary

The Emit phase generates all output files with plan-based emission:
1. **Support types:** From @tsonic/types package
2. **InternalIndexEmitter:** Full declarations (internal/index.d.ts) with plan-based emission
3. **ExtensionsEmitter:** Extension method buckets (internal/extensions/index.d.ts)
4. **FacadeEmitter:** Public facade (index.d.ts) with extension method re-export
5. **MetadataEmitter:** CLR metadata (metadata.json)
6. **BindingEmitter:** Name bindings (bindings.json)
7. **ModuleStubEmitter:** ES module stubs (index.js)

**Key Features:**
- **Plan-based emission:** Uses 5 Shape plans (including extension methods) for error elimination
- **Zero TypeScript errors:** Achieved via plan-based emission
- **Extension methods:** 122 bucket interfaces, 1,759 methods (BCL .NET 9)
- **Complete metadata:** All CLR semantics preserved
- **Deterministic output:** Stable emission order
- **Type safety:** Branded primitives, readonly arrays
- **Cross-namespace imports:** With auto-aliasing for collisions
