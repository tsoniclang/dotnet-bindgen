# Troubleshooting

Common issues and solutions when using tsbindgen.

## Generation Issues

### Assembly Not Found

```
Error: Could not load assembly 'MyLibrary.dll'
```

**Causes:**
- Path is incorrect
- Assembly dependencies missing
- Wrong .NET version

**Solutions:**

```bash
# Verify assembly exists
ls -la ./MyLibrary.dll

# Ensure runtime directory contains dependencies
dotnet run -- generate -a ./MyLibrary.dll -d $DOTNET_RUNTIME -o ./out
```

### Missing Runtime Directory

```
Error: Runtime directory not specified
```

**Solution:**

```bash
# Find your runtime
dotnet --list-runtimes

# Use the path
dotnet run -- generate -d ~/.dotnet/shared/Microsoft.NETCore.App/10.0.0 -o ./out
```

### Namespace Not Generated

If a namespace is missing from output:

1. Check if types are public (internal types are skipped)
2. Verify namespace filter doesn't exclude it
3. Check verbose output for skip reasons

```bash
dotnet run -- generate -d $DOTNET_RUNTIME -o ./out -v 2>&1 | grep "MyNamespace"
```

## TypeScript Errors

### TS1xxx Syntax Errors

Syntax errors indicate a bug in tsbindgen. These should be reported.

```bash
# Capture full error output
node test/validate/validate.js 2>&1 | tee error-report.txt
```

### TS2304 - Cannot Find Name

```
error TS2304: Cannot find name 'int'.
```

**Cause:** Missing `@tsonic/types` import.

**Solution:** Ensure your tsconfig.json includes the types package:

```json
{
  "compilerOptions": {
    "types": ["@tsonic/types"]
  }
}
```

### TS2417 - Property Type Incompatible

```
error TS2417: Property 'value' of type 'string' is not assignable...
```

**Cause:** C# property covariance not supported in TypeScript.

**Status:** Fixed in v0.7.4. The `PropertyOverrideUnifier` pass now unifies covariant property types using union types. The BCL now compiles with **zero semantic errors**.

### TS2320 - Interface Cannot Extend

```
error TS2320: Interface 'X' cannot simultaneously extend types 'A' and 'B'.
```

**Cause:** Diamond inheritance with conflicting members.

**Status:** Fixed. The `SafeToExtendAnalyzer` detects conflicting members and routes them through views instead of extends.

### TS2430 - Interface Method Conflicts

```
error TS2430: Interface 'X' incorrectly extends interface 'Y'.
```

**Cause:** Method signature mismatch between class and inherited interface (e.g., `char` vs `CLROf<char>`).

**Status:** Fixed in v0.7.4. The `ClassPrinter` now emits inherited method overloads to satisfy both the class and interface contracts.

### TS2344 - Constraint Violations

```
error TS2344: Type 'T' does not satisfy the constraint 'C'.
```

**Cause:** Multi-arity facade type parameters passed to constrained internal types without verification.

**Status:** Fixed in v0.7.4. The `MultiArityAliasEmit` now uses nested constraint guards (`[T] extends [C] ? Result : never`) to verify constraints before dispatch.

## Library Mode Issues

### LIB001 - Type Not Found

```
Error LIB001: Type 'System.String' not found in library
```

**Cause:** The `--lib` directory is missing types your assembly references.

**Solutions:**

1. Regenerate BCL with same options
2. Use published `@tsonic/dotnet` package
3. Ensure all dependencies are in library path

### LIB002 - Signature Mismatch

```
Error LIB002: Member signature mismatch for 'List.Add'
```

**Cause:** Library was generated with different options (e.g., different `--naming`).

**Solution:** Regenerate library with matching options:

```bash
# Both must use same naming
dotnet run -- generate -d $DOTNET_RUNTIME -o ./bcl --naming js
dotnet run -- generate -a ./MyLib.dll -d $DOTNET_RUNTIME -o ./out --lib ./bcl --naming js
```

## Performance Issues

### Slow Generation

Full BCL generation takes 30-60 seconds. For faster iteration:

```bash
# Generate specific namespaces only
dotnet run -- generate -d $DOTNET_RUNTIME -o ./out -n System,System.Collections.Generic
```

### Out of Memory

For very large assemblies:

```bash
# Increase heap size
DOTNET_GCHeapHardLimit=2g dotnet run -- generate -d $DOTNET_RUNTIME -o ./out
```

## Validation Issues

### Validation Takes Too Long

Full validation runs TypeScript on 130 namespaces (2-3 minutes).

**Workaround:** Validate specific namespaces:

```bash
# Generate subset
dotnet run -- generate -d $DOTNET_RUNTIME -o .tests/subset -n System

# Validate just that
npx tsc --noEmit .tests/subset/System/index.d.ts
```

### Completeness Verification Fails

```
VERIFICATION FAILED - 5 types lost
```

**Action:** This indicates a bug. The lost types should be investigated:

```bash
# Run with verbose output
node test/validate/verify-completeness.js 2>&1 | grep "MISSING"
```

## Debugging Tips

### Enable Verbose Output

```bash
dotnet run -- generate -d $DOTNET_RUNTIME -o ./out -v
```

### Enable Specific Logs

```bash
dotnet run -- generate -d $DOTNET_RUNTIME -o ./out --logs ImportPlanner,FacadeEmitter
```

Available log categories:
- `ImportPlanner` - Import statement planning
- `FacadeEmitter` - Facade file generation
- `InternalIndexEmitter` - Internal declaration generation
- `ViewPlanner` - Explicit interface view planning
- `StructuralConformance` - Interface conformance analysis
- `SafeToExtendAnalyzer` - LINQ assignability analysis

### Inspect Generated Files

```bash
# View TypeScript output
cat output/System/index.d.ts

# View metadata
cat output/System/metadata.json | jq .

# View type list (what was emitted)
cat output/System/typelist.json | jq .
```

## Getting Help

If you encounter issues not covered here:

1. Check existing issues: https://github.com/tsoniclang/tsbindgen/issues
2. File a new issue with:
   - tsbindgen version
   - .NET version
   - Command used
   - Full error output
   - Minimal reproduction steps

