# Load Phase

The Load phase uses .NET reflection to extract type information from assemblies.

## Entry Point

**File:** `Load/AssemblyLoader.cs`

```csharp
public static SymbolGraph Load(
    BuildContext ctx,
    IEnumerable<string> assemblyPaths,
    string runtimeDirectory)
```

## MetadataLoadContext

Uses `System.Reflection.MetadataLoadContext` for isolated assembly inspection:

```csharp
var resolver = new PathAssemblyResolver(runtimeAssemblies);
using var mlc = new MetadataLoadContext(resolver);

foreach (var path in assemblyPaths)
{
    var assembly = mlc.LoadFromAssemblyPath(path);
    // Reflect types...
}
```

**Why MetadataLoadContext?**
- Can inspect assemblies without loading them into the runtime
- Works with assemblies targeting different .NET versions
- No conflict with already-loaded assemblies

## Type Reflection

For each public type, extracts:

| Property | Source |
|----------|--------|
| Name | `Type.Name` |
| Namespace | `Type.Namespace` |
| Assembly | `Type.Assembly.GetName().Name` |
| Base class | `Type.BaseType` |
| Interfaces | `Type.GetInterfaces()` |
| Generic parameters | `Type.GetGenericArguments()` |
| Attributes | `Type.GetCustomAttributesData()` |

## Member Reflection

For each type, reflects:

- **Methods** - `Type.GetMethods(BindingFlags.Public | ...)`
- **Properties** - `Type.GetProperties(...)`
- **Fields** - `Type.GetFields(...)`
- **Events** - `Type.GetEvents(...)`
- **Constructors** - `Type.GetConstructors(...)`

## Critical: Name-Based Comparisons

**MetadataLoadContext types cannot be compared with `typeof()`:**

```csharp
// WRONG - Always false for MetadataLoadContext types
if (type == typeof(int)) return "number";

// CORRECT - Use name-based comparison
if (type.FullName == "System.Int32") return "int";
```

This is because MetadataLoadContext creates isolated Type objects that are different instances from the runtime's typeof() results.

## Output: SymbolGraph

The Load phase produces a `SymbolGraph` containing:

- `Namespaces` - All namespaces found
- `Types` - All public types with members
- No indices yet (built in Model phase)
