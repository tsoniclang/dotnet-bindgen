using System.Collections.Generic;
using System.Linq;
using DotnetBindgen.Model;
using DotnetBindgen.Model.Symbols;
using DotnetBindgen.Model.Symbols.MemberSymbols;
using DotnetBindgen.Model.Types;

namespace DotnetBindgen.Plan;

/// <summary>
/// Builds cross-namespace dependency graph for import planning.
/// Analyzes type references to determine which namespaces need to import from which other namespaces.
/// Creates ImportGraphData containing dependency edges and namespace-local type sets.
/// </summary>
public static class ImportGraph
{
    public static ImportGraphData Build(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("ImportGraph", "Building cross-namespace dependency graph...");

        var graphData = new ImportGraphData
        {
            NamespaceDependencies = new Dictionary<string, HashSet<string>>(),
            NamespaceTypeIndex = new Dictionary<string, HashSet<string>>(),
            CrossNamespaceReferences = new List<CrossNamespaceReference>()
        };

        // Build namespace type index first
        BuildNamespaceTypeIndex(ctx, graph, graphData);

        // Analyze dependencies for each namespace
        foreach (var ns in graph.Namespaces)
        {
            AnalyzeNamespaceDependencies(ctx, graph, ns, graphData);
        }

        ctx.Log("ImportGraph", $"Found {graphData.NamespaceDependencies.Count} namespaces with dependencies");
        ctx.Log("ImportGraph", $"Total cross-namespace references: {graphData.CrossNamespaceReferences.Count}");

        return graphData;
    }

    /// <summary>
    /// PropertyOverrideUnifier can introduce new cross-namespace type references by unifying property
    /// types across inheritance chains (e.g., "BaseType | DerivedType").
    ///
    /// These unified override type strings are injected AFTER import planning, so without explicitly
    /// adding their referenced CLR types into the import graph, the emitted internal/index.d.ts can
    /// become invalid TypeScript (missing import / TS2304).
    ///
    /// This method augments an existing ImportGraphData instance with the type references required
    /// by the PropertyOverridePlan so ImportPlanner can import them deterministically.
    /// </summary>
    public static void AugmentWithPropertyOverridePlan(
        BuildContext ctx,
        SymbolGraph graph,
        ImportGraphData graphData,
        PropertyOverridePlan plan)
    {
        if (plan.PropertyOverrideReferencedClrTypes.Count == 0)
            return;

        ctx.Log("ImportGraph", $"Augmenting import graph with {plan.PropertyOverrideReferencedClrTypes.Count} property override import sets...");

        var existing = new HashSet<(string SourceNs, string SourceType, string TargetNs, string TargetType, ReferenceKind Kind)>();
        foreach (var r in graphData.CrossNamespaceReferences)
        {
            existing.Add((r.SourceNamespace, r.SourceType, r.TargetNamespace, r.TargetType, r.ReferenceKind));
        }

        foreach (var ((typeStableId, _), referencedClrTypes) in plan.PropertyOverrideReferencedClrTypes)
        {
            if (referencedClrTypes.Count == 0)
                continue;

            // StableId format: "AssemblyName:ClrFullName" (graph.TypeIndex keys are ClrFullName)
            var clrFullName = typeStableId.Contains(':')
                ? typeStableId.Substring(typeStableId.IndexOf(':') + 1)
                : typeStableId;

            if (!graph.TryGetType(clrFullName, out var type) || type == null)
                continue;

            if (!graph.TryGetNamespace(type.Namespace, out var sourceNs) || sourceNs == null)
                continue;

            if (!graphData.NamespaceDependencies.TryGetValue(sourceNs.Name, out var deps))
            {
                deps = new HashSet<string>();
                graphData.NamespaceDependencies[sourceNs.Name] = deps;
            }

            foreach (var targetClr in referencedClrTypes)
            {
                // Resolve namespace for this CLR type key.
                // Prefer the current graph's namespace index; fall back to library contract mapping in library mode.
                string? targetNsName = null;
                if (graphData.ClrFullNameToNamespace.TryGetValue(targetClr, out var nsName))
                {
                    targetNsName = nsName;
                }
                else if (ctx.LibraryContract != null &&
                         ctx.LibraryContract.ClrFullNameToNamespace.TryGetValue(targetClr, out var libNs))
                {
                    targetNsName = libNs;
                }
                else
                {
                    // Track unresolved so DeclaringAssemblyResolver can try to resolve it.
                    graphData.UnresolvedClrKeys.Add(targetClr);
                }

                var key = (sourceNs.Name, type.ClrFullName, targetNsName ?? "", targetClr, ReferenceKind.PropertyType);
                if (targetNsName != null && existing.Contains(key))
                    continue;

                RecordTypeReference(
                    ctx,
                    graphData,
                    sourceNs,
                    type.ClrFullName,
                    targetClr,
                    targetNsName,
                    ReferenceKind.PropertyType,
                    deps);

                if (targetNsName != null)
                {
                    existing.Add(key);
                }
            }
        }
    }

    private static void BuildNamespaceTypeIndex(BuildContext ctx, SymbolGraph graph, ImportGraphData graphData)
    {
        // Build index: namespace name -> set of type full names in that namespace
        // ONLY INDEX EMITTABLE TYPES - non-emitted types should not appear in import index
        foreach (var ns in graph.Namespaces)
        {
            var typeNames = new HashSet<string>();

            foreach (var type in ns.Types.Where(TypeEmissionAccessibility.IsEmittable))
            {
                // TS2304 FIX: Index this type AND all nested types recursively
                IndexTypeRecursively(type, ns.Name, typeNames, graphData);
            }

            graphData.NamespaceTypeIndex[ns.Name] = typeNames;
        }

        ctx.Log("ImportGraph", $"Indexed {graphData.NamespaceTypeIndex.Count} namespaces");
        ctx.Log("ImportGraph", $"Fast lookup map: {graphData.ClrFullNameToNamespace.Count} types");
    }

    /// <summary>
    /// TS2304 FIX: Recursively index a type and all its nested types.
    /// Ensures nested types are findable for cross-namespace imports.
    /// </summary>
    private static void IndexTypeRecursively(
        TypeSymbol type,
        string namespaceName,
        HashSet<string> typeNames,
        ImportGraphData graphData)
    {
        // Index this type
        typeNames.Add(type.ClrFullName);
        graphData.ClrFullNameToNamespace[type.ClrFullName] = namespaceName;

        // Recursively index nested types that are themselves emittable.
        foreach (var nestedType in type.NestedTypes.Where(TypeEmissionAccessibility.IsEmittable))
        {
            IndexTypeRecursively(nestedType, namespaceName, typeNames, graphData);
        }
    }

    private static void AnalyzeNamespaceDependencies(
        BuildContext ctx,
        SymbolGraph graph,
        NamespaceSymbol ns,
        ImportGraphData graphData)
    {
        var dependencies = new HashSet<string>();

        // ONLY ANALYZE EMITTABLE TYPES - non-emitted types won't appear in declarations
        foreach (var type in ns.Types.Where(TypeEmissionAccessibility.IsEmittable))
        {
            // TS2304 FIX: Analyze this type AND all nested types recursively
            AnalyzeTypeAndNestedRecursively(ctx, graph, graphData, ns, type, dependencies);
        }

        if (dependencies.Count > 0)
        {
            graphData.NamespaceDependencies[ns.Name] = dependencies;
            ctx.Log("ImportGraph", $"{ns.Name} depends on {dependencies.Count} other namespaces");
        }
    }

    private static void RecordTypeReference(
        BuildContext ctx,
        ImportGraphData graphData,
        NamespaceSymbol sourceNamespace,
        string sourceTypeClrFullName,
        string targetTypeClrFullName,
        string? targetNamespace,
        ReferenceKind referenceKind,
        HashSet<string> dependencies)
    {
        if (targetNamespace == null)
            return;

        if (targetNamespace != sourceNamespace.Name)
        {
            dependencies.Add(targetNamespace);
            graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                SourceNamespace: sourceNamespace.Name,
                SourceType: sourceTypeClrFullName,
                TargetNamespace: targetNamespace,
                TargetType: targetTypeClrFullName,
                ReferenceKind: referenceKind));
            return;
        }

        // Library mode: A namespace can be split across packages. If this reference targets a type that was filtered out
        // into a --lib package, we must still record it so ImportPlanner can import it from the library facade.
        if (ctx.LibraryContract == null || !ctx.LibraryContract.AllowedClrFullNames.Contains(targetTypeClrFullName))
            return;

        if (graphData.NamespaceTypeIndex.TryGetValue(sourceNamespace.Name, out var localTypeNames) &&
            localTypeNames.Contains(targetTypeClrFullName))
        {
            return;
        }

        dependencies.Add(sourceNamespace.Name);
        graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
            SourceNamespace: sourceNamespace.Name,
            SourceType: sourceTypeClrFullName,
            TargetNamespace: sourceNamespace.Name,
            TargetType: targetTypeClrFullName,
            ReferenceKind: referenceKind));
    }

    /// <summary>
    /// TS2304 FIX: Recursively analyze a type and all its nested types.
    /// Ensures nested type members are scanned for cross-namespace dependencies.
    /// </summary>
    private static void AnalyzeTypeAndNestedRecursively(
        BuildContext ctx,
        SymbolGraph graph,
        ImportGraphData graphData,
        NamespaceSymbol ns,
        TypeSymbol type,
        HashSet<string> dependencies)
    {
        // Analyze base class - collect ALL referenced types recursively
        if (type.BaseType != null)
        {
            var baseTypeRefs = new HashSet<(string FullName, string? Namespace)>();
            CollectTypeReferences(ctx, type.BaseType, graph, graphData, baseTypeRefs);

            foreach (var (fullName, targetNs) in baseTypeRefs)
            {
                RecordTypeReference(
                    ctx,
                    graphData,
                    ns,
                    type.ClrFullName,
                    fullName,
                    targetNs,
                    ReferenceKind.BaseClass,
                    dependencies);
            }
        }

        // Analyze interfaces - collect ALL referenced types recursively
        foreach (var ifaceRef in type.Interfaces)
        {
            var ifaceTypeRefs = new HashSet<(string FullName, string? Namespace)>();
            CollectTypeReferences(ctx, ifaceRef, graph, graphData, ifaceTypeRefs);

            foreach (var (fullName, targetNs) in ifaceTypeRefs)
            {
                RecordTypeReference(
                    ctx,
                    graphData,
                    ns,
                    type.ClrFullName,
                    fullName,
                    targetNs,
                    ReferenceKind.Interface,
                    dependencies);
            }
        }

        // Analyze explicit view interfaces. Companion view interfaces emit As_IInterface()
        // return types, so their target interfaces must be imported just like normal
        // member return types.
        foreach (var view in type.ExplicitViews)
        {
            var viewTypeRefs = new HashSet<(string FullName, string? Namespace)>();
            CollectTypeReferences(ctx, view.InterfaceReference, graph, graphData, viewTypeRefs);

            foreach (var (fullName, targetNs) in viewTypeRefs)
            {
                RecordTypeReference(
                    ctx,
                    graphData,
                    ns,
                    type.ClrFullName,
                    fullName,
                    targetNs,
                    ReferenceKind.Interface,
                    dependencies);
            }
        }

        // Analyze generic parameters constraints - collect ALL referenced types recursively
        foreach (var gp in type.GenericParameters)
        {
            foreach (var constraint in gp.Constraints)
            {
                var constraintTypeRefs = new HashSet<(string FullName, string? Namespace)>();
                CollectTypeReferences(ctx, constraint, graph, graphData, constraintTypeRefs);

                foreach (var (fullName, targetNs) in constraintTypeRefs)
                {
                    RecordTypeReference(
                        ctx,
                        graphData,
                        ns,
                        type.ClrFullName,
                        fullName,
                        targetNs,
                        ReferenceKind.GenericConstraint,
                        dependencies);
                }
            }
        }

        // Analyze members of this type (own members)
        AnalyzeMemberDependencies(ctx, graph, graphData, ns, type, dependencies);

        // Analyze inherited members for cross-namespace imports
        // When we emit a type, BindingsProvider will include inherited methods/properties
        // We need to ensure their return types and parameter types are imported
        AnalyzeInheritedMemberDependencies(ctx, graph, graphData, ns, type, dependencies);

        // TS2304 FIX: Recursively analyze nested types (ONLY PUBLIC nested types)
        foreach (var nestedType in type.NestedTypes.Where(TypeEmissionAccessibility.IsEmittable))
        {
            AnalyzeTypeAndNestedRecursively(ctx, graph, graphData, ns, nestedType, dependencies);
        }
    }

    private static void AnalyzeMemberDependencies(
        BuildContext ctx,
        SymbolGraph graph,
        ImportGraphData graphData,
        NamespaceSymbol ns,
        TypeSymbol type,
        HashSet<string> dependencies)
    {
        // Analyze methods
        foreach (var method in type.Members.Methods)
        {
            // Return type - collect ALL referenced types recursively
            var returnTypeRefs = new HashSet<(string FullName, string? Namespace)>();
            CollectTypeReferences(ctx, method.ReturnType, graph, graphData, returnTypeRefs);

            foreach (var (fullName, targetNs) in returnTypeRefs)
            {
                RecordTypeReference(
                    ctx,
                    graphData,
                    ns,
                    type.ClrFullName,
                    fullName,
                    targetNs,
                    ReferenceKind.MethodReturn,
                    dependencies);
            }

            // Parameters - collect ALL referenced types recursively
            foreach (var param in method.Parameters)
            {
                var paramTypeRefs = new HashSet<(string FullName, string? Namespace)>();
                CollectTypeReferences(ctx, param.Type, graph, graphData, paramTypeRefs);

                foreach (var (fullName, targetNs) in paramTypeRefs)
                {
                    RecordTypeReference(
                        ctx,
                        graphData,
                        ns,
                        type.ClrFullName,
                        fullName,
                        targetNs,
                        ReferenceKind.MethodParameter,
                        dependencies);
                }
            }

            // Generic parameters constraints - collect recursively
            foreach (var gp in method.GenericParameters)
            {
                foreach (var constraint in gp.Constraints)
                {
                    var constraintTypeRefs = new HashSet<(string FullName, string? Namespace)>();
                    CollectTypeReferences(ctx, constraint, graph, graphData, constraintTypeRefs);

                    foreach (var (fullName, targetNs) in constraintTypeRefs)
                    {
                        RecordTypeReference(
                            ctx,
                            graphData,
                            ns,
                            type.ClrFullName,
                            fullName,
                            targetNs,
                            ReferenceKind.GenericConstraint,
                            dependencies);
                    }
                }
            }
        }

        // Analyze constructors
        foreach (var ctor in type.Members.Constructors)
        {
            // Parameters - collect ALL referenced types recursively
            foreach (var param in ctor.Parameters)
            {
                var paramTypeRefs = new HashSet<(string FullName, string? Namespace)>();
                CollectTypeReferences(ctx, param.Type, graph, graphData, paramTypeRefs);

                foreach (var (fullName, targetNs) in paramTypeRefs)
                {
                    RecordTypeReference(
                        ctx,
                        graphData,
                        ns,
                        type.ClrFullName,
                        fullName,
                        targetNs,
                        ReferenceKind.ConstructorParameter,
                        dependencies);
                }
            }
        }

        // Analyze properties - collect ALL referenced types recursively
        foreach (var property in type.Members.Properties)
        {
            var propTypeRefs = new HashSet<(string FullName, string? Namespace)>();
            CollectTypeReferences(ctx, property.PropertyType, graph, graphData, propTypeRefs);

            foreach (var (fullName, targetNs) in propTypeRefs)
            {
                RecordTypeReference(
                    ctx,
                    graphData,
                    ns,
                    type.ClrFullName,
                    fullName,
                    targetNs,
                    ReferenceKind.PropertyType,
                    dependencies);
            }

            // Index parameters - collect recursively
            foreach (var indexParam in property.IndexParameters)
            {
                var indexTypeRefs = new HashSet<(string FullName, string? Namespace)>();
                CollectTypeReferences(ctx, indexParam.Type, graph, graphData, indexTypeRefs);

                foreach (var (fullName, targetNs) in indexTypeRefs)
                {
                    if (targetNs != null && targetNs != ns.Name)
                    {
                        dependencies.Add(targetNs);
                    }
                }
            }
        }

        // Analyze fields - collect ALL referenced types recursively
        foreach (var field in type.Members.Fields)
        {
            var fieldTypeRefs = new HashSet<(string FullName, string? Namespace)>();
            CollectTypeReferences(ctx, field.FieldType, graph, graphData, fieldTypeRefs);

            foreach (var (fullName, targetNs) in fieldTypeRefs)
            {
                RecordTypeReference(
                    ctx,
                    graphData,
                    ns,
                    type.ClrFullName,
                    fullName,
                    targetNs,
                    ReferenceKind.FieldType,
                    dependencies);
            }
        }

        // Analyze events - collect ALL referenced types recursively
        foreach (var evt in type.Members.Events)
        {
            var eventTypeRefs = new HashSet<(string FullName, string? Namespace)>();
            CollectTypeReferences(ctx, evt.EventHandlerType, graph, graphData, eventTypeRefs);

            foreach (var (fullName, targetNs) in eventTypeRefs)
            {
                RecordTypeReference(
                    ctx,
                    graphData,
                    ns,
                    type.ClrFullName,
                    fullName,
                    targetNs,
                    ReferenceKind.EventType,
                    dependencies);
            }
        }
    }

    /// <summary>
    /// Analyze inherited members from base classes for cross-namespace imports.
    /// This ensures that when BindingsProvider emits inherited methods/properties,
    /// their return types and parameter types are imported into the derived type's namespace.
    /// </summary>
    private static void AnalyzeInheritedMemberDependencies(
        BuildContext ctx,
        SymbolGraph graph,
        ImportGraphData graphData,
        NamespaceSymbol ns,
        TypeSymbol type,
        HashSet<string> dependencies)
    {
        // Get base type
        if (type.BaseType == null) return;

        var baseTypeRef = type.BaseType as NamedTypeReference;
        if (baseTypeRef == null) return;

        // Resolve base type from graph
        if (!graph.TryGetType(baseTypeRef.FullName, out var baseType) || baseType == null)
            return;

        // Analyze base type's public instance methods and properties
        // (these will be emitted as inherited exposures by BindingsProvider)
        AnalyzeInheritedMethods(ctx, graph, graphData, ns, type, baseType, dependencies);
        AnalyzeInheritedProperties(ctx, graph, graphData, ns, type, baseType, dependencies);

        // Recursively analyze base's base
        AnalyzeInheritedMemberDependenciesRecursive(ctx, graph, graphData, ns, type, baseType, dependencies);
    }

    private static void AnalyzeInheritedMemberDependenciesRecursive(
        BuildContext ctx,
        SymbolGraph graph,
        ImportGraphData graphData,
        NamespaceSymbol ns,
        TypeSymbol derivedType,
        TypeSymbol currentBase,
        HashSet<string> dependencies)
    {
        // Get next base type
        if (currentBase.BaseType == null) return;

        var nextBaseRef = currentBase.BaseType as NamedTypeReference;
        if (nextBaseRef == null) return;

        // Resolve base type from graph
        if (!graph.TryGetType(nextBaseRef.FullName, out var nextBase) || nextBase == null)
            return;

        // Analyze this base level
        AnalyzeInheritedMethods(ctx, graph, graphData, ns, derivedType, nextBase, dependencies);
        AnalyzeInheritedProperties(ctx, graph, graphData, ns, derivedType, nextBase, dependencies);

        // Continue recursion
        AnalyzeInheritedMemberDependenciesRecursive(ctx, graph, graphData, ns, derivedType, nextBase, dependencies);
    }

    private static void AnalyzeInheritedMethods(
        BuildContext ctx,
        SymbolGraph graph,
        ImportGraphData graphData,
        NamespaceSymbol ns,
        TypeSymbol derivedType,
        TypeSymbol baseType,
        HashSet<string> dependencies)
    {
        // Only analyze ClassSurface instance methods (ViewOnly methods are emitted separately)
        foreach (var method in baseType.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface && !m.IsStatic))
        {
            // Analyze return type
            var returnTypeRefs = new HashSet<(string FullName, string? Namespace)>();
            CollectTypeReferences(ctx, method.ReturnType, graph, graphData, returnTypeRefs);

            foreach (var (fullName, targetNs) in returnTypeRefs)
            {
                RecordTypeReference(
                    ctx,
                    graphData,
                    ns,
                    derivedType.ClrFullName,
                    fullName,
                    targetNs,
                    ReferenceKind.MethodReturn,
                    dependencies);
            }

            // Analyze parameters
            foreach (var param in method.Parameters)
            {
                var paramTypeRefs = new HashSet<(string FullName, string? Namespace)>();
                CollectTypeReferences(ctx, param.Type, graph, graphData, paramTypeRefs);

                foreach (var (fullName, targetNs) in paramTypeRefs)
                {
                    RecordTypeReference(
                        ctx,
                        graphData,
                        ns,
                        derivedType.ClrFullName,
                        fullName,
                        targetNs,
                        ReferenceKind.MethodParameter,
                        dependencies);
                }
            }
        }
    }

    private static void AnalyzeInheritedProperties(
        BuildContext ctx,
        SymbolGraph graph,
        ImportGraphData graphData,
        NamespaceSymbol ns,
        TypeSymbol derivedType,
        TypeSymbol baseType,
        HashSet<string> dependencies)
    {
        // Only analyze ClassSurface instance properties (ViewOnly properties are emitted separately)
        foreach (var property in baseType.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface && !p.IsStatic))
        {
            // Analyze property type
            var propTypeRefs = new HashSet<(string FullName, string? Namespace)>();
            CollectTypeReferences(ctx, property.PropertyType, graph, graphData, propTypeRefs);

            foreach (var (fullName, targetNs) in propTypeRefs)
            {
                RecordTypeReference(
                    ctx,
                    graphData,
                    ns,
                    derivedType.ClrFullName,
                    fullName,
                    targetNs,
                    ReferenceKind.PropertyType,
                    dependencies);
            }
        }
    }

    private static string? FindNamespaceForType(
        BuildContext ctx,
        SymbolGraph graph,
        ImportGraphData graphData,
        TypeReference typeRef)
    {
        // Get normalized CLR lookup key (backtick arity, generic definition)
        var clrKey = GetClrLookupKey(typeRef);
        if (clrKey == null)
            return null; // Generic parameter, placeholder, or opaque/unhandled

        // Fast O(1) lookup using CLR full name in local graph
        // CRITICAL: This now works for generic types because clrKey uses backtick form
        // Example: IEnumerable<T> → "System.Collections.Generic.IEnumerable`1"
        if (graphData.ClrFullNameToNamespace.TryGetValue(clrKey, out var ns))
            return ns;

        // Library mode: Check the library namespace index (built from full graph before filtering)
        // This allows resolving library types that were filtered out
        if (ctx.LibraryNamespaceIndex != null && ctx.LibraryNamespaceIndex.TryGetValue(clrKey, out var libNs))
            return libNs;

        // Type might be external (not in our graph or library)
        return null;
    }

    /// <summary>
    /// Get normalized CLR lookup key for a TypeReference.
    /// CRITICAL: Always returns the OPEN generic definition name (not constructed).
    /// This matches how TypeSymbol.ClrFullName is stored in the index.
    ///
    /// Examples:
    ///   IEnumerable&lt;T&gt;       → "System.Collections.Generic.IEnumerable`1"
    ///   Func&lt;T1,T2&gt;         → "System.Func`2"
    ///   Exception            → "System.Exception"
    ///
    /// Why not use FullName directly?
    ///   FullName may be constructed (with type args), but the index uses open generic keys.
    /// </summary>
    private static string? GetClrLookupKey(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => GetOpenGenericClrKey(named),
            NestedTypeReference nested => GetClrLookupKey(nested.FullReference),
            ArrayTypeReference arr => GetClrLookupKey(arr.ElementType),
            PointerTypeReference ptr => GetClrLookupKey(ptr.PointeeType),
            ByRefTypeReference byref => GetClrLookupKey(byref.ReferencedType),
            GenericParameterReference => null, // Type parameters are local, never imported
            PlaceholderTypeReference => null, // Placeholders are explicit opaque markers, no import
            _ => null
        };
    }

    /// <summary>
    /// Construct the open generic CLR key from NamedTypeReference.
    /// Always uses the format: Namespace.NameWithoutArity`Arity (for generics)
    /// or Namespace.Name (for non-generics).
    ///
    /// This avoids relying on FullName which may be constructed with type arguments.
    /// </summary>
    private static string GetOpenGenericClrKey(NamedTypeReference named)
    {
        // TS2304 FIX: For nested types, FullName already has the correct CLR format with '+' separator
        // (e.g., "System.Collections.Immutable.ImmutableArray`1+Builder")
        // We should use it directly instead of reconstructing from Namespace + Name,
        // because Name for nested types is just the child part (e.g., "Builder")
        if (named.FullName.Contains('+'))
        {
            // This is a nested type - use FullName directly, stripping type arguments if present
            var fullName = named.FullName;

            // Strip assembly qualification if present (defensive)
            if (fullName.Contains(','))
            {
                fullName = fullName.Substring(0, fullName.IndexOf(',')).Trim();
            }

            // FullName already has backtick arity in the correct CLR format
            return fullName;
        }

        var ns = named.Namespace;       // e.g., "System.Collections.Generic"
        var name = named.Name;          // e.g., "IEnumerable`1" or "List`1"
        var arity = named.Arity;        // e.g., 1 (0 for non-generic)

        // HARDENING: Validate inputs - name/namespace should not contain assembly info
        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(name))
        {
            // Fallback to FullName if namespace/name are empty
            // This shouldn't happen but prevents garbage output
            return named.FullName;
        }

        // Strip assembly qualification from name if present (defensive)
        // Example: "IEnumerable, mscorlib, Version=..." → "IEnumerable"
        if (name.Contains(','))
        {
            name = name.Substring(0, name.IndexOf(',')).Trim();
        }

        if (arity == 0)
        {
            // Non-generic type: just namespace + name
            return $"{ns}.{name}";
        }

        // Generic type: strip backtick from name if present, then reconstruct
        // Name might be "IEnumerable`1" or "IEnumerable" depending on source
        var nameWithoutArity = name.Contains('`')
            ? name.Substring(0, name.IndexOf('`'))
            : name;

        // Always use backtick arity form for consistency with index
        return $"{ns}.{nameWithoutArity}`{arity}";
    }

    private static string GetTypeFullName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            GenericParameterReference gp => gp.Name,
            ArrayTypeReference arr => GetTypeFullName(arr.ElementType),
            PointerTypeReference ptr => GetTypeFullName(ptr.PointeeType),
            ByRefTypeReference byref => GetTypeFullName(byref.ReferencedType),
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    /// <summary>
    /// Recursively collect all named type references from a TypeReference tree.
    /// This includes generic type arguments, array element types, etc.
    /// Returns set of (FullName, Namespace) pairs for all referenced named types.
    /// </summary>
    private static void CollectTypeReferences(
        BuildContext ctx,
        TypeReference? typeRef,
        SymbolGraph graph,
        ImportGraphData graphData,
        HashSet<(string FullName, string? Namespace)> collected)
    {
        if (typeRef == null) return;

        switch (typeRef)
        {
            case NamedTypeReference named:
                var ns = FindNamespaceForType(ctx, graph, graphData, named);
                // CRITICAL: Use open generic CLR key, not FullName which may be constructed
                var clrKey = GetOpenGenericClrKey(named);

                // INVARIANT: CLR keys must never contain assembly-qualified garbage
                // This guard prevents regressions of the import garbage bug (fixed in commit 70d21db)
                if (clrKey.Contains('[') || clrKey.Contains(','))
                {
                    ctx.Diagnostics.Error(
                        Core.Diagnostics.DiagnosticCodes.InvalidImportModulePath,
                        $"INVARIANT VIOLATION: CollectTypeReferences yielded assembly-qualified key: '{clrKey}' " +
                        $"from type {named.AssemblyName}:{named.FullName}. " +
                        $"This indicates GetOpenGenericClrKey() failed to strip assembly info.");
                }

                collected.Add((clrKey, ns));

                // FIX E: Track unresolved types (ns == null means not in our graph)
                if (ns == null && !string.IsNullOrEmpty(clrKey))
                {
                    graphData.UnresolvedClrKeys.Add(clrKey);
                }

                // Recurse into type arguments
                foreach (var arg in named.TypeArguments)
                {
                    CollectTypeReferences(ctx, arg, graph, graphData, collected);
                }
                break;

            case NestedTypeReference nested:
                var nestedNs = FindNamespaceForType(ctx, graph, graphData, nested);
                // CRITICAL: Use open generic CLR key for nested type
                var nestedClrKey = GetOpenGenericClrKey(nested.FullReference);

                // INVARIANT: CLR keys must never contain assembly-qualified garbage
                if (nestedClrKey.Contains('[') || nestedClrKey.Contains(','))
                {
                    ctx.Diagnostics.Error(
                        Core.Diagnostics.DiagnosticCodes.InvalidImportModulePath,
                        $"INVARIANT VIOLATION: CollectTypeReferences yielded assembly-qualified key: '{nestedClrKey}' " +
                        $"from nested type. This indicates GetOpenGenericClrKey() failed.");
                }

                collected.Add((nestedClrKey, nestedNs));

                // FIX E: Track unresolved nested types
                if (nestedNs == null && !string.IsNullOrEmpty(nestedClrKey))
                {
                    graphData.UnresolvedClrKeys.Add(nestedClrKey);
                }

                // Recurse into type arguments of nested type
                foreach (var arg in nested.FullReference.TypeArguments)
                {
                    CollectTypeReferences(ctx, arg, graph, graphData, collected);
                }
                break;

            case ArrayTypeReference arr:
                CollectTypeReferences(ctx, arr.ElementType, graph, graphData, collected);
                break;

            case PointerTypeReference ptr:
                CollectTypeReferences(ctx, ptr.PointeeType, graph, graphData, collected);
                break;

            case ByRefTypeReference byref:
                CollectTypeReferences(ctx, byref.ReferencedType, graph, graphData, collected);
                break;

            case GenericParameterReference:
                // Generic parameters don't need imports - they're declared locally
                break;

            default:
                // Unknown type reference - skip
                break;
        }
    }
}

/// <summary>
/// Import graph data structure containing namespace dependencies and cross-references.
/// </summary>
public sealed class ImportGraphData
{
    /// <summary>
    /// Maps namespace name to set of namespaces it depends on.
    /// </summary>
    public Dictionary<string, HashSet<string>> NamespaceDependencies { get; init; } = new();

    /// <summary>
    /// Maps namespace name to set of type full names defined in that namespace.
    /// </summary>
    public Dictionary<string, HashSet<string>> NamespaceTypeIndex { get; init; } = new();

    /// <summary>
    /// Fast lookup map: CLR full name (with backtick arity) → owning namespace.
    /// Example: "System.Collections.Generic.IEnumerable`1" → "System.Collections.Generic"
    /// Built once during BuildNamespaceTypeIndex for O(1) lookups.
    /// </summary>
    public Dictionary<string, string> ClrFullNameToNamespace { get; init; } = new();

    /// <summary>
    /// List of all cross-namespace type references.
    /// </summary>
    public List<CrossNamespaceReference> CrossNamespaceReferences { get; init; } = new();

    /// <summary>
    /// FIX E: Set of CLR keys that couldn't be resolved to a namespace in the current graph.
    /// These are candidates for cross-assembly resolution.
    /// </summary>
    public HashSet<string> UnresolvedClrKeys { get; init; } = new();

    /// <summary>
    /// FIX E: Maps unresolved CLR key → declaring assembly name (resolved via reflection).
    /// Populated after DeclaringAssemblyResolver runs.
    /// </summary>
    public Dictionary<string, string> UnresolvedToAssembly { get; set; } = new();
}

/// <summary>
/// Represents a single cross-namespace type reference.
/// </summary>
public sealed record CrossNamespaceReference(
    string SourceNamespace,
    string SourceType,
    string TargetNamespace,
    string TargetType,
    ReferenceKind ReferenceKind);

/// <summary>
/// Kind of cross-namespace reference.
/// </summary>
public enum ReferenceKind
{
    BaseClass,
    Interface,
    GenericConstraint,
    MethodReturn,
    MethodParameter,
    ConstructorParameter,
    PropertyType,
    FieldType,
    EventType
}
