# nodejs TypeScript Errors Analysis Report

> **RESOLVED**: This document is historical. All errors described here have been fixed in v0.7.4.
> The nodejs package now compiles with **zero TypeScript errors**.

**Date:** 2025-12-06
**Author:** Claude (with Jeswin)
**Package:** @tsonic/nodejs
**Total Errors:** ~~13 nodejs-specific + 18 BCL errors~~ → **0 errors (v0.7.4+)**

---

## Executive Summary

After implementing library mode in tsbindgen, the nodejs package correctly imports BCL types from `@tsonic/dotnet` using facade names. However, 13 TypeScript errors remain in the nodejs package itself. These fall into 3 distinct categories:

| Error | Count | Severity | Fix Location |
|-------|-------|----------|--------------|
| TS2300: Duplicate IEnumerable | 4 | High | ImportPlanner.cs or FacadeEmitter.cs |
| TS1016: Required param after optional | 8 | Medium | MethodPrinter.cs |
| TS2314: ValueTuple requires 1 arg | 1 | Medium | FacadeEmitter.cs (AliasEmit.cs) |

---

## Error 1: TS2300 - Duplicate Identifier 'IEnumerable'

### Error Messages

```
nodejs.d.ts(9,71): error TS2300: Duplicate identifier 'IEnumerable'.
nodejs.d.ts(10,15): error TS2300: Duplicate identifier 'IEnumerable'.
nodejs/internal/index.d.ts(13,71): error TS2300: Duplicate identifier 'IEnumerable'.
nodejs/internal/index.d.ts(15,15): error TS2300: Duplicate identifier 'IEnumerable'.
```

### Context

In .NET, there are TWO types named `IEnumerable`:

1. **`System.Collections.IEnumerable`** - Non-generic interface (legacy)
2. **`System.Collections.Generic.IEnumerable<T>`** - Generic interface (modern)

These are in DIFFERENT namespaces but have the same simple name.

### What Happened

When tsbindgen's facade system transforms type names:
- `System.Collections.Generic.IEnumerable`1` → exports as `IEnumerable` (arity stripped)
- `System.Collections.IEnumerable` → exports as `IEnumerable` (no arity to strip)

Both become `IEnumerable` in their respective facade files:

```typescript
// @tsonic/dotnet/System.Collections.Generic.d.ts
export type IEnumerable<T> = Internal.IEnumerable_1<T>;

// @tsonic/dotnet/System.Collections.d.ts
export type IEnumerable = Internal.IEnumerable;
```

When nodejs imports from BOTH namespaces, TypeScript sees a duplicate:

```typescript
// nodejs/internal/index.d.ts (GENERATED - PROBLEMATIC)
import type { ..., IEnumerable, ... } from "@tsonic/dotnet/System.Collections.Generic.js";
import type { IEnumerable } from "@tsonic/dotnet/System.Collections.js";  // COLLISION!
```

### Root Cause

The `ImportPlanner.cs` deduplication logic only prevents duplicate imports within a SINGLE import statement. It doesn't handle same-name imports from DIFFERENT namespaces.

```csharp
// Current code (ImportPlanner.cs:156-162)
// LIBRARY FACADE FIX: Skip if this facade name was already imported
// Multiple arity types (Action_1, Action_2, Action_3) all map to same facade name (Action)
if (typeImports.Any(ti => ti.TypeName == tsName))
{
    ctx.Log("ImportPlanner", $"Skipping duplicate facade import: {tsName}");
    continue;
}
```

This deduplication is scoped to a single `foreach` iteration (one target namespace), not across all namespaces.

### Test Case

```typescript
// test-duplicate-import.ts
import type { IEnumerable } from "@tsonic/dotnet/System.Collections.Generic.js";
import type { IEnumerable } from "@tsonic/dotnet/System.Collections.js";
// Error: Duplicate identifier 'IEnumerable'

// What TypeScript expects:
import type { IEnumerable } from "@tsonic/dotnet/System.Collections.Generic.js";
import type { IEnumerable as IEnumerable_NonGeneric } from "@tsonic/dotnet/System.Collections.js";
```

### Potential Fixes

**Option A: Cross-namespace deduplication with aliasing (ImportPlanner.cs)**
```csharp
// Track all imported type names across ALL namespaces
var allImportedTypeNames = new HashSet<string>();

foreach (var targetNamespace in dependencies.OrderBy(d => d))
{
    // ... existing code ...

    foreach (var clrName in referencedTypeClrNames)
    {
        // ... compute tsName ...

        // NEW: Check if this name was already imported from ANY namespace
        if (allImportedTypeNames.Contains(tsName))
        {
            // Need alias - append namespace suffix
            var alias = $"{tsName}_{GetNamespaceShortName(targetNamespace)}";
            typeImports.Add(new TypeImport(TypeName: tsName, Alias: alias, ...));
        }
        else
        {
            allImportedTypeNames.Add(tsName);
            typeImports.Add(new TypeImport(TypeName: tsName, Alias: null, ...));
        }
    }
}
```

**Option B: Facade naming differentiation (FacadeEmitter.cs)**

Since `IEnumerable` (non-generic) is rarely used directly in modern code, we could export it with a suffix:
```typescript
// System.Collections.d.ts
export type IEnumerable_0 = Internal.IEnumerable;  // Non-generic gets _0
```

This would match how we handle `Task` vs `Task<T>` → `Task_0` vs `Task`.

**Recommendation:** Option A is more robust. It handles ANY cross-namespace name collision, not just IEnumerable.

---

## Error 2: TS1016 - Required Parameter After Optional

### Error Messages

```
nodejs/internal/index.d.ts(2094,53): error TS1016: A required parameter cannot follow an optional parameter.
nodejs/internal/index.d.ts(2098,37): error TS1016: A required parameter cannot follow an optional parameter.
nodejs/internal/index.d.ts(2101,37): error TS1016: A required parameter cannot follow an optional parameter.
nodejs/internal/index.d.ts(2105,36): error TS1016: A required parameter cannot follow an optional parameter.
nodejs/internal/index.d.ts(2106,35): error TS1016: A required parameter cannot follow an optional parameter.
nodejs/internal/index.d.ts(2112,36): error TS1016: A required parameter cannot follow an optional parameter.
nodejs/internal/index.d.ts(2114,37): error TS1016: A required parameter cannot follow an optional parameter.
nodejs/internal/index.d.ts(2115,36): error TS1016: A required parameter cannot follow an optional parameter.
```

### Context

In C#, the `params` keyword allows variadic arguments:

```csharp
// nodejs-clr/src/nodejs/console/console.cs (line 24)
public static void assert(bool value, string? message = null, params object[] optionalParams)
{
    // Can be called as:
    // assert(true)
    // assert(true, "message")
    // assert(true, "message", arg1, arg2, arg3)
}
```

Key behaviors:
- `message` is optional (`= null`)
- `params object[] optionalParams` is IMPLICITLY optional in C# - you can omit it entirely

### What Happened

tsbindgen emits `params` arrays as regular (required) arrays:

```typescript
// Generated TypeScript (WRONG)
static assert(value: boolean, message?: string, optionalParams: unknown[]): void;
//                            ^^^^^^^^ optional    ^^^^^^^^^^^^^^^^^^ required!

// TypeScript rule: Required params cannot follow optional params
```

### Root Cause

The `MethodPrinter.cs` (or wherever parameters are emitted) doesn't mark `params` arrays as optional:

```csharp
// Current emission (simplified)
foreach (var param in method.Parameters)
{
    var isOptional = param.HasDefaultValue;  // Only checks HasDefaultValue
    // params arrays don't have HasDefaultValue, so they're treated as required
}
```

### Test Case

```csharp
// C# source
public static void log(object? message = null, params object[] optionalParams) { }

// Current TypeScript emission (WRONG)
static log(message?: unknown, optionalParams: unknown[]): void;

// Correct TypeScript emission
static log(message?: unknown, optionalParams?: unknown[]): void;
//                                          ^ must be optional!

// Or with rest syntax (more idiomatic):
static log(message?: unknown, ...optionalParams: unknown[]): void;
```

### Potential Fixes

**Option A: Mark params arrays as optional**
```csharp
// In MethodPrinter.cs or ParameterEmitter.cs
var isOptional = param.HasDefaultValue || param.IsParams;

// Emit as: paramName?: unknown[]
```

**Option B: Use TypeScript rest syntax**
```csharp
// Detect params arrays and emit with rest syntax
if (param.IsParams)
{
    sb.Append($"...{paramName}: {elementType}[]");
}
else
{
    sb.Append($"{paramName}{optionalMarker}: {paramType}");
}

// Emit as: ...optionalParams: unknown[]
```

**Recommendation:** Option B is more idiomatic TypeScript. Rest parameters (`...args`) are the TypeScript equivalent of C# `params`.

### Affected Methods (8 total)

All in `console$instance`:
- `assert(value, message?, optionalParams)` - line 2094
- `debug(message?, optionalParams)` - line 2098
- `error(message?, optionalParams)` - line 2101
- `info(message?, optionalParams)` - line 2105
- `log(message?, optionalParams)` - line 2106
- `timeLog(label?, data)` - line 2112
- `trace(message?, optionalParams)` - line 2114
- `warn(message?, optionalParams)` - line 2115

---

## Error 3: TS2314 - Generic Type Requires Type Arguments

### Error Message

```
nodejs/internal/index.d.ts(2147,67): error TS2314: Generic type 'ValueTuple_1' requires 1 type argument(s).
```

### Context

In C#, `ValueTuple` comes in multiple arities:
- `ValueTuple` - 0 elements (empty tuple)
- `ValueTuple<T1>` - 1 element
- `ValueTuple<T1, T2>` - 2 elements
- ... up to `ValueTuple<T1, ..., T8>` - 8 elements

This is similar to `Action` and `Func` which also have multiple arities.

### What Happened

The nodejs code uses `ValueTuple<T1, T2>` (2-element tuple):

```csharp
// C# source
public static ValueTuple<KeyObject, KeyObject> generateKeyPairSync(string type_, object? options = null)
```

Which should emit as:
```typescript
static generateKeyPairSync(type_: string, options?: unknown): ValueTuple_2<KeyObject, KeyObject>;
```

But it emits as:
```typescript
// Generated (WRONG)
static generateKeyPairSync(type_: string, options?: unknown): ValueTuple<KeyObject, KeyObject>;
```

The problem: `ValueTuple` in the facade maps to `ValueTuple_1` (1-arg), not a conditional type that handles all arities:

```typescript
// Current facade exports (System.d.ts)
export { ValueTuple as ValueTuple_0 } from './System/internal/index.js';
export { ValueTuple_1 as ValueTuple } from './System/internal/index.js';
// Missing: ValueTuple_2 through ValueTuple_8!
```

When nodejs imports `ValueTuple` and uses it with 2 type arguments:
```typescript
import type { ValueTuple } from "@tsonic/dotnet/System.js";
// ValueTuple is actually ValueTuple_1 which requires exactly 1 type argument

ValueTuple<KeyObject, KeyObject>  // Error! ValueTuple_1 only takes 1 arg
```

### Root Cause

The facade emitter has special handling for `Action` and `Func` (conditional types), but NOT for `ValueTuple`:

```typescript
// System.d.ts - Action uses conditional type for all arities
export type Action<
  T1 = __,
  T2 = __,
  ...
  T16 = __,
> =
  [T1] extends [__] ? ((() => void) | Internal.Action) :
  [T2] extends [__] ? (((arg1: T1) => void) | Internal.Action_1<T1>) :
  [T3] extends [__] ? (((arg1: T1, arg2: T2) => void) | Internal.Action_2<T1, T2>) :
  // ... handles all arities with one export name

// ValueTuple SHOULD have similar treatment but doesn't:
export type ValueTuple<
  T1 = __,
  T2 = __,
  ...
  T8 = __,
> =
  [T1] extends [__] ? Internal.ValueTuple :
  [T2] extends [__] ? Internal.ValueTuple_1<T1> :
  [T3] extends [__] ? Internal.ValueTuple_2<T1, T2> :
  // ... etc
```

### Test Case

```typescript
// test-valuetuple.ts
import type { ValueTuple } from "@tsonic/dotnet/System.js";

// Current behavior (FAILS)
type KeyPair = ValueTuple<string, number>;  // Error: requires 1 type argument

// Expected behavior
type KeyPair = ValueTuple<string, number>;  // Should work, maps to ValueTuple_2
```

### Potential Fixes

**Option A: Add conditional type for ValueTuple (AliasEmit.cs)**

Add `ValueTuple` to the list of multi-arity types that get conditional type treatment:

```csharp
// In AliasEmit.cs or DelegateAliasEmit.cs
private static readonly HashSet<string> MultiArityTypes = new()
{
    "System.Action",
    "System.Func",
    "System.ValueTuple",  // ADD THIS
    "System.Tuple",       // Also has multiple arities
};
```

Then generate:
```typescript
export type ValueTuple<
  T1 = __,
  T2 = __,
  T3 = __,
  T4 = __,
  T5 = __,
  T6 = __,
  T7 = __,
  T8 = __,
> =
  [T1] extends [__] ? Internal.ValueTuple :
  [T2] extends [__] ? Internal.ValueTuple_1<T1> :
  [T3] extends [__] ? Internal.ValueTuple_2<T1, T2> :
  [T4] extends [__] ? Internal.ValueTuple_3<T1, T2, T3> :
  [T5] extends [__] ? Internal.ValueTuple_4<T1, T2, T3, T4> :
  [T6] extends [__] ? Internal.ValueTuple_5<T1, T2, T3, T4, T5> :
  [T7] extends [__] ? Internal.ValueTuple_6<T1, T2, T3, T4, T5, T6> :
  [T8] extends [__] ? Internal.ValueTuple_7<T1, T2, T3, T4, T5, T6, T7> :
  Internal.ValueTuple_8<T1, T2, T3, T4, T5, T6, T7, T8>;
```

**Option B: Export all arities individually**

Export each arity separately in the facade:
```typescript
export { ValueTuple as ValueTuple_0 } from './System/internal/index.js';
export { ValueTuple_1 } from './System/internal/index.js';
export { ValueTuple_2 } from './System/internal/index.js';
// ... etc
```

Then user code imports the specific arity:
```typescript
import type { ValueTuple_2 } from "@tsonic/dotnet/System.js";
type KeyPair = ValueTuple_2<string, number>;
```

**Recommendation:** Option A is preferred. It provides ergonomic usage (`ValueTuple<A, B>` instead of `ValueTuple_2<A, B>`) and matches how Action/Func work.

---

## Appendix: BCL Errors (Known Limitation)

The 18 TS2430 errors in `@tsonic/dotnet/System.Numerics` are a known .NET/TypeScript impedance mismatch:

```
error TS2430: Interface 'IBinaryFloatingPointIeee754_1$instance<TSelf>' incorrectly extends
interface 'IUtf8SpanFormattable$instance'.
  Types of property 'tryFormat' are incompatible.
```

This occurs because:
1. C# interfaces have covariant method overloading
2. TypeScript interfaces don't support method overloading with incompatible signatures

This is tracked as a known limitation and is NOT caused by the library mode changes.

---

## Summary of Recommended Actions

| Error | Fix | File | Effort |
|-------|-----|------|--------|
| TS2300 Duplicate IEnumerable | Cross-namespace deduplication with aliasing | ImportPlanner.cs | Medium |
| TS1016 Params not optional | Use rest syntax for params arrays | MethodPrinter.cs | Low |
| TS2314 ValueTuple arity | Add conditional type (like Action/Func) | AliasEmit.cs | Medium |

**Total Effort:** ~2-3 hours of focused work

---

## Files Referenced

- `tsbindgen/src/tsbindgen/Plan/ImportPlanner.cs` - Import deduplication logic
- `tsbindgen/src/tsbindgen/Emit/*.cs` - Type/method emission
- `nodejs/nodejs/internal/index.d.ts` - Generated output with errors
- `dotnet/System.d.ts` - Facade exports
- `nodejs-clr/src/nodejs/console/console.cs` - C# source with params
