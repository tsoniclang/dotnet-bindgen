# Command-Line Interface Specification

## Command

```bash
tsbindgen generate [options]
```

## Assembly Input Options

### `--assembly`, `-a` (repeatable)
Path to a .NET assembly (`.dll`) to process.

**Usage**:
```bash
tsbindgen generate --assembly path/to/Assembly.dll
tsbindgen generate -a Assembly1.dll -a Assembly2.dll
```

### `--assembly-dir`, `-d`
Directory containing assemblies to process (non-recursive, `.dll` files only).

**Usage**:
```bash
tsbindgen generate --assembly-dir /path/to/assemblies
```

**Note**: Can be combined with `--assembly` to process both individual files and directories.

## Output Options

### `--out-dir`, `-o`
Output directory for generated files.

**Default**: `out/`

**Usage**:
```bash
tsbindgen generate --assembly Assembly.dll --out-dir ./types
```

## Library Mode

### `--lib`
Path to existing tsbindgen output package (base library contract).

When generating TypeScript declarations for a user assembly that references a base library (e.g., BCL), `--lib` excludes base library types from emission, producing a clean package containing only user types.

**Path**: Directory containing prior tsbindgen output with `metadata.json` and `bindings.json` files.

**Usage**:
```bash
# Step 1: Generate base BCL package (done once)
tsbindgen generate -d ~/dotnet/shared/Microsoft.NETCore.App/10.0.0 -o ./bcl-package

# Step 2: Generate user library, excluding BCL types
tsbindgen generate -a MyLib.dll -d ~/dotnet/.../10.0.0 \
  --lib ./bcl-package -o ./my-lib-package
```

**What `--lib` does**:
1. Loads contract from base package (`metadata.json` → type StableIds, `bindings.json` → runtime bindings)
2. **Filters** symbol graph: removes types IN contract, keeps types NOT in contract
3. **Validates** (LIB002): No dangling references (all type references must be in contract OR in current build)

**Validation**:
- **LIB001**: Contract directory/files exist (validated at load time, build fails if missing)
- **LIB002**: No dangling references (strict failure if user type references filtered-out type)

**Example error (LIB002)**:
```
Dangling reference detected:
  User member:     MyLib:MyCompany.Utils.Calculator::DoWork():void
  References:      System.Data.SqlClient:System.Data.SqlClient.SqlConnection
  Location:        return type
  Fix:             Add BCL types to --lib package OR remove dependency on this BCL type
```

**Result**: User package contains ONLY user types, BCL types excluded.

## Filter Options

### `--namespaces`, `-n`
Comma-separated list of namespaces to include (whitelist filter).

**Usage**:
```bash
tsbindgen generate -a Assembly.dll --namespaces System.Linq,System.Collections
```

## Naming Style Option

### `--naming`
Set the member naming style for generated TypeScript declarations.

**Values**: `clr` (default), `js`

**`clr`**: Preserve original C# names as-is.
```bash
--naming clr
# GetValue → GetValue
# Count → Count
# CC_CDECL → CC_CDECL
```

**`js`**: JavaScript-style lowerFirst for pure PascalCase members.
```bash
--naming js
# GetValue → getValue
# HelloWorld → helloWorld
# HTTPSConnection → httpsConnection
```

**Important**: The `js` style only transforms pure PascalCase. These patterns are preserved unchanged:
- ALL-UPPERCASE: `CC_CDECL` → `CC_CDECL`
- Underscore names: `Hello_World` → `Hello_World`
- CLR-reserved: `value__` → `value__`

**Note**: When `--naming js` is used, a `*.bindings.json` file is generated containing CLR name → TypeScript name mappings.

## Logging & Debug Options

### `--verbose`, `-v`
Show detailed generation progress.

**Default**: `false`

**Usage**:
```bash
tsbindgen generate -a Assembly.dll --verbose
```

### `--logs`
Enable logging for specific categories (repeatable).

**Categories**: `ViewPlanner`, `PhaseGate`, `ShapePass`, etc.

**Usage**:
```bash
tsbindgen generate -a Assembly.dll --logs PhaseGate ViewPlanner
```

### `--debug-snapshot`
Write intermediate snapshots to disk for debugging.

**Default**: `false`

**Output**: `<out-dir>/assemblies/*.snapshot.json`, `<out-dir>/namespaces/*.snapshot.json`

**Usage**:
```bash
tsbindgen generate -a Assembly.dll --debug-snapshot
```

### `--debug-typelist`
Write TypeScript type lists for debugging/verification.

**Default**: `false`

**Output**: `<namespace>/typelist.json` for each namespace

**Usage**:
```bash
tsbindgen generate -a Assembly.dll --debug-typelist
```

## Pipeline Selection

### `--use-new-pipeline`
Use Single-Phase Architecture pipeline (experimental).

**Default**: `false` (uses two-phase pipeline)

**Usage**:
```bash
tsbindgen generate -a Assembly.dll --use-new-pipeline
```

**Note**: The Single-Phase pipeline is the current greenfield implementation. This flag will eventually become the default.

## External Package Resolution (Planned)

The following options are part of the upcoming external package resolution feature:

### `--ref-path` (repeatable)
Directory containing installed tsbindgen packages for dependency resolution.

**Planned usage**:
```bash
tsbindgen generate -a MyApp.dll \
  --ref-path ./node_modules \
  --ref-path ./vendor-libs \
  --out-dir ./types
```

### `--external-map`
JSON file mapping external types to module specifiers.

**Planned usage**:
```bash
tsbindgen generate -a MyApp.dll \
  --external-map external-map.json \
  --out-dir ./types
```

See [ref-path.md](ref-path.md) for complete specification.

## Examples

### Generate BCL Declarations (Bundle Mode)
```bash
tsbindgen generate \
  --assembly-dir /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/ \
  --out-dir ./bcl-types \
  --use-new-pipeline
```

**Behavior**: Auto-detects BCL, loads transitive closure, emits all types.

### Generate User Assembly (Lean Mode, Planned)
```bash
tsbindgen generate \
  --assembly MyCompany.Feature.dll \
  --ref-path ./node_modules \
  --external-map external-map.json \
  --out-dir ./types \
  --use-new-pipeline
```

**Behavior**: Emits only `MyCompany.Feature.dll` types, imports BCL types from `@dotnet/bcl`.

### Generate with JavaScript-Style Naming
```bash
tsbindgen generate \
  --assembly MyApp.dll \
  --naming js \
  --out-dir ./types \
  --use-new-pipeline
```

**Output**: Members use JavaScript-style naming (lowerFirst), bindings file created.

### Generate with Verbose Logging
```bash
tsbindgen generate \
  --assembly Assembly.dll \
  --verbose \
  --logs PhaseGate ViewPlanner \
  --out-dir ./types \
  --use-new-pipeline
```

**Output**: Detailed progress + specific category logs.

### Generate with Debug Output
```bash
tsbindgen generate \
  --assembly Assembly.dll \
  --debug-snapshot \
  --debug-typelist \
  --out-dir ./types \
  --use-new-pipeline
```

**Output**: Includes intermediate snapshots and type lists for verification.

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Generation error (exception during processing) |
| 2 | No assemblies specified |
| 3 | Assembly directory not found |

## Environment Variables

None currently supported.

## Configuration Files

None currently supported. All configuration is via CLI options.
