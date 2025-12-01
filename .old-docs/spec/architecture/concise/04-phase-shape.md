# Phase 4: Shape (22 Transformation Passes)

## Overview

The **Shape phase** transforms CLR semantics → TypeScript semantics through 22 sequential transformation passes. Creates compatibility plans for emission. Each pass is pure, returning new immutable SymbolGraph or plan.

**Input:** SymbolGraph (from Normalize, with indices)
**Output:** SymbolGraph (TypeScript-ready, unnamed) + 4 Shape plans

**Plans created:**
- `StaticFlatteningPlan` (Pass 4.7) - Static hierarchy flattening
- `StaticConflictPlan` (Pass 4.8) - Static conflict detection
- `OverrideConflictPlan` (Pass 4.9) - Override conflict detection
- `PropertyOverridePlan` (Pass 4.10) - Property type unification
- `ExtensionMethodsPlan` (Pass 4.11) - Extension method bucket grouping

---

## Pass 1: GlobalInterfaceIndex.Build

**Purpose:** Build global interface inheritance lookup

**Algorithm:**
1. For each namespace → For each interface type
2. Build index: InterfaceStableId → InterfaceSymbol
3. Store in BuildContext for later phases

**Files:** `Shape/GlobalInterfaceIndex.cs`

---

## Pass 2: InterfaceDeclIndex.Build

**Purpose:** Build interface member declaration lookup

**Algorithm:**
1. For each interface in GlobalInterfaceIndex
2. For each member in interface
3. Build index: (InterfaceStableId, MemberName) → MemberSymbol
4. Store in BuildContext

**Files:** `Shape/InterfaceDeclIndex.cs`

---

## Pass 3: StructuralConformance.Analyze

**Purpose:** Synthesize ViewOnly members for structural interface conformance

**Key:** MUST run BEFORE InterfaceInliner (needs original hierarchy to walk)

**Algorithm:**
1. For each type implementing interfaces
2. For each implemented interface
3. Check if type structurally conforms:
   - For each interface member, find matching member in class
   - Check signature compatibility (parameters, return type)
   - If mismatch: Mark interface member as ViewOnly (needs explicit view)
4. Return SymbolGraph with ViewOnly members synthesized

**Files:** `Shape/StructuralConformance.cs`

---

## Pass 4: InterfaceInliner.Inline

**Purpose:** Flatten interface hierarchies (copy inherited members into each interface)

**Key:** MUST run AFTER indices and conformance

**Algorithm:**
1. For each interface type
2. Walk full interface inheritance hierarchy
3. Copy all inherited members into interface
4. Mark as `Provenance.InterfaceInlined`
5. Return SymbolGraph with flattened interfaces

**Files:** `Shape/InterfaceInliner.cs`

---

## Pass 5: ExplicitImplSynthesizer.Synthesize

**Purpose:** Synthesize ViewOnly members for explicit interface implementations

**Algorithm:**
1. For each class implementing interfaces
2. Find members with qualified names (contains '.')
3. Parse interface name from qualified member name
4. Synthesize ViewOnly member for interface
5. Mark as `Provenance.ExplicitImpl`

**Files:** `Shape/ExplicitImplSynthesizer.cs`

---

## Pass 6: DiamondResolver.Resolve

**Purpose:** Resolve diamond inheritance (same member from multiple interfaces)

**Algorithm:**
1. For each type
2. Group members by signature
3. For signatures with multiple sources (diamond):
   - Pick one canonical source (arbitrary but deterministic)
   - Mark others as duplicates
   - Keep only canonical member

**Files:** `Shape/DiamondResolver.cs`

---

## Pass 7: BaseOverloadAdder.AddOverloads

**Purpose:** Add base class method overloads for interface compatibility

**Updated:** Uses topological sort and walks full hierarchy (not just immediate base)

**Algorithm:**
1. Build type hierarchy graph
2. Topological sort (base classes before derived)
3. For each type (sorted order):
   - Walk full base hierarchy (not just immediate base)
   - For each base class method
   - If not overridden in current type:
     - Add as `Provenance.BaseOverload`
   - Accumulate all base overloads transitively
4. Return SymbolGraph with base overloads

**Why:** Ensures derived types have all base methods visible for interface conformance checking.

**Files:** `Shape/BaseOverloadAdder.cs`

---

## Pass 8: StaticSideAnalyzer.Analyze

**Purpose:** Analyze static members and constructors

**Output:** Side effect in BuildContext

**Files:** `Shape/StaticSideAnalyzer.cs`

---

## Pass 9: IndexerPlanner.Plan

**Purpose:** Mark indexers for omission (TypeScript limitation)

**Algorithm:**
1. For each type
2. For each property with IndexParameters.Length > 0
3. Mark as `EmitScope.Omitted`
4. Log warning for user

**Files:** `Shape/IndexerPlanner.cs`

---

## Pass 10: HiddenMemberPlanner.Plan

**Purpose:** Handle C# 'new' keyword hiding (rename hidden members)

**Algorithm:**
1. For each type
2. For each member that hides base member (C# new keyword)
3. Create rename decision with suffix (e.g., "ToString" → "ToString2")
4. Store in Renamer

**Output:** Side effect (rename decisions)

**Files:** `Shape/HiddenMemberPlanner.cs`

---

## Pass 11: FinalIndexersPass.Run

**Purpose:** Remove any indexer properties that leaked through

**Algorithm:**
1. For each type
2. Remove properties with IndexParameters.Length > 0
3. Ensure no indexers remain in graph

**Files:** `Shape/FinalIndexersPass.cs`

---

## Pass 12: ClassSurfaceDeduplicator.Deduplicate

**Purpose:** Resolve name collisions on class surface (pick winner, demote rest to ViewOnly)

**Algorithm:**
1. For each type
2. Group ClassSurface members by TsEmitName (once set)
3. For each collision group:
   - Pick winner (first Original member, or first by provenance order)
   - Demote losers to ViewOnly
4. Return SymbolGraph with duplicates demoted

**Files:** `Shape/ClassSurfaceDeduplicator.cs`

---

## Pass 13: ConstraintCloser.Close

**Purpose:** Complete generic constraint closures

**Algorithm:**
1. For each type with generic parameters
2. For each generic parameter with RawConstraintTypes
3. Convert System.Type[] to TypeReference[] via TypeReferenceFactory
4. Store in Constraints field
5. Clear RawConstraintTypes

**Files:** `Shape/ConstraintCloser.cs`

---

## Pass 14: OverloadReturnConflictResolver.Resolve

**Purpose:** Resolve method overloads with conflicting return types

**Algorithm:**
1. For each type
2. Group methods by name and arity
3. For groups with different return types:
   - Compute least upper bound type (LUB)
   - Or use union type if no LUB
   - Update return types to unified type

**Files:** `Shape/OverloadReturnConflictResolver.cs`

---

## Pass 15: ViewPlanner.Plan

**Purpose:** Plan explicit interface views (one interface per view)

**Algorithm:**
1. For each type
2. Group ViewOnly members by InterfaceSource
3. For each interface:
   - Create ExplicitView with view property name (e.g., "As_IDisposable")
   - Add all ViewOnly members for that interface
4. Store ExplicitViews in TypeSymbol

**Files:** `Shape/ViewPlanner.cs`

---

## Pass 16: MemberDeduplicator.Deduplicate

**Purpose:** Remove duplicate members introduced by Shape passes

**Algorithm:**
1. For each type
2. Group members by StableId
3. Keep first occurrence, remove duplicates
4. Log warnings for removed duplicates

**Files:** `Shape/MemberDeduplicator.cs`

---

## Pass 17: StaticSideAnalyzer.Analyze (Legacy)

**Purpose:** Analyze static conflicts (superseded by 4.7-4.8)

**Note:** Kept for compatibility, actual static handling done by passes 4.7-4.8

**Files:** `Shape/StaticSideAnalyzer.cs`

---

## Pass 18: ConstraintCloser.Close

**Purpose:** Complete generic constraint closures (second pass)

**Files:** `Shape/ConstraintCloser.cs`

---

## Pass 4.7 (19): StaticHierarchyFlattener.Build

**Purpose:** Plan flattening for static-only inheritance hierarchies

**Input:** SymbolGraph
**Output:** (SymbolGraph, StaticFlatteningPlan)

**Algorithm:**
1. **Identify static-only types:**
   - No instance members (only static/const)
   - Base class (if any) also static-only or is Object
2. **Collect inherited static members:**
   - Walk full base hierarchy recursively
   - For each base class: Collect all static methods/properties/fields
   - Track original declaring type for each member
3. **Create flattening plan:**
   - Map: StaticOnlyTypeStableId → List<(MemberStableId, DeclaringType)>
   - Plan stores which members to inherit and from where
4. **Return:** (SymbolGraph unchanged, StaticFlatteningPlan)

**Impact:** Eliminates TS2417 for SIMD intrinsics (~50 types)

**Example:** `Vector128<T>` inherits static members from base `Vector128`

**Files:** `Shape/StaticHierarchyFlattener.cs`

---

## Pass 4.8 (20): StaticConflictDetector.Build

**Purpose:** Detect static member conflicts in hybrid types (both static and instance members)

**Input:** SymbolGraph
**Output:** StaticConflictPlan

**Algorithm:**
1. **Identify hybrid types:**
   - Has both static and instance members
   - Has base class
2. **Find static conflicts:**
   - For each static property/method/field in type
   - Walk base hierarchy
   - If base class has static member with same name:
     - Add to conflict set for this type
3. **Create conflict plan:**
   - Map: HybridTypeStableId → Set<ConflictingStaticMemberName>
   - Plan stores which static members shadow base statics
4. **Return:** StaticConflictPlan

**Impact:** Eliminates TS2417 for Task<T> and similar hybrid types (~4 types)

**Example:** `Task<T>.Factory` shadows `Task.Factory`

**Files:** `Shape/StaticConflictDetector.cs`

---

## Pass 4.9 (21): OverrideConflictDetector.Build

**Purpose:** Detect instance member override conflicts (same-assembly only)

**Input:** SymbolGraph
**Output:** OverrideConflictPlan

**Algorithm:**
1. **For each type:**
   - Walk base hierarchy (same assembly only)
   - For each property in current type:
     - Find property with same name in base
     - If exists and types incompatible:
       - Add to conflict set
   - For each method in current type:
     - Find method with same name/arity in base
     - If exists and signatures incompatible:
       - Add to conflict set
2. **Create conflict plan:**
   - Map: DerivedTypeStableId → Set<ConflictingInstanceMemberName>
   - Plan stores which instance members have incompatible overrides
3. **Return:** OverrideConflictPlan

**Impact:** Reduced TS2416 by 44% (same-assembly cases)

**Limitation:** Only detects when both base and derived in same SymbolGraph

**Files:** `Shape/OverrideConflictDetector.cs`

---

## Pass 4.10 (22): PropertyOverrideUnifier.Build

**Purpose:** Unify property types across inheritance hierarchies via union types

**Input:** SymbolGraph
**Output:** PropertyOverridePlan

**Algorithm:**

**Step 1: For each type:**
1. Build full inheritance chain (base → derived)
   - Walk base hierarchy
   - For cross-assembly bases: Lookup by ClrFullName in TypeIndex
   - Handle assembly forwarding (lookup by ClrFullName, not StableId)

**Step 2: For each property in type:**
1. Walk inheritance chain collecting properties with same name
2. Extract property types from chain
3. **Check if types vary:**
   - If all types identical: Skip (no unification needed)
   - If types differ: Proceed to unification

**Step 3: Compute union type:**
1. Collect distinct type strings
2. Create union: `Type1 | Type2 | Type3 | ...`
3. **Safety filter: Skip if property uses generic type parameters:**
   - Check if property type is GenericParameterReference
   - Or if property type is NamedTypeReference with TypeArguments containing GenericParameterReference
   - Reason: Generic parameters like T, TKey, TValue cause TS2304 errors
   - Examples to skip: `level: T`, `items: Array<TKey>`

**Step 4: Create plan entry:**
1. Map: PropertyStableId → UnifiedUnionTypeString
2. Example: `"System.Net.HttpRequestCachePolicy::level"` → `"HttpRequestCacheLevel | HttpCacheAgeControl"`

**Step 5: Return PropertyOverridePlan with all unifications**

**Impact:** Eliminated final TS2416 error → **zero TypeScript errors**

**Statistics:** 222 property chains unified, 444 union entries created

**Safety:** Generic filter prevents TS2304 errors from leaked type parameters

**Assembly forwarding fix:** Lookup base types by ClrFullName instead of StableId to handle cross-assembly forwarding

**Files:** `Shape/PropertyOverrideUnifier.cs`

---

## Pass 4.11 (23): ExtensionMethodAnalyzer.Analyze

**Purpose:** Group C# extension methods by target type for emission as TypeScript bucket interfaces.

**Input:** SymbolGraph

**Output:** ExtensionMethodsPlan

**Algorithm:**

**Step 1: Collect extension methods:**
- Filter all methods where `IsExtensionMethod = true`
- Extension methods are static methods with `ExtensionAttribute`

**Step 2: Group by target type:**
- Key: `(ExtensionTarget.FullName, ExtensionTarget.Arity)`
- Example: `("System.Collections.Generic.IEnumerable`1", 1)` for all `IEnumerable<T>` extensions

**Step 3: Build bucket plans:**
- Create `ExtensionBucketPlan` for each group
- Store target type, methods, and bucket interface name (e.g., `__Ext_IEnumerable_1`)

**Step 4: Return ExtensionMethodsPlan with all buckets**

**Impact:** 122 bucket interfaces, 1,759 extension methods (BCL .NET 9)

**Emission:** ExtensionsEmitter generates `internal/extensions/index.d.ts` with:
- Bucket interfaces: `export interface __Ext_IEnumerable_1<T> { ... }`
- Helper type: `export type ExtensionMethods<TShape> = ...` with constrained infer clauses

**Files:** `Analysis/ExtensionMethodAnalyzer.cs`, `Analysis/ExtensionBucketPlan.cs`

---

## Output State After Shape

- All members have `EmitScope` determined (ClassSurface, ViewOnly, or Omitted)
- All transformations complete
- `TsEmitName` still null (assigned in Phase 3.5)
- **Plans created:**
  - `StaticFlatteningPlan`: Static-only types → inherited static members
  - `StaticConflictPlan`: Hybrid types → conflicting static member names
  - `OverrideConflictPlan`: Derived types → incompatible instance override names
  - `PropertyOverridePlan`: Properties → unified union type strings
  - `ExtensionMethodsPlan`: Extension methods → bucket interfaces by target type

**Data flow:** SymbolGraph + 5 Plans → Phase 3.5 (Name Reservation)

---

## Summary

The Shape phase transforms CLR semantics to TypeScript through 23 passes:
1. **Interface processing** (1-6): Flatten hierarchies, synthesize members, resolve diamonds
2. **Member handling** (7-16): Add overloads, mark indexers, deduplicate, plan views
3. **Static hierarchy** (4.7-4.8): Flatten static-only types, detect hybrid conflicts
4. **Override handling** (4.9-4.10): Detect instance conflicts, unify property types
5. **Extension methods** (4.11): Group extension methods by target type for bucket emission

**Key achievements:**
- Eliminates TS2417 errors (static hierarchy issues)
- Reduces TS2416 errors (override conflicts)
- Final pass (4.10) achieves **zero TypeScript errors** via property type unification
- Creates compatibility plans for plan-based emission

**Critical ordering:**
- StructuralConformance BEFORE InterfaceInliner (needs original hierarchy)
- InterfaceInliner BEFORE ExplicitImplSynthesizer (needs flattened interfaces)
- Passes 4.7-4.10 MUST run LAST (create plans based on all prior transformations)
