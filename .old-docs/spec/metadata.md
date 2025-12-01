# Metadata JSON Schema

`<Namespace>/metadata.json` describes CLR-specific semantics that TypeScript cannot express. This file is consumed by the Tsonic compiler when generating C# code from TypeScript.

## Purpose

TypeScript declarations (`.d.ts`) capture **static types** but lose CLR **runtime semantics**:
- Virtual/override/abstract modifiers
- Accessibility (public/protected/internal/private)
- Static vs instance distinction
- Indexers (omitted from .d.ts due to overload conflicts)
- Generic static members (omitted from .d.ts due to TypeScript limitations)

Metadata sidecars preserve this information for the Tsonic→C# emitter.

## File Location

```
<output-dir>/
  System.Linq/
    internal/
      index.d.ts           # TypeScript declarations
    index.d.ts             # Facade
    metadata.json          # ← CLR semantics (this file)
```

## Root Schema

```json
{
  "namespace": "System.Linq",
  "sourceAssemblies": ["System.Linq", "System.Core"],
  "types": {
    "Enumerable": { /* TypeMetadata */ },
    "EnumerableQuery_1": { /* TypeMetadata */ }
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `namespace` | string | CLR namespace name |
| `sourceAssemblies` | string[] | Assemblies that contributed types to this namespace |
| `types` | Record<string, TypeMetadata> | Type metadata, keyed by TypeScript name |

## TypeMetadata

```json
{
  "clrName": "Enumerable",
  "tsEmitName": "Enumerable",
  "fullName": "System.Linq.Enumerable",
  "kind": "class",
  "accessibility": "public",
  "isAbstract": false,
  "isSealed": true,
  "isStatic": true,
  "isValueType": false,
  "baseType": null,
  "interfaces": [],
  "members": {
    "SelectMany": { /* MemberMetadata */ }
  },
  "intentionalOmissions": {
    "indexers": [],
    "genericStaticMembers": []
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `clrName` | string | Original CLR type name |
| `tsEmitName` | string | TypeScript identifier (may differ if naming transforms active) |
| `fullName` | string | Fully-qualified CLR name (e.g., `System.Linq.Enumerable`) |
| `kind` | string | `"class"`, `"struct"`, `"interface"`, `"enum"`, `"delegate"` |
| `accessibility` | string | `"public"`, `"internal"`, `"protected"`, `"private"` |
| `isAbstract` | boolean | Abstract type (cannot be instantiated) |
| `isSealed` | boolean | Sealed type (cannot be inherited) |
| `isStatic` | boolean | Static class (all members static) |
| `isValueType` | boolean | Value type (struct, enum, primitive) |
| `baseType` | string \| null | Base class full name, or `null` for interfaces/Object |
| `interfaces` | string[] | Implemented interface full names |
| `members` | Record<string, MemberMetadata> | Member metadata, keyed by signature |
| `intentionalOmissions` | IntentionalOmissions | Members intentionally not emitted to .d.ts |

**Notes**:
- `baseType` excludes `System.Object` and `System.ValueType` (always `null` for these)
- `members` keys are C#-style signatures (e.g., `"SelectMany(IEnumerable, Func)"`)
- Nested types use `$` in `tsEmitName` (e.g., `"List_1$Enumerator"`)

## MemberMetadata

### Constructor

```json
{
  "kind": "constructor",
  "accessibility": "public",
  "parameters": [
    {
      "name": "capacity",
      "type": "System.Int32",
      "isRef": false,
      "isOut": false,
      "isParams": false
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `kind` | `"constructor"` | Member kind |
| `accessibility` | string | `"public"`, `"protected"`, `"internal"`, `"private"` |
| `parameters` | ParameterMetadata[] | Constructor parameters |

### Method

```json
{
  "kind": "method",
  "clrName": "SelectMany",
  "tsEmitName": "selectMany",
  "accessibility": "public",
  "isStatic": true,
  "isVirtual": false,
  "isAbstract": false,
  "isOverride": false,
  "isSealed": false,
  "isExtensionMethod": true,
  "returnType": "System.Collections.Generic.IEnumerable`1",
  "genericParameters": ["TSource", "TResult"],
  "parameters": [
    {
      "name": "source",
      "type": "System.Collections.Generic.IEnumerable`1<TSource>",
      "isRef": false,
      "isOut": false,
      "isParams": false
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `kind` | `"method"` | Member kind |
| `clrName` | string | Original CLR method name |
| `tsEmitName` | string | TypeScript identifier (may differ if camelCase transform) |
| `accessibility` | string | `"public"`, `"protected"`, `"internal"`, `"private"` |
| `isStatic` | boolean | Static method |
| `isVirtual` | boolean | Virtual method (can be overridden) |
| `isAbstract` | boolean | Abstract method (must be overridden) |
| `isOverride` | boolean | Overrides base method |
| `isSealed` | boolean | Sealed override (cannot be further overridden) |
| `isExtensionMethod` | boolean | C# extension method (first param is `this`) |
| `returnType` | string | Return type full name (CLR, with backtick arity) |
| `genericParameters` | string[] | Method-level generic parameters |
| `parameters` | ParameterMetadata[] | Method parameters |

### Property

```json
{
  "kind": "property",
  "clrName": "Length",
  "tsEmitName": "length",
  "accessibility": "public",
  "isStatic": false,
  "isVirtual": false,
  "isAbstract": false,
  "isOverride": false,
  "type": "System.Int32",
  "canRead": true,
  "canWrite": false,
  "isIndexer": false
}
```

| Field | Type | Description |
|-------|------|-------------|
| `kind` | `"property"` | Member kind |
| `clrName` | string | Original CLR property name |
| `tsEmitName` | string | TypeScript identifier |
| `accessibility` | string | `"public"`, `"protected"`, `"internal"`, `"private"` |
| `isStatic` | boolean | Static property |
| `isVirtual` | boolean | Virtual property |
| `isAbstract` | boolean | Abstract property |
| `isOverride` | boolean | Overrides base property |
| `type` | string | Property type full name |
| `canRead` | boolean | Has getter |
| `canWrite` | boolean | Has setter |
| `isIndexer` | boolean | Is an indexer property |

**Note**: Indexer properties (`isIndexer: true`) are tracked in metadata but NOT emitted to .d.ts (TypeScript overload conflict).

### Field (Enums, Delegates)

```json
{
  "kind": "field",
  "clrName": "Monday",
  "tsEmitName": "monday",
  "accessibility": "public",
  "isStatic": true,
  "type": "System.DayOfWeek",
  "isConst": true,
  "value": 1
}
```

| Field | Type | Description |
|-------|------|-------------|
| `kind` | `"field"` | Member kind |
| `clrName` | string | Original CLR field name |
| `tsEmitName` | string | TypeScript identifier |
| `accessibility` | string | Always `"public"` for enum members |
| `isStatic` | boolean | Static field (always `true` for enum members) |
| `type` | string | Field type full name |
| `isConst` | boolean | Const field (enum members) |
| `value` | number \| string \| null | Constant value (for enums) |

## ParameterMetadata

```json
{
  "name": "source",
  "type": "System.Collections.Generic.IEnumerable`1<TSource>",
  "isRef": false,
  "isOut": false,
  "isParams": false,
  "defaultValue": null
}
```

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Parameter name |
| `type` | string | Parameter type (CLR full name, with generic args) |
| `isRef` | boolean | `ref` parameter |
| `isOut` | boolean | `out` parameter |
| `isParams` | boolean | `params` parameter (C# variable args) |
| `defaultValue` | any \| null | Default value (if optional parameter) |

## IntentionalOmissions

Tracks members that were intentionally NOT emitted to `.d.ts` due to known TypeScript limitations.

```json
{
  "indexers": [
    {
      "signature": "Item[int]",
      "reason": "TypeScript does not support indexer overloads with different parameter types"
    }
  ],
  "genericStaticMembers": [
    {
      "signature": "DefaultValue<T>",
      "reason": "TypeScript does not support generic static properties"
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `indexers` | OmissionEntry[] | Indexer properties omitted from .d.ts |
| `genericStaticMembers` | OmissionEntry[] | Generic static members omitted from .d.ts |

### OmissionEntry

```json
{
  "signature": "Item[string, int]",
  "reason": "TypeScript indexer overload conflict"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `signature` | string | C#-style signature of omitted member |
| `reason` | string | Human-readable explanation for omission |

## Complete Example

```json
{
  "namespace": "System.Collections.Generic",
  "sourceAssemblies": ["System.Private.CoreLib", "System.Runtime"],
  "types": {
    "List_1": {
      "clrName": "List`1",
      "tsEmitName": "List_1",
      "fullName": "System.Collections.Generic.List`1",
      "kind": "class",
      "accessibility": "public",
      "isAbstract": false,
      "isSealed": false,
      "isStatic": false,
      "isValueType": false,
      "baseType": "System.Object",
      "interfaces": [
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IEnumerable`1"
      ],
      "members": {
        "ctor()": {
          "kind": "constructor",
          "accessibility": "public",
          "parameters": []
        },
        "ctor(int)": {
          "kind": "constructor",
          "accessibility": "public",
          "parameters": [
            {
              "name": "capacity",
              "type": "System.Int32",
              "isRef": false,
              "isOut": false,
              "isParams": false,
              "defaultValue": null
            }
          ]
        },
        "Add": {
          "kind": "method",
          "clrName": "Add",
          "tsEmitName": "Add",
          "accessibility": "public",
          "isStatic": false,
          "isVirtual": false,
          "isAbstract": false,
          "isOverride": false,
          "isSealed": false,
          "returnType": "System.Void",
          "genericParameters": [],
          "parameters": [
            {
              "name": "item",
              "type": "T",
              "isRef": false,
              "isOut": false,
              "isParams": false,
              "defaultValue": null
            }
          ]
        },
        "Count": {
          "kind": "property",
          "clrName": "Count",
          "tsEmitName": "Count",
          "accessibility": "public",
          "isStatic": false,
          "isVirtual": false,
          "isAbstract": false,
          "isOverride": false,
          "type": "System.Int32",
          "canRead": true,
          "canWrite": false,
          "isIndexer": false
        }
      },
      "intentionalOmissions": {
        "indexers": [
          {
            "signature": "Item[int]",
            "reason": "Emitted to .d.ts as class surface indexer"
          }
        ],
        "genericStaticMembers": []
      }
    }
  }
}
```

## Type Name Conventions

### Generic Arity
**CLR**: Backtick notation (`List`1`, `Dictionary`2`)
**TypeScript**: Underscore notation (`List_1`, `Dictionary_2`)

**Example**:
```json
{
  "clrName": "List`1",
  "tsEmitName": "List_1",
  "fullName": "System.Collections.Generic.List`1"
}
```

### Nested Types
**CLR**: Plus sign (`Outer+Inner`)
**TypeScript**: Dollar sign (`Outer$Inner`)

**Example**:
```json
{
  "clrName": "List`1+Enumerator",
  "tsEmitName": "List_1$Enumerator",
  "fullName": "System.Collections.Generic.List`1+Enumerator"
}
```

### Generic Type References
Always use CLR backtick notation in type strings:

```json
{
  "type": "System.Collections.Generic.IEnumerable`1<T>"
}
```

## Usage by Tsonic Compiler

### Virtual Dispatch
```typescript
// TypeScript code
const list = new List_1<string>();
list.Add("hello");
```

**Tsonic compiler**:
1. Loads `System.Collections.Generic/metadata.json`
2. Looks up `List_1` → finds `clrName: "List`1"`
3. Looks up `Add` member → finds `isVirtual: false`
4. Emits C#: `list.Add("hello");` (non-virtual call)

### Property Access
```typescript
// TypeScript code
const count = list.Count;
```

**Tsonic compiler**:
1. Looks up `Count` in metadata
2. Finds `kind: "property"`, `canRead: true`
3. Emits C#: `list.Count` (property access, not method call)

### Indexer Handling
```typescript
// TypeScript code (indexers omitted from .d.ts)
// User must explicitly check metadata
```

**Tsonic compiler**:
1. Checks `intentionalOmissions.indexers`
2. Finds `"Item[int]"` omitted
3. Allows runtime binding to indexer despite missing .d.ts declaration

## Serialization Format

- **Encoding**: UTF-8
- **Formatting**: Indented (2 spaces)
- **Property naming**: camelCase
- **Null handling**: Explicit `null` for nullable fields
- **Array handling**: Empty arrays as `[]`, not `null`
