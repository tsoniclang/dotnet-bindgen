# Naming Conventions

tsbindgen supports two naming conventions for members.

## CLR Mode (Default)

Members retain their C#/CLR PascalCase names.

```bash
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out
# or explicitly:
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out --naming clr
```

**Output:**

```typescript
list.GetEnumerator();
Console.WriteLine("hello");
String.IsNullOrEmpty(s);
Array.Sort(array);
```

## JavaScript Mode

Members are converted to camelCase.

```bash
npx tsbindgen generate -d $DOTNET_RUNTIME -o ./out --naming js
```

**Output:**

```typescript
list.getEnumerator();
Console.writeLine("hello");
String.isNullOrEmpty(s);
Array.sort(array);
```

## Conversion Rules

### Basic Conversion

First letter becomes lowercase:

| CLR | JavaScript |
|-----|------------|
| `GetEnumerator` | `getEnumerator` |
| `WriteLine` | `writeLine` |
| `ToString` | `toString` |

### Acronyms

Acronyms are lowercased as a unit:

| CLR | JavaScript |
|-----|------------|
| `XMLReader` | `xmlReader` |
| `HTTPClient` | `httpClient` |
| `IOStream` | `ioStream` |

### Single Letters

Single-letter prefixes are lowercased:

| CLR | JavaScript |
|-----|------------|
| `IDisposable` | `iDisposable` |
| `TKey` | `tKey` |

## Type Names

Type names are **never** transformed - always PascalCase:

```typescript
// Both modes:
List_1<T>
Dictionary_2<TKey, TValue>
IEnumerable_1<T>
```

## Extension Methods

Extension method names follow the same naming mode:

- `--naming clr`: `Where`, `Select`, `ToList`, ...
- `--naming js`: `where`, `select`, `toList`, ...

## Reserved Words

Reserved words are sanitized only when they appear in **Identifier** contexts (for example: binding identifiers like parameters, and type names).

Member names (methods/properties) are emitted in **IdentifierName** positions, so keywords are allowed and are emitted as-is.

### Binding identifiers (params/vars)

| Original | TypeScript |
|----------|-----------|
| `default` | `default_` |
| `class` | `class_` |
| `function` | `function_` |
| `import` | `import_` |

### Member names (methods/properties)

- No `_` suffix is added for keywords.
- Examples: `delete()`, `export()`, `with()`, `type`, `from`, `set(...)`, `get(...)`

## Name Conflicts

When multiple members would have the same name, numeric suffixes are added:

```typescript
// Two methods both become "add" after camelCase
add(item: T): void;
add2(index: int, item: T): void;
```

## Explicit Interface Members

Explicit interface implementations get interface-suffixed names:

```csharp
// C#
void ICollection.Clear() { }
void Clear() { }
```

```typescript
// TypeScript
clear(): void;           // Own method
clear_ICollection(): void; // Explicit implementation
```

## Choosing a Mode

**Use CLR mode when:**
- Interop with existing C# code
- Documentation references use C# names
- Team is familiar with C# conventions

**Use JavaScript mode when:**
- TypeScript-first development
- Following JavaScript naming conventions
- IDE autocomplete matches expectations
