# Phase 10: Call Graphs (Complete Call Chains)

## Overview

Complete call chains from CLI entry point through file emission. Shows who calls what, in execution order, with actual function names.

**Purpose:**
- Trace execution flow through entire pipeline
- Debug call paths
- Impact analysis (what breaks if I change this?)
- Guide new developers through codebase

---

## Entry Point Call Chain

```
User executes CLI command
  ↓
Program.Main(string[] args) → src/tsbindgen/Cli/Program.cs
  ↓
RootCommand.InvokeAsync(args)
  ↓
GenerateCommand.SetHandler → Cli/GenerateCommand.cs:114
  ↓
GenerateCommand.ExecuteAsync → Cli/GenerateCommand.cs:153
  ↓
Builder.Build(assemblyPaths, outDir, policy, logger, verbose, logCategories)
  Location: Builder.cs:27
  ↓
┌────────────────────────────┐
│ Five-Phase Pipeline Starts │
└────────────────────────────┘
  [Phase 1: Load]
  [Phase 2: Normalize]
  [Phase 3: Shape - 23 passes]
  [Phase 3.5: Name Reservation]
  [Phase 4: Plan]
  [Phase 4.5-4.7: Validation]
  [Phase 5: Emit]
```

---

## Phase 1: Load Call Graph

```
Builder.Build
  ↓
LoadPhase(ctx, assemblyPaths) → Builder.cs:118
  ↓
new AssemblyLoader(ctx) → Load/AssemblyLoader.cs:22
  ↓
AssemblyLoader.LoadClosure(seedPaths, refPaths, strictVersions) → AssemblyLoader.cs:113
  ↓
  ├─→ BuildCandidateMap(refPaths) → AssemblyLoader.cs:167
  │   Returns: Dictionary<AssemblyKey, List<string>>
  │   Scan reference directories for .dll files
  │
  ├─→ ResolveClosure(seedPaths, candidateMap, strictVersions) → AssemblyLoader.cs:207
  │   Returns: Dictionary<AssemblyKey, string>
  │   BFS traversal for transitive dependencies
  │   Uses PEReader to read assembly metadata without loading
  │
  ├─→ ValidateAssemblyIdentity(resolvedPaths, strictVersions) → AssemblyLoader.cs:295
  │   Check PG_LOAD_002 (mixed PKT), PG_LOAD_003 (version drift)
  │
  ├─→ FindCoreLibrary(resolvedPaths) → AssemblyLoader.cs:354
  │   Returns: Path to System.Private.CoreLib.dll
  │
  └─→ new MetadataLoadContext(resolver, "System.Private.CoreLib")
      Returns: MetadataLoadContext with all assemblies loaded
  ↓
new ReflectionReader(ctx) → Load/ReflectionReader.cs:14
  ↓
ReflectionReader.ReadAssemblies(loadContext, allAssemblyPaths) → ReflectionReader.cs:28
  ↓
  └─→ For each assembly → For each type:
      └─→ ReadType(type) → ReflectionReader.cs:102
          ↓
          ├─→ DetermineTypeKind(type) → ReflectionReader.cs:177
          │   Returns: TypeKind (Class, Interface, Enum, Delegate, Struct)
          │
          ├─→ ComputeAccessibility(type) → ReflectionReader.cs:154
          │   Returns: Accessibility (Public, Internal)
          │   Handles nested type accessibility
          │
          ├─→ TypeReferenceFactory.CreateGenericParameterSymbol(param)
          │   For each generic parameter
          │
          ├─→ TypeReferenceFactory.Create(type.BaseType)
          │   Create TypeReference for base type
          │
          ├─→ TypeReferenceFactory.Create(iface) for each interface
          │
          ├─→ ReadMembers(type) → ReflectionReader.cs:189
          │   ├─→ ReadMethod(method, type) → ReflectionReader.cs:264
          │   │   ├─→ CreateMethodSignature(method) → ReflectionReader.cs:465
          │   │   │   └─→ ctx.CanonicalizeMethod(name, paramTypes, returnType)
          │   │   │
          │   │   ├─→ Check ExtensionAttribute → Set IsExtensionMethod, ExtensionTarget
          │   │   │
          │   │   ├─→ ReadParameter(param) → ReflectionReader.cs:446
          │   │   │   ├─→ TypeScriptReservedWords.SanitizeParameterName(name)
          │   │   │   └─→ TypeReferenceFactory.Create(param.ParameterType)
          │   │   │
          │   │   ├─→ TypeReferenceFactory.Create(method.ReturnType)
          │   │   └─→ IsMethodOverride(method) → ReflectionReader.cs:524
          │   │
          │   ├─→ ReadProperty(property, type) → ReflectionReader.cs:312
          │   ├─→ ReadField(field, type) → ReflectionReader.cs:359
          │   ├─→ ReadEvent(evt, type) → ReflectionReader.cs:385
          │   └─→ ReadConstructor(ctor, type) → ReflectionReader.cs:426
          │
          └─→ For each nested type: ReadType(nestedType) (recursive)
  ↓
InterfaceMemberSubstitution.SubstituteClosedInterfaces(ctx, graph)
  Location: Load/InterfaceMemberSubstitutor.cs:20
  ↓
  ├─→ BuildInterfaceIndex(graph) → InterfaceMemberSubstitutor.cs:47
  │   Returns: Dictionary<string, TypeSymbol> of all interfaces
  │
  └─→ For each type:
      └─→ ProcessType(ctx, type, interfaceIndex) → InterfaceMemberSubstitutor.cs:65
          For each closed generic interface (e.g., IComparable<int>):
            └─→ BuildSubstitutionMap(ifaceSymbol, closedInterfaceRef)
                Creates: T → int for IComparable<int>
```

**Key Reflection APIs:**
- Assembly.GetTypes, Type.GetMethods, Type.GetProperties
- Type.GetFields, Type.GetEvents, Type.GetConstructors
- Type.GetInterfaces, MethodInfo.GetParameters

---

## Phase 2: Normalize Call Graph

```
Builder.Build
  ↓
graph = graph.WithIndices → Model/SymbolGraph.cs
  ↓
  Populates TypeIndex, NamespaceIndex for O(1) lookups
  ↓
┌──────────────────────────────────────────┐
│ [Library Mode Only - if --lib provided] │
└──────────────────────────────────────────┘
  ↓
LibraryContractLoader.Load(libraryPath) → Library/LibraryContractLoader.cs:23
  ↓
  ├─→ FindMetadataFiles(libraryPath)
  │   For each metadata.json:
  │     └─→ JsonSerializer.Deserialize<NamespaceMetadata>(json)
  │         Extract type StableIds
  │
  ├─→ FindBindingsFiles(libraryPath)
  │   For each bindings.json:
  │     └─→ JsonSerializer.Deserialize<NamespaceBindings>(json)
  │         Extract member StableIds
  │
  └─→ new LibraryContract
      Returns: Immutable contract with HashSet<string> of StableIds
  ↓
graph = LibraryFilter.Filter(graph, contract) → Library/LibraryFilter.cs:18
  ↓
  For each type:
    stableId = type.StableId.ToString()
    if contract.AllowedTypeStableIds.Contains(stableId):
      REMOVE type (base library type)
    else:
      KEEP type (user type)
  Returns: New SymbolGraph with only user types
```

**TypeIndex:** CLR full name → TypeSymbol
**NamespaceIndex:** Namespace name → NamespaceSymbol
**Library Mode:** Contract loading validates LIB001, filtering is O(1) per type

---

## Phase 3: Shape Call Graph

23 transformation passes:

```
Builder.Build
  ↓
ShapePhase(ctx, graph) → Builder.cs:165
  ↓
┌─────────────────────────────────────────┐
│ Pass 1: Global Interface Index (BEFORE) │
└─────────────────────────────────────────┘
  ↓
GlobalInterfaceIndex.Build(ctx, graph) → Shape/GlobalInterfaceIndex.cs:22
  For each interface:
    └─→ ComputeMethodSignatures(ctx, iface)
        └─→ ctx.CanonicalizeMethod(name, paramTypes, returnType)
    └─→ ComputePropertySignatures(ctx, iface)
        └─→ ctx.CanonicalizeProperty(name, indexParams, propType)
  Populates: _globalIndex[interfaceFullName] = InterfaceInfo
  ↓
InterfaceDeclIndex.Build(ctx, graph) → GlobalInterfaceIndex.cs:149
  For each interface:
    ├─→ CollectInheritedSignatures(iface) → GlobalInterfaceIndex.cs:244
    │   Walk base interfaces, collect inherited signatures
    └─→ Compute declared-only signatures (exclude inherited)
        Store: _declIndex[interfaceFullName] = DeclaredMembers
  ↓
┌──────────────────────────────────────────┐
│ Pass 2: Structural Conformance (ViewOnly)│
└──────────────────────────────────────────┘
  ↓
graph = StructuralConformance.Analyze(ctx, graph) → Shape/StructuralConformance.cs
  For each class/struct → For each implemented interface:
    If class method doesn't match interface signature:
      Create ViewOnly synthetic method
      Set: EmitScope = ViewOnly, SourceInterface = interface CLR name
           Provenance = MemberProvenance.InterfaceView
  Returns: New SymbolGraph with ViewOnly members added
  ↓
┌────────────────────────────────────────────┐
│ Pass 3: Interface Inlining (flatten)      │
└────────────────────────────────────────────┘
  ↓
graph = InterfaceInliner.Inline(ctx, graph) → Shape/InterfaceInliner.cs
  For each class/struct → For each interface (including base interfaces):
    For each interface method:
      If class doesn't have matching signature:
        Create new method symbol
        Set: EmitScope = ClassSurface, Provenance = InterfaceInlining
             SourceInterface = interface CLR name
  Returns: New SymbolGraph with inlined interface members
  ↓
┌────────────────────────────────────────────────┐
│ Pass 4: Explicit Interface Implementation     │
└────────────────────────────────────────────────┘
  ↓
graph = ExplicitImplSynthesizer.Synthesize(ctx, graph) → Shape/ExplicitImplSynthesizer.cs
  For each method/property with name containing '.':
    Parse qualified name (e.g., "System.IDisposable.Dispose")
    Create synthetic ViewOnly member
    Set: EmitScope = ViewOnly, Provenance = ExplicitInterfaceImpl
  Returns: New SymbolGraph with explicit impl members tagged
  ↓
┌──────────────────────────────────────────┐
│ Pass 5: Diamond Inheritance Resolution   │
└──────────────────────────────────────────┘
  ↓
graph = DiamondResolver.Resolve(ctx, graph) → Shape/DiamondResolver.cs
  For each interface with diamond inheritance:
    Pick single implementation for ambiguous members
    Emit PG_INT_005 diagnostic if conflict
  Returns: New SymbolGraph with diamond conflicts resolved
  ↓
┌──────────────────────────────────────────┐
│ Pass 6: Base Overload Addition          │
└──────────────────────────────────────────┘
  ↓
graph = BaseOverloadAdder.AddOverloads(ctx, graph) → Shape/BaseOverloadAdder.cs
  For each class → Walk inheritance chain:
    For each base class method:
      Add overload on derived class if needed for TypeScript
      Set: Provenance = MemberProvenance.BaseOverload
  Returns: New SymbolGraph with base overloads added
  ↓
┌──────────────────────────────────────────┐
│ Pass 7: Static-Side Analysis            │
└──────────────────────────────────────────┘
  ↓
StaticSideAnalyzer.Analyze(ctx, graph) → Shape/StaticSideAnalyzer.cs
  For each type:
    Check for static/instance name collisions
    Emit PG_NAME_002 diagnostic if collision
  Mutates: ctx.Diagnostics (analysis only)
  ↓
┌──────────────────────────────────────────┐
│ Pass 8: Indexer Planning                │
└──────────────────────────────────────────┘
  ↓
graph = IndexerPlanner.Plan(ctx, graph) → Shape/IndexerPlanner.cs
  For each property with IndexParameters.Count > 0:
    Set EmitScope = Omit
    Reserve name through ctx.Renamer
    Track in metadata.json
  Returns: New SymbolGraph with indexers omitted
  ↓
┌──────────────────────────────────────────┐
│ Pass 9: Hidden Member Planning (C# 'new')│
└──────────────────────────────────────────┘
  ↓
HiddenMemberPlanner.Plan(ctx, graph) → Shape/HiddenMemberPlanner.cs
  For each class → For each member that hides base member:
    Reserve renamed name through ctx.Renamer
    Add disambiguation suffix
  Mutates: ctx.Renamer
  ↓
┌──────────────────────────────────────────┐
│ Pass 10: Final Indexers Pass            │
└──────────────────────────────────────────┘
  ↓
graph = FinalIndexersPass.Run(ctx, graph) → Shape/FinalIndexersPass.cs
  For each property:
    Verify no indexers have EmitScope = ClassSurface
    If found: Set EmitScope = Omit, Emit PG_EMIT_001
  Returns: New SymbolGraph with indexer leaks fixed
  ↓
┌──────────────────────────────────────────────┐
│ Pass 10.5: Class Surface Deduplication (M5) │
└──────────────────────────────────────────────┘
  ↓
graph = ClassSurfaceDeduplicator.Deduplicate(ctx, graph) → Shape/ClassSurfaceDeduplicator.cs
  For each type → Group ClassSurface members by TsEmitName:
    If duplicates found:
      Pick winner (prefer Original over Synthesized)
      Demote losers to Omit
      Reserve winner name in ctx.Renamer
      Emit PG_DEDUP_001 diagnostic
  Returns: New SymbolGraph with duplicates removed
  ↓
┌──────────────────────────────────────────┐
│ Pass 11: Constraint Closure              │
└──────────────────────────────────────────┘
  ↓
graph = ConstraintCloser.Close(ctx, graph) → Shape/ConstraintCloser.cs
  For each generic type/method:
    Compute transitive closure of constraints
    Example: T : IComparable<U>, U : IList<V> → T : IList<V>
  Returns: New SymbolGraph with complete constraint sets
  ↓
┌──────────────────────────────────────────┐
│ Pass 12: Return-Type Conflict Resolution │
└──────────────────────────────────────────┘
  ↓
graph = OverloadReturnConflictResolver.Resolve(ctx, graph) → Shape/OverloadReturnConflictResolver.cs
  For each method group with same name:
    Check for different return types with compatible signatures
    If conflict: Add disambiguation suffix, Emit PG_OVERLOAD_001
  Returns: New SymbolGraph with return type conflicts resolved
  ↓
┌──────────────────────────────────────────┐
│ Pass 13: View Planning (explicit views)  │
└──────────────────────────────────────────┘
  ↓
graph = ViewPlanner.Plan(ctx, graph) → Shape/ViewPlanner.cs
  For each class/struct → For each ViewOnly member:
    Verify SourceInterface is set
    Check if member should be in view
    Keep EmitScope = ViewOnly or set to Omit
  Returns: New SymbolGraph with view membership finalized
  ↓
┌──────────────────────────────────────────┐
│ Pass 14: Final Member Deduplication      │
└──────────────────────────────────────────┘
  ↓
graph = MemberDeduplicator.Deduplicate(ctx, graph) → Shape/MemberDeduplicator.cs
  For each type → Group members by StableId:
    If duplicates: Keep first, remove rest, Emit PG_DEDUP_002
  Returns: New SymbolGraph with all duplicates removed
  ↓
┌──────────────────────────────────────────┐
│ Pass 15: Static-Side Analysis (Legacy)   │
└──────────────────────────────────────────┘
  ↓
StaticSideAnalyzer.Analyze(ctx, graph)
  (Superseded by passes 4.7-4.8 for actual static handling)
  ↓
┌──────────────────────────────────────────┐
│ Pass 16: Generic Constraint Closure      │
└──────────────────────────────────────────┘
  ↓
graph = ConstraintCloser.Close(ctx, graph)
  For each type's generic parameters:
    Close constraint relationships
  Returns: New SymbolGraph with constraints closed
  ↓
┌──────────────────────────────────────────────┐
│ Pass 4.7 (17): Static Hierarchy Flattening  │
└──────────────────────────────────────────────┘
  ↓
(graph, staticFlattening) = StaticHierarchyFlattener.Build(ctx, graph)
  Location: Shape/StaticHierarchyFlattener.cs
  ↓
  IdentifyStaticOnlyTypes(graph):
    For each type with ALL static members (no instance):
      Check if has base type (exclude Object, ValueType)
      If static-only:
        CollectInheritedStaticMembers(graph, type)
          Walk base chain recursively
          Collect ALL static members (methods, properties, fields)
        Store: staticFlattening.FlattenedTypes[type.StableId] = InheritedStaticMembers
  Returns: (graph unchanged, StaticFlatteningPlan)
  Impact: ~50 SIMD intrinsic types (Sse, Avx, etc.)
  ↓
┌──────────────────────────────────────────────┐
│ Pass 4.8 (18): Static Conflict Detection    │
└──────────────────────────────────────────────┘
  ↓
staticConflicts = StaticConflictDetector.Build(ctx, graph)
  Location: Shape/StaticConflictDetector.cs
  ↓
  Filter hybrid types (both static AND instance members):
    For each hybrid type with base class:
      Find base class in graph
      Collect static members from derived and base
      For each derived static member:
        If base has static with same name (incompatible):
          Mark for suppression
          Store: staticConflicts.Suppressions[(type.StableId, member.StableId)]
  Returns: StaticConflictPlan
  Impact: ~4 types (Task_1, CallSite_1, etc.)
  ↓
┌──────────────────────────────────────────────┐
│ Pass 4.9 (19): Override Conflict Detection  │
└──────────────────────────────────────────────┘
  ↓
overrideConflicts = OverrideConflictDetector.Build(ctx, graph)
  Location: Shape/OverrideConflictDetector.cs
  ↓
  Filter types with base classes:
    For each type:
      Find base class in graph (skip if external)
      Collect instance methods and properties
      For each derived instance member:
        Find matching base member by name
        Compare return types and parameters
        If incompatible (different return type, parameters):
          Mark for suppression
          Store: overrideConflicts.Suppressions[(type.StableId, member.StableId)]
  Returns: OverrideConflictPlan
  Impact: Reduced TS2416 errors by 44% (same-assembly cases)
  Limitation: Only detects conflicts within same SymbolGraph
  ↓
┌──────────────────────────────────────────────────┐
│ Pass 4.10 (20): Property Override Unification   │
└──────────────────────────────────────────────────┘
  ↓
propertyOverrides = PropertyOverrideUnifier.Build(graph, ctx)
  Location: Shape/PropertyOverrideUnifier.cs
  ↓
  Find all types with base classes:
    For each type:
      WalkHierarchy(type, graph):
        Recursively walk BaseType references
        ResolveBase(type, graph):
          Lookup by ClrFullName (handles assembly forwarding):
            graph.TypeIndex.Values
              .FirstOrDefault(t => t.ClrFullName == named.FullName)
        Track depth (root = 0, derived = 1, etc.)
        Return top-down list (root first, derived last)
      ↓
      GroupPropertiesByName(hierarchy, ctx):
        For each type in hierarchy:
          Collect all properties
          Group by CLR name
        Returns: Dictionary<string, List<(Type, Property)>>
      ↓
      For each property group:
        UnifyPropertyGroup(group, graph, ctx, plan):
          Collect unique TS type strings
          If all types same → skip (no variance)
          ↓
          SAFETY FILTER - Generic type parameters:
            if (Regex.IsMatch(tsType, @"\b(T|E|K|V|TKey|TValue|...)\b"))
              return;  // Skip unification (prevents TS2304)
          ↓
          Create union: "type1 | type2 | ..."
          Store for ALL properties in group:
            plan.PropertyTypeOverrides[(type.StableId, prop.StableId)] = unionType
  Returns: PropertyOverridePlan
  Impact: Eliminated final TS2416 error → **ZERO TypeScript errors**
  Statistics: 222 property chains unified, 444 union entries created
  ↓
┌──────────────────────────────────────────────────────┐
│ Pass 4.11 (23): Extension Method Analysis          │
└──────────────────────────────────────────────────────┘
  ↓
extensionMethods = ExtensionMethodAnalyzer.Analyze(ctx, graph)
  Location: Analysis/ExtensionMethodAnalyzer.cs
  ↓
  Collect all extension methods (IsExtensionMethod = true)
  Group by ExtensionTarget → (FullName, Arity) key
  Build bucket plans with target type and methods
  ↓
  Returns: ExtensionMethodsPlan
  Impact: 122 bucket interfaces, 1,759 extension methods (BCL .NET 9)
```

**Key Observations:**
- Passes 1-16: PURE (each returns new SymbolGraph)
- Passes 4.7-4.11: Return PLANS (planning data, not graph transformations)
- Renamer: MUTATED (not pure, accumulates rename decisions)
- Plans passed through EmissionPlan to Emit phase

---

## Phase 3.5: Name Reservation Call Graph

Central naming phase - reserves all TypeScript names:

```
Builder.Build
  ↓
graph = NameReservation.ReserveAllNames(ctx, graph)
  Location: Normalize/NameReservation.cs:32
  ↓
┌────────────────────────────────────────────┐
│ Step 1: Reserve Type Names                │
└────────────────────────────────────────────┘
  ↓
  For each namespace → For each type:
    Shared.ComputeTypeRequestedBase(type.ClrName)
      Location: Normalize/Naming/Shared.cs
      Transforms: Remove backtick+arity, Replace + with _, Sanitize reserved words
    ↓
    ctx.Renamer.ReserveTypeName(stableId, requested, scope, context, source)
      Location: Renaming/SymbolRenamer.cs
      Check if available in scope
      If conflict: Add disambiguation suffix
      Store: _typeDecisions[stableId][scope] = RenameDecision
  ↓
┌────────────────────────────────────────────┐
│ Step 2: Reserve Class Surface Member Names│
└────────────────────────────────────────────┘
  ↓
  Reservation.ReserveMemberNamesOnly(ctx, type)
    Location: Normalize/Naming/Reservation.cs
    ↓
    For each member where EmitScope == ClassSurface:
      If member already has rename decision: Skip
      Otherwise:
        Shared.ComputeMemberRequestedBase(member.ClrName)
          Transforms: Sanitize reserved words, Apply camelCase if policy enabled
        ↓
        ScopeFactory.ClassSurface(type, member.IsStatic)
          Location: Normalize/Naming/ScopeFactory.cs
          Creates: namespace/internal/TypeName/instance or static
        ↓
        ctx.Renamer.ReserveMemberName(stableId, requested, scope, context, source)
          Check static vs instance scope collision
          If conflict: Add disambiguation suffix
          Store: _memberDecisions[stableId][scope] = RenameDecision
  ↓
┌────────────────────────────────────────────┐
│ Step 3: Build Class Surface Name Sets     │
└────────────────────────────────────────────┘
  ↓
  For each member where EmitScope == ClassSurface:
    methodScope = ScopeFactory.ClassSurface(type, method.IsStatic)
    ctx.Renamer.TryGetDecision(stableId, methodScope, out decision)
    Add decision.Final to classInstanceNames or classStaticNames
    Union into classAllNames set
  ↓
  Purpose: Track names used on class surface for view-vs-class collision detection
  ↓
┌────────────────────────────────────────────┐
│ Step 4: Reserve View-Scoped Member Names  │
└────────────────────────────────────────────┘
  ↓
  Reservation.ReserveViewMemberNamesOnly(ctx, graph, type, classAllNames)
    Location: Naming/Reservation.cs
    ↓
    For each member where EmitScope == ViewOnly:
      If member.SourceInterface is null: Error
      Otherwise:
        Shared.ComputeMemberRequestedBase(member.ClrName)
        ↓
        ScopeFactory.ViewScope(type, sourceInterface, member.IsStatic)
          Creates: namespace/internal/TypeName/view/InterfaceName/instance or static
          DIFFERENT scope from class surface!
        ↓
        Check collision with classAllNames
        If collision: Add disambiguation suffix, Emit PG_NAME_003/004
        ↓
        ctx.Renamer.ReserveMemberName(stableId, requested, viewScope, context, source)
          Store: _memberDecisions[stableId][viewScope] = RenameDecision
          Note: Same member can have DIFFERENT names in class scope vs view scope
  ↓
┌────────────────────────────────────────────┐
│ Step 5: Post-Reservation Audit (fail fast)│
└────────────────────────────────────────────┘
  ↓
  Audit.AuditReservationCompleteness(ctx, graph)
    Location: Normalize/Naming/Audit.cs
    ↓
    For each type → For each member where EmitScope != Omit:
      Compute expected scope
      ctx.Renamer.TryGetDecision(stableId, scope, out _)
      If NOT found: Error (fatal bug - name reservation incomplete)
  ↓
┌────────────────────────────────────────────┐
│ Step 6: Apply Names to Graph (pure)       │
└────────────────────────────────────────────┘
  ↓
  updatedGraph = Application.ApplyNamesToGraph(ctx, graph)
    Location: Normalize/Naming/Application.cs
    ↓
    For each type:
      ctx.Renamer.GetFinalTypeName(stableId, namespaceScope)
      Set: type.TsEmitName
      ↓
      For each member:
        Compute correct scope (ClassSurface or ViewScope)
        ctx.Renamer.GetFinalMemberName(stableId, scope)
        Set: member.TsEmitName
    Returns: New SymbolGraph with all TsEmitName fields populated
  ↓
  All subsequent phases use TsEmitName for output
```

**Critical Invariants Established:**
1. Every emitted type has TsEmitName set
2. Every emitted member (ClassSurface or ViewOnly) has TsEmitName set
3. Every TsEmitName has corresponding RenameDecision in Renamer
4. View members can have different TsEmitName from class members

---

## Phase 4: Plan Call Graph

```
Builder.Build
  ↓
plan = PlanPhase(ctx, graph) → Builder.cs:250
  ↓
┌────────────────────────────────────────────┐
│ Step 1: Build Import Graph                │
└────────────────────────────────────────────┘
  ↓
importGraph = ImportGraph.Build(ctx, graph) → Plan/ImportGraph.cs
  For each namespace → For each type → For each member signature:
    Extract foreign type references (Types from different namespaces)
    Record: SourceNamespace → ForeignNamespace → TypeNames
  Returns: ImportGraph with cross-namespace dependencies
  ↓
┌────────────────────────────────────────────┐
│ Step 2: Plan Imports and Aliases          │
└────────────────────────────────────────────┘
  ↓
imports = ImportPlanner.PlanImports(ctx, graph, importGraph) → Plan/ImportPlanner.cs
  For each namespace:
    Collect all foreign types needed
    Group by source namespace
    For each imported type:
      Check for name collision with local types
      If collision: Create alias (TypeName → TypeName_fromNamespace)
    Build ImportPlan with: Import statements, Alias mappings, Used types per namespace
  Returns: ImportPlan
  ↓
┌────────────────────────────────────────────┐
│ Step 3: Plan Emission Order               │
└────────────────────────────────────────────┘
  ↓
orderPlanner = new EmitOrderPlanner(ctx)
order = orderPlanner.PlanOrder(graph) → Plan/EmitOrderPlanner.cs
  Build dependency graph between namespaces
  Perform topological sort
  Within each namespace, order types:
    1. Interfaces, 2. Base classes, 3. Derived classes, 4. Structs, 5. Enums, 6. Delegates
  Returns: EmitOrder with stable, deterministic order
  ↓
┌────────────────────────────────────────────┐
│ Phase 4.5: Overload Unification           │
└────────────────────────────────────────────┘
  ↓
graph = OverloadUnifier.UnifyOverloads(ctx, graph) → Normalize/OverloadUnifier.cs
  For each type → Group methods by TsEmitName:
    If multiple overloads:
      Sort by parameter count (ascending)
      Mark last as UnifiedImplementation (umbrella signature)
      Mark others as UnifiedDeclaration (declaration-only)
  Returns: New SymbolGraph with overload roles assigned
  ↓
┌────────────────────────────────────────────────┐
│ Phase 4.6: Interface Constraint Audit         │
└────────────────────────────────────────────────┘
  ↓
constraintFindings = InterfaceConstraintAuditor.Audit(ctx, graph)
  Location: Plan/InterfaceConstraintAuditor.cs
  For each (Type, Interface) pair:
    Check constructor constraints
    If interface requires `new` but type has no public constructor: PG_CONSTRAINT_001
    If interface requires specific base type but type doesn't inherit: PG_CONSTRAINT_002
  Returns: InterfaceConstraintFindings
  ↓
┌────────────────────────────────────────────┐
│ Phase 4.7: PhaseGate Validation (50+ checks)│
└────────────────────────────────────────────┘
  ↓
PhaseGate.Validate(ctx, graph, imports, constraintFindings, libraryContract)
  Location: Plan/PhaseGate.cs:24
  [See Section 8 for complete PhaseGate call graph]
  Returns: void (emits diagnostics to ctx.Diagnostics)
  Validates library mode LIB002 (dangling references) if --lib flag
  Throws: If validation errors exceed threshold
  If no errors: Continue to Emit phase
  If errors: Build fails
```

**EmissionPlan Structure:**
```csharp
record EmissionPlan
{
    SymbolGraph Graph;              // Fully validated graph
    ImportPlan Imports;             // Import statements per namespace
    EmitOrder EmissionOrder;        // Stable ordering for emission
    StaticFlatteningPlan;           // Pass 4.7 plan
    StaticConflictPlan;             // Pass 4.8 plan
    OverrideConflictPlan;           // Pass 4.9 plan
    PropertyOverridePlan;           // Pass 4.10 plan
    ExtensionMethodsPlan;           // Pass 4.11 plan
}
```

---

## Phase 4.7: PhaseGate Validation Call Graph

Comprehensive pre-emission validation (50+ functions, 19 modules):

```
PhaseGate.Validate(ctx, graph, imports, constraintFindings, libraryContract) → Plan/PhaseGate.cs:24
  ↓
validationContext = new ValidationContext
  Tracks: ErrorCount, WarningCount, Diagnostics, DiagnosticCountsByCode
  ↓
┌────────────────────────────────────────────┐
│ Core Validations (8 functions)            │
└────────────────────────────────────────────┘
  ↓
ValidationCore.ValidateTypeNames(ctx, graph, validationContext) → Plan/Validation/Core.cs
  For each type:
    Check type.TsEmitName is set → If null: PG_NAME_001
    Check TsEmitName is valid TypeScript identifier → If invalid: PG_IDENT_001
  ↓
ValidationCore.ValidateMemberNames(ctx, graph, validationContext)
  For each member where EmitScope != Omit:
    Check member.TsEmitName is set → If null: PG_NAME_001
    Check TsEmitName is valid identifier → If invalid: PG_IDENT_001
  ↓
ValidationCore.ValidateGenericParameters(ctx, graph, validationContext)
  For each generic type/method:
    Check generic parameter names valid
    Check constraint types exist → If missing: PG_GEN_001
  ↓
ValidationCore.ValidateInterfaceConformance(ctx, graph, validationContext)
  For each class implementing interfaces:
    Check all interface members have implementations
    Either: ClassSurface member OR ViewOnly member → If missing: PG_INT_001
  ↓
ValidationCore.ValidateInheritance(ctx, graph, validationContext)
  For each derived class:
    Check base type exists
    Check override members match base signatures → If mismatch: PG_INH_001
  ↓
ValidationCore.ValidateEmitScopes(ctx, graph, validationContext)
  For each member:
    Check EmitScope is valid (ClassSurface, ViewOnly, or Omit)
    Check EmitScope matches member characteristics → If invalid: PG_SCOPE_001
  ↓
ValidationCore.ValidateImports(ctx, graph, imports, validationContext)
  For each import statement:
    Check imported namespace exists
    Check imported types exist → If missing: PG_IMPORT_002
  ↓
ValidationCore.ValidatePolicyCompliance(ctx, graph, validationContext)
  Check all policy rules followed:
    Unsafe markers present where required
    Name transforms applied correctly
    Omissions tracked in metadata → If violation: PG_POLICY_001
  ↓
┌────────────────────────────────────────────┐
│ M1: Identifier Sanitization (Names)       │
└────────────────────────────────────────────┘
  ↓
Names.ValidateIdentifiers(ctx, graph, validationContext) → Plan/Validation/Names.cs
  For each type and member:
    Check TsEmitName doesn't contain TypeScript reserved words
    Check no special characters (except _, $) → If invalid: PG_IDENT_002
  ↓
┌────────────────────────────────────────────┐
│ M2: Overload Collision Detection (Names)  │
└────────────────────────────────────────────┘
  ↓
Names.ValidateOverloadCollisions(ctx, graph, validationContext)
  For each method group with same TsEmitName:
    Check overload signatures compatible
    Check return types follow TypeScript rules → If conflict: PG_OVERLOAD_002
  ↓
┌────────────────────────────────────────────┐
│ M3: View Integrity (Views - 3 rules)      │
└────────────────────────────────────────────┘
  ↓
Views.ValidateIntegrity(ctx, graph, validationContext) → Plan/Validation/Views.cs
  Rule 1: ViewOnly members MUST have SourceInterface
    For each ViewOnly member:
      Check member.SourceInterface != null → If null: PG_VIEW_001 (FATAL)
  ↓
  Rule 2: ViewOnly members MUST have ClassSurface twin with same StableId
    For each ViewOnly member:
      Find ClassSurface member with matching StableId → If not found: PG_VIEW_002 (FATAL)
  ↓
  Rule 3: ClassSurface-ViewOnly pairs MUST have same CLR signature
    For each pair:
      Compare canonical signatures → If mismatch: PG_VIEW_003 (FATAL)
  ↓
┌────────────────────────────────────────────┐
│ M4: Constraint Findings (Constraints)     │
└────────────────────────────────────────────┘
  ↓
Constraints.EmitDiagnostics(ctx, constraintFindings, validationContext)
  Location: Plan/Validation/Constraints.cs
  For each finding:
    Emit PG_CONSTRAINT_001 (missing constructor)
    Emit PG_CONSTRAINT_002 (missing base type)
  ↓
┌────────────────────────────────────────────────┐
│ M5: View Member Scoping (Views - PG_NAME_003/004)│
└────────────────────────────────────────────────┘
  ↓
Views.ValidateMemberScoping(ctx, graph, validationContext)
  For each type:
    Build classAllNames set (instance + static)
    For each ViewOnly member:
      Get view scope: ScopeFactory.ViewScope(type, sourceInterface, isStatic)
      ctx.Renamer.GetFinalMemberName(stableId, viewScope)
      Check collision with classAllNames
        If collision: PG_NAME_003 (static) or PG_NAME_004 (instance)
      Check view name != class name for same StableId → If same: PG_NAME_005
  ↓
┌────────────────────────────────────────────────┐
│ M5: EmitScope Invariants (Scopes - PG_INT_002/003)│
└────────────────────────────────────────────────┘
  ↓
Scopes.ValidateEmitScopeInvariants(ctx, graph, validationContext) → Plan/Validation/Scopes.cs
  For each type → For each member:
    Check EmitScope ∈ {ClassSurface, ViewOnly, Omit} → If invalid: PG_SCOPE_001
    If EmitScope == ViewOnly: Check SourceInterface is set → If null: PG_INT_002 (FATAL)
    If EmitScope == ClassSurface: Check SourceInterface is null → If set: PG_INT_003 (FATAL)
  ↓
┌────────────────────────────────────────────────┐
│ M5: Scope Mismatches (Scopes - PG_SCOPE_003/004)│
└────────────────────────────────────────────────┘
  ↓
Scopes.ValidateScopeMismatches(ctx, graph, validationContext)
  For each member where EmitScope != Omit:
    Compute expected scope from EmitScope + IsStatic + SourceInterface
    ctx.Renamer.TryGetDecision(stableId, expectedScope, out _)
    If NOT found:
      Check if decision exists in wrong scope
        If in class scope but member is ViewOnly: PG_SCOPE_003
        If in view scope but member is ClassSurface: PG_SCOPE_004
      If not found anywhere: PG_NAME_001 (FATAL)
  ↓
┌────────────────────────────────────────────────────┐
│ M5: Class Surface Uniqueness (Names - PG_NAME_005)│
└────────────────────────────────────────────────────┘
  ↓
Names.ValidateClassSurfaceUniqueness(ctx, graph, validationContext)
  For each type → Group ClassSurface members by (TsEmitName, IsStatic):
    If duplicates: PG_NAME_005 (FATAL) - ClassSurfaceDeduplicator failed
  ↓
┌────────────────────────────────────────────────────┐
│ M6: Finalization Sweep (PG_FIN_001 through PG_FIN_009)│
└────────────────────────────────────────────────────┘
  ↓
Finalization.Validate(ctx, graph, validationContext) → Plan/Validation/Finalization.cs
  For each type:
    Check TsEmitName is set: PG_FIN_001
    Check Accessibility is set: PG_FIN_002
    Check Kind is valid: PG_FIN_003
    For each member where EmitScope != Omit:
      Check TsEmitName is set: PG_FIN_004
      Check EmitScope is valid: PG_FIN_005
      Check Provenance is set: PG_FIN_006
      If ViewOnly: Check SourceInterface is set: PG_FIN_007
      Check return type exists: PG_FIN_008
      Check all parameter types exist: PG_FIN_009
  ↓
┌────────────────────────────────────────────────────┐
│ M7: Printer Name Consistency (Types - PG_PRINT_001)│
└────────────────────────────────────────────────────┘
  ↓
Types.ValidatePrinterNameConsistency(ctx, graph, validationContext) → Plan/Validation/Types.cs
  For each type → For each member signature → For each TypeReference:
    Simulate TypeRefPrinter.Print(typeRef, scope)
    TypeNameResolver.ResolveTypeName(typeRef, scope, ctx.Renamer)
    ctx.Renamer.GetFinalTypeName(stableId, scope)
    Check result is not null and valid identifier → If invalid: PG_PRINT_001
  Purpose: Validate TypeRefPrinter→Renamer chain works correctly
  ↓
┌────────────────────────────────────────────────────┐
│ M7a: TypeMap Compliance (Types - PG_TYPEMAP_001)  │
└────────────────────────────────────────────────────┘
  ↓
Types.ValidateTypeMapCompliance(ctx, graph, validationContext)
  For each type → For each member signature → For each TypeReference:
    Check TypeReference kind
    If PointerTypeReference: PG_TYPEMAP_001 (UNSUPPORTED)
    If ByRefTypeReference (not param): PG_TYPEMAP_001 (UNSUPPORTED)
    If FunctionPointerReference: PG_TYPEMAP_001 (UNSUPPORTED)
  Purpose: Detect unsupported CLR types early
  MUST RUN EARLY - before other type validation
  ↓
┌────────────────────────────────────────────────────────┐
│ M7b: External Type Resolution (Types - PG_LOAD_001)   │
└────────────────────────────────────────────────────────┘
  ↓
Types.ValidateExternalTypeResolution(ctx, graph, validationContext)
  For each type → For each member signature → For each foreign NamedTypeReference:
    Check if type exists in graph.TypeIndex
    If NOT found: Check if built-in (System.Object, etc.)
    If NOT built-in: PG_LOAD_001 "External type reference not in closure"
  MUST RUN AFTER TypeMap, BEFORE API surface validation
  ↓
┌────────────────────────────────────────────────────────┐
│ M8: Public API Surface (ImportExport - PG_API_001/002)│
└────────────────────────────────────────────────────────┘
  ↓
ImportExport.ValidatePublicApiSurface(ctx, graph, imports, validationContext)
  Location: Plan/Validation/ImportExport.cs
  For each public type → For each public member → For each TypeReference:
    Check if referenced type is emitted
    If type has Accessibility = Internal: PG_API_001
    If type has EmitScope = Omit: PG_API_002
  MUST RUN BEFORE PG_IMPORT_001 - it's more fundamental
  ↓
┌────────────────────────────────────────────────────────┐
│ M9: Import Completeness (ImportExport - PG_IMPORT_001)│
└────────────────────────────────────────────────────────┘
  ↓
ImportExport.ValidateImportCompleteness(ctx, graph, imports, validationContext)
  For each namespace:
    Collect all foreign types used in signatures
    For each foreign type:
      Check if imports.HasImport(foreignNamespace, typeName)
      If NOT found: PG_IMPORT_001 "Missing import for foreign type"
  ↓
┌────────────────────────────────────────────────────────┐
│ M10: Export Completeness (ImportExport - PG_EXPORT_001)│
└────────────────────────────────────────────────────────┘
  ↓
ImportExport.ValidateExportCompleteness(ctx, graph, imports, validationContext)
  For each namespace → For each import statement:
    Check if source namespace actually exports the type
    If NOT exported: PG_EXPORT_001 "Imported type not exported by source"
  ↓
┌────────────────────────────────────────────────────────┐
│ M19: Library Mode Validation (LibraryMode - LIB002)   │
└────────────────────────────────────────────────────────┘
  ↓
LibraryMode.Validate(ctx, graph, contract, validationContext) → Plan/Validation/LibraryMode.cs
  ↓
  If NOT in library mode: Skip validation
  If in library mode:
    For each namespace → For each type:
      └─→ CheckTypeReference(type, type.BaseType, contract, ctx, validationContext)
          └─→ If reference.StableId in contract AND type not in graph: LIB002
      └─→ For each interface in type.Interfaces:
          CheckTypeReference(type, interface, contract, ctx, validationContext)
      └─→ For each member (method/property/field/event):
          CheckTypeReference recursively for all type references
          (parameters, return types, property types, field types, event types, generics)
  LIB001 validated during contract loading (Phase 2)
  LIB003 disabled in library mode (binding consistency checked separately)
  ↓
┌────────────────────────────────────────────┐
│ Final: Report Results                     │
└────────────────────────────────────────────┘
  ↓
  Print diagnostic summary table:
    Group by diagnostic code, Sort by count (descending)
    Show: Code, Count, Description
  ↓
  If ErrorCount > 0:
    ctx.Diagnostics.Error(DiagnosticCodes.ValidationFailed, ...)
    Show first 20 errors in message
    Build fails
  ↓
  Context.WriteDiagnosticsFile(ctx, validationContext) → Plan/Validation/Context.cs
    Write to: .tests/phasegate-diagnostics.txt
  ↓
  Context.WriteSummaryJson(ctx, validationContext)
    Write to: .tests/phasegate-summary.json
```

**Validation Module Structure:**
- **Core.cs**: 8 fundamental validations
- **Names.cs**: Identifier, collision, uniqueness checks
- **Views.cs**: View integrity (3 hard rules), scoping
- **Scopes.cs**: EmitScope invariants, mismatches
- **Constraints.cs**: Interface constraint violations
- **Finalization.cs**: Comprehensive finalization sweep (9 checks)
- **Types.cs**: TypeMap, external resolution, printer consistency
- **ImportExport.cs**: API surface, import/export completeness
- **LibraryMode.cs**: Library mode contract integrity, dangling references (LIB002)

**Total PhaseGate Checks:** 50+ validation functions (19 modules), 46 diagnostic codes (including LIB001-LIB003)

---

## Phase 5: Emit Call Graph

File generation phase:

```
Builder.Build
  ↓
EmitPhase(ctx, plan, outputDirectory) → Builder.cs:285
  ↓
┌────────────────────────────────────────────┐
│ Step 1: Support Types from @tsonic/types   │
└────────────────────────────────────────────┘
    Branded numeric types (int, uint, byte, etc.)
    Unsafe marker types (ptr<T>, ref<T>, etc.)
  ↓
┌────────────────────────────────────────────────┐
│ Step 2: Emit Internal Index Files (per namespace)│
└────────────────────────────────────────────────┘
  ↓
InternalIndexEmitter.Emit(ctx, plan, outputDirectory) → Emit/InternalIndexEmitter.cs
  For each namespace in plan.EmissionOrder:
    Create StringBuilder
    ├─→ EmitFileHeader(builder)
    │   Add: // Generated by tsbindgen
    │
    ├─→ EmitImports(builder, imports)
    │   For each import: import type { TypeA, TypeB } from "../OtherNamespace/internal/index.js"
    │
    ├─→ EmitNamespaceDeclaration(builder, namespace)
    │   Write: export namespace NamespaceName {
    │
    ├─→ For each type in plan.EmissionOrder:
    │   Switch on type.Kind:
    │     ├─→ TypeKind.Class or TypeKind.Struct:
    │     │   ClassPrinter.PrintClassDeclaration(builder, type, ctx)
    │     │     Location: Emit/Printers/ClassPrinter.cs
    │     │     ├─→ Write class/interface keyword
    │     │     ├─→ Print generic parameters with constraints
    │     │     ├─→ Print extends clause
    │     │     ├─→ Print implements clause
    │     │     ├─→ Open class body: {
    │     │     ├─→ For each constructor:
    │     │     │   MethodPrinter.PrintConstructor(builder, ctor, ctx)
    │     │     ├─→ For each field where EmitScope == ClassSurface:
    │     │     │   Write: readonly FieldName: FieldType;
    │     │     ├─→ For each property where EmitScope == ClassSurface:
    │     │     │   **Check PropertyOverridePlan:**
    │     │     │     If property in plan: Use union type from plan
    │     │     │     Else: Use original property type
    │     │     │   Write: get PropertyName: PropertyType;
    │     │     ├─→ For each method where EmitScope == ClassSurface:
    │     │     │   **Check OverrideConflictPlan:**
    │     │     │     If member in conflict set: SKIP (suppress emission)
    │     │     │   MethodPrinter.PrintMethod(builder, method, ctx)
    │     │     │     ├─→ Print method signature
    │     │     │     ├─→ If overloaded: Check OverloadRole
    │     │     │     ├─→ For each parameter:
    │     │     │     │   TypeRefPrinter.Print(param.Type, scope)
    │     │     │     │     Location: Emit/Printers/TypeRefPrinter.cs
    │     │     │     │     TypeNameResolver.ResolveTypeName(typeRef, scope, ctx.Renamer)
    │     │     │     │       Location: Emit/Printers/TypeNameResolver.cs
    │     │     │     │       Switch on TypeReference kind:
    │     │     │     │         NamedTypeReference: ctx.Renamer.GetFinalTypeName(stableId, scope)
    │     │     │     │         GenericParameterReference: Return parameter name (T, U)
    │     │     │     │         ArrayTypeReference: ResolveTypeName(elementType) + "[]"
    │     │     │     │         PointerTypeReference: "UnsafePointer<T>"
    │     │     │     │         ByRefTypeReference: "UnsafeByRef<T>"
    │     │     │     └─→ TypeRefPrinter.Print(method.ReturnType, scope)
    │     │     ├─→ **Check StaticFlatteningPlan:**
    │     │     │   If type in plan: Emit inherited static members
    │     │     │   For each inherited member: Add comment "// Inherited from BaseClass"
    │     │     ├─→ **Check StaticConflictPlan:**
    │     │     │   If static member name in conflict set: SKIP (suppress emission)
    │     │     ├─→ Close class body: }
    │     │     └─→ Emit interface views (if any ViewOnly members):
    │     │         Group ViewOnly members by SourceInterface
    │     │         For each interface:
    │     │           Write: export interface TypeName_N_View_InterfaceName {
    │     │           For each ViewOnly member: Print using member.TsEmitName
    │     │           Close: }
    │     │
    │     ├─→ TypeKind.Interface:
    │     │   ClassPrinter.PrintInterfaceDeclaration(builder, type, ctx)
    │     │
    │     ├─→ TypeKind.Enum:
    │     │   Write enum declaration
    │     │
    │     ├─→ TypeKind.Delegate:
    │     │   Write delegate type alias
    │     │
    │     └─→ TypeKind.StaticNamespace:
    │         Write static namespace class
    │
    ├─→ Close namespace: }
    └─→ Write to disk: namespace/internal/index.d.ts
  ↓
┌──────────────────────────────────────────────────┐
│ Step 2a: Emit Extension Method Buckets (once)  │
└──────────────────────────────────────────────────┘
  ↓
ExtensionsEmitter.Emit(ctx, plan.ExtensionMethods, plan.Graph, outputDirectory)
  Location: Emit/ExtensionsEmitter.cs
  Generate: internal/extensions/index.d.ts (once per build, not per namespace)
  ↓
  For each bucket in plan.ExtensionMethods.Buckets:
    Emit bucket interface: export interface __Ext_TargetType<T> {
      For each method in bucket:
        Collapse method generics matching target generics
        Emit method signature with TypeRefPrinter
        Detect IEquatable constraints → add to infer clause
      Close: }
  ↓
  Emit helper type: export type ExtensionMethods<TShape> = ...
    Map shapes to buckets with constrained infer clauses
    Example: TShape extends IEnumerable_1<infer T> ? __Ext_IEnumerable_1<T> : ...
  Write to disk: internal/extensions/index.d.ts
  Result: 122 bucket interfaces, 1,759 extension methods
  ↓
┌────────────────────────────────────────────┐
│ Step 3: Emit Facade Files (per namespace) │
└────────────────────────────────────────────┘
  ↓
FacadeEmitter.Emit(ctx, plan, outputDirectory) → Emit/FacadeEmitter.cs
  For each namespace:
    Generate: namespace/index.d.ts
    Content: export * from "./internal";
  ↓
┌────────────────────────────────────────────┐
│ Step 4: Emit Metadata Files (per namespace)│
└────────────────────────────────────────────┘
  ↓
MetadataEmitter.Emit(ctx, plan, outputDirectory) → Emit/MetadataEmitter.cs
  For each namespace:
    Build metadata JSON:
      { "namespace": "...", "types": [...], "omissions": {...} }
    Write to: namespace/metadata.json
  ↓
┌────────────────────────────────────────────┐
│ Step 5: Emit Binding Files (per namespace)│
└────────────────────────────────────────────┘
  ↓
BindingEmitter.Emit(ctx, plan, outputDirectory) → Emit/BindingEmitter.cs
  For each namespace:
    Build bindings JSON (CLR → TypeScript name mappings)
    Write to: namespace/bindings.json
  ↓
┌────────────────────────────────────────────┐
│ Step 6: Emit Module Stubs (per namespace) │
└────────────────────────────────────────────┘
  ↓
ModuleStubEmitter.Emit(ctx, plan, outputDirectory) → Emit/ModuleStubEmitter.cs
  For each namespace:
    Generate: namespace/index.js (stub for module resolution)
    Content: throw new Error("This is a type-only module");
```

**Plan-Based Emission (Critical):**
- **StaticFlatteningPlan (Pass 4.7):** Emit inherited static members for static-only types
- **StaticConflictPlan (Pass 4.8):** Suppress conflicting static members in hybrid types
- **OverrideConflictPlan (Pass 4.9):** Suppress incompatible instance overrides
- **PropertyOverridePlan (Pass 4.10):** Emit union types for property type variance

**Result:** Plan-based emission achieves **zero TypeScript errors** for full BCL (4,295 types, 130 namespaces)

**Key Printer Functions:**
- **ClassPrinter.cs**: Emits class/interface/struct declarations (plan-aware)
- **MethodPrinter.cs**: Emits method/constructor signatures
- **TypeRefPrinter.cs**: Converts TypeReference → TypeScript syntax
- **TypeNameResolver.cs**: Resolves type names through Renamer

---

## Cross-Cutting Call Graphs

Functions called across multiple phases:

### SymbolRenamer Call Graph

```
┌────────────────────────────────────────────┐
│ ReserveTypeName - Called By:               │
└────────────────────────────────────────────┘
1. NameReservation.ReserveAllNames (Phase 3.5)
2. HiddenMemberPlanner.Plan (Phase 3 - Pass 9)
3. ClassSurfaceDeduplicator.Deduplicate (Phase 3 - Pass 10.5)

┌────────────────────────────────────────────┐
│ ReserveMemberName - Called By:             │
└────────────────────────────────────────────┘
1. Reservation.ReserveMemberNamesOnly (Phase 3.5 - Class Surface)
2. Reservation.ReserveViewMemberNamesOnly (Phase 3.5 - Views)
3. IndexerPlanner.Plan (Phase 3 - Pass 8)
4. HiddenMemberPlanner.Plan (Phase 3 - Pass 9)

┌────────────────────────────────────────────┐
│ GetFinalTypeName - Called By:              │
└────────────────────────────────────────────┘
1. Application.ApplyNamesToGraph (Phase 3.5)
2. TypeNameResolver.ResolveTypeName (Phase 5 - Emit)
3. PhaseGate validation modules (Phase 4.7)

┌────────────────────────────────────────────┐
│ GetFinalMemberName - Called By:            │
└────────────────────────────────────────────┘
1. Application.ApplyNamesToGraph (Phase 3.5)
2. ClassPrinter.PrintMethod/PrintProperty (Phase 5 - Emit)
3. Views.ValidateMemberScoping (Phase 4.7)

┌────────────────────────────────────────────┐
│ TryGetDecision - Called By:                │
└────────────────────────────────────────────┘
1. NameReservation.ReserveAllNames (Phase 3.5)
2. Scopes.ValidateScopeMismatches (Phase 4.7)
3. Audit.AuditReservationCompleteness (Phase 3.5)
```

**SymbolRenamer Data Structures:**
```csharp
// Type decisions: StableId → Scope → RenameDecision
Dictionary<TypeStableId, Dictionary<Scope, RenameDecision>> _typeDecisions;

// Member decisions: StableId → Scope → RenameDecision
Dictionary<MemberStableId, Dictionary<Scope, RenameDecision>> _memberDecisions;

record RenameDecision
{
    string Requested;    // Original requested name
    string Final;        // Final name after disambiguation
    string Context;      // What triggered this reservation
    string Source;       // Which pass reserved this
}
```

### DiagnosticBag Call Graph

```
┌────────────────────────────────────────────┐
│ Error - Called By:                         │
└────────────────────────────────────────────┘
1. AssemblyLoader.LoadClosure (Phase 1) - PG_LOAD_002, PG_LOAD_003
2. PhaseGate validation modules (Phase 4.7) - PG_* (40+ codes)
3. BuildContext exception handling (Any phase) - BUILD_EXCEPTION

┌────────────────────────────────────────────┐
│ Warning - Called By:                       │
└────────────────────────────────────────────┘
1. AssemblyLoader.LoadClosure (Phase 1) - PG_LOAD_003 (non-strict)
2. PhaseGate validation modules (Phase 4.7)

┌────────────────────────────────────────────┐
│ GetAll / HasErrors - Called By:            │
└────────────────────────────────────────────┘
1. Builder.Build (End of pipeline) - BuildResult
2. PhaseGate.Validate (Phase 4.7) - Validation check
```

### Policy Call Graph

```
Policy.Emission.MemberNameTransform → Shared.ComputeMemberRequestedBase (Phase 3.5)
Policy.Omissions.OmitIndexers → IndexerPlanner.Plan (Phase 3 - Pass 8)
Policy.Safety.RequireUnsafeMarkers → TypeRefPrinter.Print (Phase 5)
Policy.Validation.StrictVersionChecks → AssemblyLoader.ValidateAssemblyIdentity (Phase 1)
```

### BuildContext.Log Call Graph

Called from everywhere (selective logging based on category/verbose flag):
- AssemblyLoader, ReflectionReader
- All 23 Shape passes
- NameReservation, PhaseGate
- All 7 Emit emitters

Usage: `ctx.Log("category", "message")`
Only logs if: verboseLogging == true OR logCategories.Contains("category")

---

## Summary

Complete call chains through tsbindgen pipeline:

1. **Entry Point** - CLI → GenerateCommand → Builder
2. **Phase 1: Load** - Assembly loading, reflection, member reading, interface substitution
3. **Phase 2: Normalize** - Index building for O(1) lookups
4. **Phase 3: Shape** - 23 transformation passes (including 4.7-4.11 planning passes)
5. **Phase 3.5: Name Reservation** - Central naming through Renamer (6 steps)
6. **Phase 4: Plan** - Import planning, ordering, overload unification, constraint audit
7. **Phase 4.7: PhaseGate** - 20+ validation functions with 40+ diagnostic codes
8. **Phase 5: Emit** - Plan-based file generation (TypeScript, metadata, bindings, stubs, extension buckets)
9. **Cross-Cutting** - SymbolRenamer, DiagnosticBag, Policy, Logging

**Key Insights:**
- Shape passes 1-16: PURE (return new graph)
- Shape passes 4.7-4.11: Return PLANS (planning data, not graph transformations)
- Renamer: MUTATED (accumulates decisions across phases)
- PhaseGate: Validates before emission (fail-fast)
- Emit: Uses TsEmitName from graph + 5 Shape plans (no further name transformation)
- Plans enable **zero TypeScript errors** for full BCL (4,295 types, 130 namespaces)
- Extension methods: 122 bucket interfaces, 1,759 methods (BCL .NET 9)
