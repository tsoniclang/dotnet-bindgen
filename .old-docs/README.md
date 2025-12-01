# tsbindgen

A .NET tool that generates TypeScript declaration files (`.d.ts`) from .NET assemblies for use with the Tsonic compiler.

## Overview

`tsbindgen` uses reflection to analyze .NET assemblies and produces TypeScript declarations that follow Tsonic's interop rules. This allows TypeScript code compiled with Tsonic to properly type-check when using .NET libraries.

## Installation

Build the tool from source:

```bash
dotnet build
```

## Usage

### Basic Usage

```bash
tsbindgen <assembly-path>
```

Example:

```bash
tsbindgen /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.0/ref/net8.0/System.Text.Json.dll
# Creates:
#   ./System.Text.Json.d.ts
#   ./System.Text.Json.metadata.json
```

### Command-Line Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--namespaces` | `-n` | Comma-separated list of namespaces to include | All namespaces |
| `--out-dir` | `-o` | Output directory for generated file | `.` (current directory) |
| `--log` | `-l` | Path to write JSON log file | None |
| `--config` | `-c` | Path to configuration JSON file | None |

### Examples

**Filter specific namespaces:**

```bash
tsbindgen System.Text.Json.dll --namespaces System.Text.Json.Serialization
```

**Specify output directory:**

```bash
tsbindgen System.Net.Http.dll --out-dir ./declarations
```

**Generate with logging:**

```bash
tsbindgen System.IO.dll --log build.log.json
```

**Generate user library excluding BCL types:**

```bash
# Step 1: Generate BCL package (once)
tsbindgen generate -d ~/dotnet/shared/Microsoft.NETCore.App/10.0.0 -o ./bcl-package

# Step 2: Generate user library, BCL types excluded
tsbindgen generate -a MyLib.dll -d ~/dotnet/.../10.0.0 \
  --lib ./bcl-package -o ./my-lib-package
```

## Library Mode (`--lib`)

When generating declarations for a user assembly that references a base library (e.g., .NET BCL), use `--lib` to exclude base library types from output. This produces a clean package containing only your library's types.

**How it works:**
1. Generate base library package first (contains `metadata.json` + `bindings.json`)
2. Use `--lib <base-package-path>` when generating user library
3. tsbindgen filters: keeps types NOT in base, removes types IN base
4. Validates no dangling references (LIB002 strict check)

**Use case:** Publishing TypeScript declarations for a .NET library without duplicating BCL type definitions.

See [CLI documentation](spec/cli.md#library-mode) for complete details.

## Generated Output

The tool generates two files for each assembly:

1. **TypeScript declarations** (`.d.ts`) - TypeScript type definitions
2. **Metadata sidecar** (`.metadata.json`) - C# semantic information

### TypeScript Declarations

The `.d.ts` file contains TypeScript declarations with:

1. **Branded type aliases** for C# numeric types:
   ```typescript
   type int = number & { __brand: "int" };
   type decimal = number & { __brand: "decimal" };
   // ... etc
   ```

2. **Namespace declarations** matching .NET namespaces:
   ```typescript
   declare namespace System.Text.Json {
     class JsonSerializer {
       static Serialize<T>(value: T): string;
     }
   }
   ```

3. **Proper type mappings**:
   - `System.String` → `string`
   - `System.Int32` → `int`
   - `System.Boolean` → `boolean`
   - `Task<T>` → `Promise<T>`
   - `T[]` → `ReadonlyArray<T>`
   - `List<T>` → `List<T>`
   - `Nullable<T>` → `T | null`

### Metadata Sidecar Files

The `.metadata.json` file contains C# semantic information that TypeScript cannot express. This enables the Tsonic compiler to generate correct C# code, particularly for:

- **Virtual/override methods** - Required to correctly override base class methods
- **Abstract classes/methods** - Required to properly extend abstract types
- **Sealed classes/methods** - Prevents invalid inheritance
- **Static classes** - Type-level restrictions
- **Struct vs Class** - Value vs reference type semantics
- **Method accessibility** - Public, protected, private, internal modifiers

#### Example Structure

```json
{
  "assemblyName": "System.Text.Json",
  "assemblyVersion": "10.0.0.0",
  "types": {
    "System.Text.Json.JsonSerializer": {
      "kind": "class",
      "isAbstract": true,
      "isSealed": false,
      "isStatic": false,
      "baseType": null,
      "interfaces": [],
      "members": {
        "Serialize<T>(T)": {
          "kind": "method",
          "isVirtual": false,
          "isAbstract": false,
          "isSealed": false,
          "isOverride": false,
          "isStatic": true,
          "accessibility": "public"
        },
        "Deserialize<T>(string)": {
          "kind": "method",
          "isVirtual": false,
          "isAbstract": false,
          "isSealed": false,
          "isOverride": false,
          "isStatic": true,
          "accessibility": "public"
        }
      }
    }
  }
}
```

#### Metadata Fields

**Type-level fields:**
- `kind`: `"class"`, `"struct"`, `"interface"`, or `"enum"`
- `isAbstract`: True for abstract classes (excluding interfaces)
- `isSealed`: True for sealed classes (excluding value types and enums)
- `isStatic`: True for static classes
- `baseType`: Full name of base class (if any)
- `interfaces`: Array of implemented interface names
- `members`: Dictionary of member metadata keyed by signature

**Member-level fields:**
- `kind`: `"method"`, `"property"`, or `"constructor"`
- `isVirtual`: True if method can be overridden
- `isAbstract`: True for abstract methods
- `isSealed`: True if method prevents further overriding
- `isOverride`: True if method overrides a base method
- `isStatic`: True for static members
- `accessibility`: `"public"`, `"protected"`, `"private"`, `"internal"`, etc.

**Signature format:**
- Methods: `MethodName(Type1,Type2,...)` using C# type names
- Properties: `PropertyName`
- Constructors: `ctor(Type1,Type2,...)`

## Configuration File

You can provide a JSON configuration file to customize behavior:

```json
{
  "skipNamespaces": ["System.Internal"],
  "typeRenames": {
    "System.OldType": "NewType"
  },
  "skipMembers": [
    "System.String::InternalMethod"
  ]
}
```

Usage:

```bash
tsbindgen Assembly.dll --config config.json
```

## Log Output

When using `--log`, a JSON file is generated with:

```json
{
  "timestamp": "2025-11-01T13:03:38Z",
  "namespaces": ["System.Text.Json"],
  "typeCounts": {
    "classes": 40,
    "interfaces": 5,
    "enums": 10,
    "total": 55
  },
  "warnings": []
}
```

## Type Mapping Rules

The tool follows Tsonic's type mapping specification:

- **Classes** → TypeScript classes
- **Interfaces** → TypeScript interfaces
- **Enums** → TypeScript enums
- **Structs** → TypeScript classes
- **Static methods** → `static` methods
- **Properties** → TypeScript properties (with `readonly` when appropriate)
- **Generic types** → TypeScript generics `<T>`
- **Optional parameters** → `param?: Type`
- **Params arrays** → `...values: ReadonlyArray<T>`

## Excluded Members

The tool automatically skips:

- Private and internal members
- Compiler-generated types
- Common Object methods (`Equals`, `GetHashCode`, `ToString`, `GetType`, `ReferenceEquals`)
- Special-name members (property accessors, backing fields)

## Reserved Keyword Escaping

Parameter names that conflict with TypeScript/JavaScript reserved keywords are automatically escaped by prefixing them with an underscore. This prevents syntax errors in the generated declarations.

**Escaped keywords include:**
- Control flow: `break`, `case`, `catch`, `continue`, `default`, `do`, `else`, `finally`, `for`, `if`, `return`, `switch`, `throw`, `try`, `while`
- Declarations: `class`, `const`, `enum`, `export`, `extends`, `function`, `import`, `let`, `var`, `void`
- Modifiers: `async`, `await`, `implements`, `interface`, `package`, `private`, `protected`, `public`, `static`, `yield`
- Special identifiers: `arguments`, `eval`, `this`, `super`, `new`, `typeof`, `instanceof`, `delete`, `debugger`, `with`, `in`

**Example:**
```csharp
// C# method signature
public static LoopExpression Loop(Expression body, LabelTarget break, LabelTarget continue)

// Generated TypeScript
static Loop(body: Expression, _break: LabelTarget, _continue: LabelTarget): LoopExpression;
```

This ensures that all generated `.d.ts` files are valid TypeScript and can be parsed by the TypeScript compiler without syntax errors.

## Testing & Validation

The project includes comprehensive test scripts to ensure correctness and prevent regressions.

### Running All Tests

```bash
# TypeScript syntax validation
npm install  # First time only
npm run validate

# Regression guards (run all)
./test/scripts/test-determinism.sh      # Deterministic output
./test/scripts/test-strict-mode.sh      # Strict mode compliance
./test/scripts/test-surface-manifest.sh # Surface baseline guard
./test/scripts/test-lib.sh              # Library mode (--lib)
```

### TypeScript Validation

Ensures all generated `.d.ts` files are syntactically valid TypeScript:

```bash
npm run validate
```

This script:
1. Regenerates all 38 BCL assemblies to a temporary directory
2. Creates an `index.d.ts` with triple-slash references
3. Runs the TypeScript compiler to validate all declarations
4. Reports syntax errors (TS1xxx), duplicate type errors (TS6200), and semantic errors (TS2xxx)

**Success criteria:**
- ✅ **Zero syntax errors (TS1xxx)** - All output is valid TypeScript
- ⚠️ **Semantic errors acceptable** - TS2xxx errors are expected (known limitations)

**Example output:**
```
✓ VALIDATION PASSED

All 38 assemblies generated successfully
All metadata files present
✓ No TypeScript syntax errors (TS1xxx)

Error breakdown:
  - Syntax errors (TS1xxx): 0 ✓
  - Duplicate types (TS6200): 0 (expected)
  - Semantic errors (TS2xxx): 1 (expected - missing cross-assembly refs)
```

### Regression Guards

#### Determinism Test (`test-determinism.sh`)

Ensures tsbindgen produces identical output across runs:

```bash
./test/scripts/test-determinism.sh
```

**What it tests:**
- Same input → same output (bit-for-bit identical)
- No nondeterministic ordering or formatting
- Critical for reproducible builds and diffing

#### Strict Mode Test (`test-strict-mode.sh`)

Verifies strict mode compliance and performance baseline:

```bash
./test/scripts/test-strict-mode.sh
```

**What it tests:**
- No diagnostics with `--strict` flag
- Performance doesn't regress beyond baseline

#### Surface Baseline Test (`test-surface-manifest.sh`)

Guards against accidental removal of public API surface:

```bash
./test/scripts/test-surface-manifest.sh
```

**What it tests:**
- Type count matches baseline (prevents deletions)
- Member count matches baseline (prevents deletions)
- Intentional changes require baseline update

#### Library Mode Test (`test-lib.sh`)

Validates `--lib` mode filters base library types correctly:

```bash
./test/scripts/test-lib.sh
```

**What it tests:**
- BCL package generation succeeds
- User library builds successfully
- Full generation includes both user + BCL types
- `--lib` filtered generation includes ONLY user types
- No LIB001-003 validation errors
- BCL namespaces correctly excluded from filtered output
- User namespaces correctly included in filtered output

**Expected result:**
```
✓ LIBRARY MODE FULLY VERIFIED

Summary:
  ✓ BCL generation succeeded (130 namespaces)
  ✓ User library build succeeded
  ✓ Full generation: 131 namespaces (user + BCL)
  ✓ Filtered generation: 1 namespaces (user only)
  ✓ BCL types correctly excluded via --lib
  ✓ User types (MyCompany.Utils) correctly included
  ✓ No LIB001-003 validation errors
  ✓ Strict mode passes
```

## Development

### Project Structure

```
tsbindgen/
├── Src/
│   ├── Program.cs              # CLI entry point
│   ├── AssemblyProcessor.cs    # Reflection and type/metadata extraction
│   ├── TypeMapper.cs           # C# to TypeScript type mapping
│   ├── DeclarationRenderer.cs  # TypeScript output generation
│   ├── TypeInfo.cs             # Data structures for declarations
│   ├── MetadataModel.cs        # Data structures for metadata
│   ├── SignatureFormatter.cs   # Method/property signature formatting
│   ├── MetadataWriter.cs       # JSON metadata serialization
│   ├── GeneratorConfig.cs      # Configuration support
│   └── GenerationLogger.cs     # Logging functionality
├── Scripts/
│   └── validate.js             # Full validation script
├── package.json                # npm scripts (validate)
└── README.md
```

### Building

```bash
dotnet build
```

### Running

```bash
dotnet run --project Src -- <assembly-path> [options]
```

## Related Documentation

- [Tsonic Type Mappings](../tsonic/spec/04-type-mappings.md)
- [.NET Interop](../tsonic/spec/08-dotnet-interop.md)
- [.NET Declarations](../tsonic/spec/14-dotnet-declarations.md)

## License

See LICENSE file for details.
