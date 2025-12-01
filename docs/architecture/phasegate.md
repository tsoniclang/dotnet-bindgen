# PhaseGate Validation

PhaseGate validates the SymbolGraph before emission. It catches invariant violations that would produce invalid TypeScript.

## When It Runs

After Plan phase, before Emit phase:

```
Load -> Model -> Shape -> Normalize -> Plan -> [PhaseGate] -> Emit
```

If PhaseGate finds errors, emission is blocked.

## Diagnostic Codes

Diagnostics use prefix `TBG` (tsbindgen) with numeric codes:

| Range | Category |
|-------|----------|
| TBG001-099 | Type mapping errors |
| TBG100-199 | Load/resolution errors |
| TBG200-299 | Export/import errors |
| TBG300-399 | Finalization errors |
| TBG400-499 | View/interface errors |
| TBG500-599 | Name collision errors |
| TBG800-899 | Printer consistency errors |

Library mode adds `LIB001-LIB003` for contract validation.

## Key Validation Rules

### Type Resolution

| Code | Rule |
|------|------|
| TBG001 | No unmapped unsafe types (pointers without ptr<T>) |
| TBG101 | All external type references resolved |
| TBG102 | No mixed assembly versions |

### Export Correctness

| Code | Rule |
|------|------|
| TBG201 | All imports reference exported types |
| TBG202 | No circular namespace dependencies |

### Finalization

| Code | Rule |
|------|------|
| TBG301 | Every type has final name in namespace scope |
| TBG302 | Every member has final name in correct scope |
| TBG303 | EmitScope set for all members |

### View Correctness

| Code | Rule |
|------|------|
| TBG401 | ViewOnly members have view scope names |
| TBG402 | As_ properties exist for all views |

### Name Collisions

| Code | Rule |
|------|------|
| TBG501 | No duplicate type names in namespace |
| TBG502 | No duplicate member names on type surface |

### Library Mode

| Code | Rule |
|------|------|
| LIB001 | Base package contract loaded |
| LIB002 | No dangling references to filtered types |
| LIB003 | Signature compatibility with base |

## Severity Levels

- **Error**: Blocks emission. Must be fixed.
- **Warning**: Emits but may cause issues.
- **Info**: Informational only.

## Output Format

PhaseGate produces `diagnostics.txt` and `summary.json`:

```
=== PhaseGate Validation ===
Errors: 0
Warnings: 2

TBG401 [Warning] System.Collections.Generic.List`1
  ViewOnly member 'GetEnumerator' missing view scope name
```

## Debugging Failures

1. Check diagnostic code in output
2. Find the type/member mentioned
3. Trace back to Shape/Normalize pass that should have set the value
4. Fix the pass, re-run

Common causes:
- Missing Shape pass for edge case
- Normalize didn't reserve name in correct scope
- Type reference to assembly not in load set

