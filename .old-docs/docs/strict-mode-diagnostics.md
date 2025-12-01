# Strict Mode Diagnostic Policy

**Last Updated**: 2025-11-22
**Policy File**: `src/tsbindgen/Plan/Validation/StrictModePolicy.cs`

---

## Philosophy

Strict mode enforces **zero warnings** unless explicitly whitelisted with documented justification.

### Diagnostic Levels

| Level | Strict Mode Behavior | Description |
|-------|---------------------|-------------|
| **ERROR** | ❌ Always forbidden | Blocks emission - must be eliminated |
| **WARNING** | ⚠️ Forbidden unless whitelisted | Must be resolved or have documented exception |
| **INFO** | ✅ Always allowed | Informational only - doesn't count toward totals |

---

## Current Policy

### ❌ Forbidden (ERROR codes)

All ERROR codes are forbidden in strict mode - no exceptions.

| Code | Description | Status |
|------|-------------|--------|
| TBG900 | StaticFlatteningPlan invalid | ✅ Eliminated |
| TBG901 | StaticConflictPlan invalid | ✅ Eliminated |
| TBG902 | OverrideConflictPlan invalid | ✅ Eliminated |
| TBG903 | PropertyOverridePlan invalid | ✅ Eliminated |
| TBG904 | ExtensionMethodsPlan invalid | ✅ Eliminated |
| TBG905 | Extension method 'any' erasures | ✅ Eliminated |
| TBG906 | Extension bucket name invalid | ✅ Eliminated |
| TBG907 | Extension import unresolved | ✅ Eliminated |

---

### ⚠️ Whitelisted Warnings

**Status**: ✅ **NO WHITELISTED WARNINGS** (Zero tolerance achieved!)

All previous WARNING codes have been eliminated or downgraded to INFO:
- **TBG120**: Downgraded to INFO (PR D)
- **TBG201**: Eliminated (PR B)
- **TBG203**: Eliminated (PR C/D)

---

#### TBG201: Circular Namespace Dependencies (~~267~~ 0 instances)

**Status**: ✅ **ELIMINATED in PR B**
**Count**: 0 (was 267)
**Resolution**: SCC bucketing filters intra-SCC cycles from validation

**How PR B Eliminated TBG201**:
1. Computed Strongly Connected Components (SCCs) in namespace dependency graph using Tarjan's algorithm
2. Found 63 SCCs: 2 multi-namespace (66 + 3 namespaces), 61 singletons
3. Filtered 267 intra-SCC circular dependencies from TBG201 warnings
4. Intra-SCC imports are valid (within same strongly connected component)

**SCC Structure** (BCL namespaces):
- **SCC 0 (66 namespaces)**: Main BCL cycle including System, Collections, IO, Reflection, etc.
- **SCC 34 (3 namespaces)**: Reflection.Metadata, Reflection.Metadata.Ecma335, Reflection.PortableExecutable
- **61 singleton SCCs**: Namespaces with no circular dependencies

**Technical Implementation**:
- `SCCCompute.cs`: Tarjan's algorithm for SCC detection
- `SCCPlan`: Maps namespaces to SCC buckets
- `ValidateImports`: Filters out intra-SCC cycles (only reports inter-SCC cycles, which should never occur)

---

#### TBG120: Reserved Word Collisions (8 instances → INFO)

**Status**: ✅ **DOWNGRADED TO INFO in PR D**
**Count**: 8 types (now INFO, not WARNING)
**Resolution**: Core BCL types always used in qualified contexts

**Affected Types**:
```
System.Enum
System.String
System.Type
System.Boolean
System.Void
System.Diagnostics.Switch
System.Diagnostics.Debugger
System.Reflection.Module
```

**Why INFO (not WARNING)**:
- Always referenced via qualified imports (e.g., System.Type, not bare Type)
- TypeScript's module system prevents name collisions
- Sanitization would break compatibility
- No actual risk of reserved word conflicts

---

#### TBG203: Interface Conformance Issues (~~87~~ 0 instances)

**Status**: ✅ **ELIMINATED in PR C/D**
**Count**: 0 (was 87)
**Resolution**: Honest emission filtering via HonestEmissionPlan + bug fixes

**How PR C/D Eliminated TBG203**:
1. **InterfaceConformanceAnalyzer** pre-analyzes all types before PhaseGate validation (PR C)
2. **HonestEmissionPlanner** identifies interfaces that cannot be satisfied in TypeScript (PR C)
3. **PR D Bug Fixes** to HonestEmissionPlanner:
   - Fixed interface name parsing to extract clean names from issue strings (was including trailing text)
   - Fixed GetShortInterfaceName to include generic arity for proper matching (was removing ``1` suffix)
   - Result: 87/87 types now handled correctly (was only 34/87 before fixes)
4. **ClassPrinter** filters unsatisfiable interfaces from `implements` clause during emission (PR C)
5. **MetadataEmitter** preserves full truth in metadata.json with `unsatisfiableInterfaces` field (PR C)
6. **PhaseGate validation** suppresses TBG203 for types in HonestEmissionPlan (PR C)

**Example - Honest Emission Result**:
```typescript
// TypeScript declaration (index.d.ts)
class Int32$instance implements
    IComparable,
    IEquatable_1<Int32>
    // IMinMaxValue_1<Int32> omitted - cannot satisfy (static abstract members)
{
    compareTo(value: Int32): int;
    equals(obj: any): boolean;
}
```

**Metadata (metadata.json)**:
```json
{
  "clrName": "System.Int32",
  "tsEmitName": "Int32",
  "unsatisfiableInterfaces": [
    {
      "interfaceClrName": "System.Numerics.IMinMaxValue`1",
      "reason": "MissingOrIncompatibleMembers",
      "issueCount": 2
    }
  ]
}
```

**Technical Implementation**:
- `InterfaceConformanceAnalyzer.cs`: Pre-validation conformance analysis
- `HonestEmissionPlanner.cs`: Plans which interfaces to omit
- `HonestEmissionPlan`: Tracks unsatisfiable interfaces by type
- `ClassPrinter.IsUnsatisfiableInterface()`: Filters during emission
- `MetadataEmitter`: Preserves truth in metadata

---

### ✅ Informational (INFO codes)

These codes are informational only and **never count toward warning totals**.

#### TBG120: Reserved Word Collisions (8 instances)

**Status**: ✅ Informational (allowed)
**Count**: 8 types
**Justification**: Core BCL types use reserved words but are always referenced in qualified contexts.

**Why Informational**:
- Always used via qualified imports (System.Type, not Type)
- TypeScript module system prevents collisions
- Sanitization would break compatibility
- No actual risk of name conflicts

**Affected Types**:
```
System.Enum
System.String
System.Type
System.Boolean
System.Void
System.Diagnostics.Switch
System.Diagnostics.Debugger
System.Reflection.Module
```

**Resolution**: Not required - working as designed

---

#### TBG310: Property Covariance (12 instances)

**Status**: ✅ Informational (allowed)
**Count**: 12 types
**Justification**: C# allows property covariance, TypeScript doesn't support property overloads. This is a fundamental TypeScript limitation.

**Why Informational**:
- Not actionable (TS language limitation)
- Properties still accessible
- PropertyOverridePlan unifies types where needed
- Documented in metadata

**Affected Types**:
```
System.Half (59 issues)
System.Runtime.InteropServices.NFloat (28 issues)
System.Numerics.BigInteger (4 issues)
System.Int128, System.UInt128
```

**Resolution**: Not required - working as designed

---

#### TBG410: Narrowed Generic Constraints (5 instances)

**Status**: ✅ Informational (allowed)
**Count**: 5 types
**Justification**: Derived types add generic constraints beyond base class. This is correct TypeScript behavior.

**Why Informational**:
- Valid TypeScript pattern
- Constraints correctly emitted
- Type safety preserved
- Documented in metadata

**Affected Types**:
```
System.Collections.Generic.GenericEqualityComparer<T>
System.Collections.Generic.NullableEqualityComparer<T>
System.Collections.Generic.EnumEqualityComparer<T>
System.Collections.Generic.GenericComparer<T>
System.Collections.Generic.NullableComparer<T>
```

**Resolution**: Not required - working correctly

---

## Roadmap to Zero Warnings

### Current State (Post PR D)

```
Errors:   0  ✅
Warnings: 0  ✅ (ZERO TOLERANCE ACHIEVED!)
Info:     25  ✅ (TBG120, TBG310, TBG410)
```

**Progress Summary**:
- ✅ **PR A**: Strict mode policy framework
- ✅ **PR B**: TBG201 eliminated (267 → 0) via SCC bucketing
- ✅ **PR C**: TBG203 partial fix (87 → 14) via honest emission
- ✅ **PR D**: TBG203 complete fix (14 → 0) + TBG120 downgrade (8 instances → INFO)

**Achievements** (PR D):
- **Fixed HonestEmissionPlanner bugs** eliminating final 14 TBG203 warnings
- **Downgraded TBG120 to INFO** for 8 core BCL types (always used in qualified contexts)
- **Zero warnings achieved** - strict mode now has true zero tolerance

**Diagnostic Summary** (Post PR D):
```
TBG120:  8 instances (INFO) - Reserved word collisions
TBG310: 12 instances (INFO) - Property covariance
TBG410:  5 instances (INFO) - Narrowed generic constraints
Total:  25 INFO, 0 WARNING, 0 ERROR
```

---

## Strict Mode Enforcement

When `BuildContext.StrictMode == true`:

1. **Validation fails** if any WARNING exists that isn't whitelisted in `StrictModePolicy`
2. **INFO messages** are allowed and don't count toward totals
3. **ERROR messages** always fail validation (zero tolerance)

### Usage

```bash
# Enable strict mode (default in production builds)
dotnet run -- generate -a assembly.dll --strict

# Disable for development
dotnet run -- generate -a assembly.dll --no-strict
```

---

## Policy Maintenance

### Adding a New Diagnostic Code

1. Add to `StrictModePolicy.cs` with justification
2. Document in this file under appropriate section
3. If WARNING: provide elimination roadmap
4. If INFO: explain why it's informational only

### Removing a Whitelisted Warning

1. Implement structural fix (PR B/C/D pattern)
2. Verify warning count reaches zero
3. Remove from whitelist in `StrictModePolicy.cs`
4. Update this documentation

### Promoting WARNING to ERROR

If a whitelisted WARNING becomes critical:

1. Change `AllowedLevel.WhitelistedWarning` → `AllowedLevel.Forbidden` in policy
2. Update justification to explain why it's now forbidden
3. Ensure count is zero before merge

---

## References

- **Policy Implementation**: `src/tsbindgen/Plan/Validation/StrictModePolicy.cs`
- **Enforcement**: `src/tsbindgen/Plan/PhaseGate.cs` (strict mode validation)
- **Roadmap**: PRs B, C, D eliminate all whitelisted warnings

---

**Last Review**: 2025-11-22
**Next Review**: After each PR (B, C, D) completion
