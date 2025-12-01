# Bindings JSON Schema

`<Namespace>/bindings.json` provides CLR-to-TypeScript name mappings and member metadata for runtime binding. This file is consumed by the Tsonic compiler for name resolution and method dispatch.

## Purpose

The bindings file enables:
1. **Name resolution**: Map TypeScript names back to CLR names (when transforms applied)
2. **Method dispatch**: Identify extension methods, static methods, metadata tokens
3. **Assembly resolution**: Track which assembly contains each type/member
4. **Exposed member tracking**: Find inherited/exposed members from base classes

## File Location

```
<output-dir>/
  System.Linq/
    internal/
      index.d.ts           # TypeScript declarations
      metadata.json        # CLR semantics
    index.d.ts             # Facade
    bindings.json          # ← Binding metadata (this file)
```

## Root Schema

```json
{
  "namespace": "System.Linq",
  "types": [
    {
      "stableId": "System.Linq:System.Linq.Enumerable",
      "clrName": "System.Linq.Enumerable",
      "tsEmitName": "Enumerable",
      "assemblyName": "System.Linq",
      "metadataToken": 0,
      "methods": [...],
      "properties": [...],
      "fields": [...],
      "events": [...],
      "constructors": [...],
      "exposedMethods": [...],
      "exposedProperties": [...],
      "exposedFields": [...],
      "exposedEvents": [...],
      "exposedConstructors": [...]
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `namespace` | string | Namespace name |
| `types` | TypeBinding[] | All types in this namespace |

## TypeBinding

```json
{
  "stableId": "System.Linq:System.Linq.Enumerable",
  "clrName": "System.Linq.Enumerable",
  "tsEmitName": "Enumerable",
  "assemblyName": "System.Linq",
  "metadataToken": 0,
  "methods": [...],
  "properties": [...],
  "fields": [...],
  "events": [...],
  "constructors": [...]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `stableId` | string | Unique identifier (assembly:fullName) |
| `clrName` | string | Fully-qualified CLR name |
| `tsEmitName` | string | TypeScript identifier |
| `assemblyName` | string | Assembly containing this type |
| `metadataToken` | int | CLR metadata token (0 for types) |
| `methods` | MethodBinding[] | Method definitions |
| `properties` | PropertyBinding[] | Property definitions |
| `fields` | FieldBinding[] | Field definitions |
| `events` | EventBinding[] | Event definitions |
| `constructors` | ConstructorBinding[] | Constructor definitions |
| `exposedMethods` | ExposedMember[]? | Inherited methods exposed on this type |
| `exposedProperties` | ExposedMember[]? | Inherited properties exposed on this type |
| `exposedFields` | ExposedMember[]? | Inherited fields exposed on this type |
| `exposedEvents` | ExposedMember[]? | Inherited events exposed on this type |
| `exposedConstructors` | ExposedMember[]? | Inherited constructors exposed on this type |

## MethodBinding

```json
{
  "stableId": "System.Linq:System.Linq.Enumerable::Where(IEnumerable_1,Func_2):IEnumerable_1",
  "clrName": "Where",
  "tsEmitName": "Where",
  "metadataToken": 100663496,
  "canonicalSignature": "(IEnumerable_1,Func_2):IEnumerable_1",
  "normalizedSignature": "Where|(IEnumerable_1,Func_2):IEnumerable_1|static=true",
  "emitScope": "ClassSurface",
  "arity": 1,
  "parameterCount": 2,
  "declaringClrType": "System.Linq.Enumerable",
  "declaringAssemblyName": "System.Linq",
  "isExtensionMethod": true
}
```

| Field | Type | Description |
|-------|------|-------------|
| `stableId` | string | Unique identifier (assembly:type::signature) |
| `clrName` | string | Original CLR method name |
| `tsEmitName` | string | TypeScript identifier (may have suffix for overloads) |
| `metadataToken` | int | CLR metadata token for reflection |
| `canonicalSignature` | string | Signature for matching |
| `normalizedSignature` | string | Full normalized signature |
| `emitScope` | string | `"ClassSurface"`, `"StaticSurface"`, `"ViewOnly"`, `"Omitted"` |
| `arity` | int | Number of generic type parameters |
| `parameterCount` | int | Number of parameters |
| `declaringClrType` | string | CLR type that declares this method |
| `declaringAssemblyName` | string | Assembly containing the declaring type |
| `isExtensionMethod` | bool | True if C# extension method (first param is `this`) |

## PropertyBinding

```json
{
  "stableId": "System.Collections.Generic:System.Collections.Generic.List`1::CountSystem.Int32",
  "clrName": "Count",
  "tsEmitName": "Count",
  "metadataToken": 385876045,
  "canonicalSignature": "Count|System.Int32",
  "normalizedSignature": "Count|System.Int32|get=true|set=false",
  "emitScope": "ClassSurface",
  "isIndexer": false,
  "hasGetter": true,
  "hasSetter": false,
  "declaringClrType": "System.Collections.Generic.List`1",
  "declaringAssemblyName": "System.Private.CoreLib"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `stableId` | string | Unique identifier |
| `clrName` | string | Original CLR property name |
| `tsEmitName` | string | TypeScript identifier |
| `metadataToken` | int | CLR metadata token |
| `canonicalSignature` | string | Property signature |
| `normalizedSignature` | string | Full normalized signature |
| `emitScope` | string | Emit scope |
| `isIndexer` | bool | True if this is an indexer property |
| `hasGetter` | bool | Has get accessor |
| `hasSetter` | bool | Has set accessor |
| `declaringClrType` | string | CLR type that declares this property |
| `declaringAssemblyName` | string | Assembly containing the declaring type |

## FieldBinding

```json
{
  "stableId": "System.Linq.Parallel:System.Linq.ParallelExecutionMode::DefaultSystem.Linq.ParallelExecutionMode",
  "clrName": "Default",
  "tsEmitName": "Default",
  "metadataToken": 67108889,
  "normalizedSignature": "Default|System.Linq.ParallelExecutionMode|static=true|const=true",
  "isStatic": true,
  "isReadOnly": false,
  "declaringClrType": "System.Linq.ParallelExecutionMode",
  "declaringAssemblyName": "System.Linq.Parallel"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `stableId` | string | Unique identifier |
| `clrName` | string | Original CLR field name |
| `tsEmitName` | string | TypeScript identifier |
| `metadataToken` | int | CLR metadata token |
| `normalizedSignature` | string | Normalized signature |
| `isStatic` | bool | Static field |
| `isReadOnly` | bool | Readonly field |
| `declaringClrType` | string | CLR type that declares this field |
| `declaringAssemblyName` | string | Assembly containing the declaring type |

## ExposedMember

Exposed members are inherited members from base classes that appear on the TypeScript type surface but are defined elsewhere.

```json
{
  "tsName": "GetHashCode",
  "isStatic": false,
  "tsSignatureId": "GetHashCode|():System.Int32|static=false",
  "target": {
    "declaringClrType": "System.Object",
    "declaringAssemblyName": "System.Private.CoreLib",
    "metadataToken": 100663363
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `tsName` | string | TypeScript method name on this type |
| `isStatic` | bool | Static member |
| `tsSignatureId` | string | Signature identifier for matching |
| `target` | MemberTarget | Where the member is actually defined |

### MemberTarget

| Field | Type | Description |
|-------|------|-------------|
| `declaringClrType` | string | CLR type that defines this member |
| `declaringAssemblyName` | string | Assembly containing the defining type |
| `metadataToken` | int | CLR metadata token of the defining member |

## Usage by Tsonic Compiler

### Extension Method Detection

```csharp
// User wrote: nums.Where(x => x > 0)
// Look up method binding
var method = typeBinding.methods.Find(m => m.tsEmitName == "Where");
if (method.isExtensionMethod) {
    // Emit: Enumerable.Where(nums, x => x > 0)
    var staticClass = method.declaringClrType; // "System.Linq.Enumerable"
}
```

### Name Resolution

```csharp
// TypeScript: list.count
// Find property binding
var prop = typeBinding.properties.Find(p => p.tsEmitName == "count");
var clrName = prop.clrName; // "Count"
// Emit C#: list.Count
```

### Exposed Member Resolution

```csharp
// TypeScript: myEnum.GetHashCode()
// Not in type's own methods, check exposedMethods
var exposed = typeBinding.exposedMethods.Find(m => m.tsName == "GetHashCode");
// Target is System.Object.GetHashCode
var target = exposed.target;
// Use metadata token for reflection: target.metadataToken
```

## Serialization Format

- **Encoding**: UTF-8
- **Formatting**: Indented (2 spaces)
- **Property naming**: camelCase (JavaScript convention)
- **Null handling**: Null/empty arrays omitted
