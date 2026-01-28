using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Model.Types;
using tsbindgen.Renaming;

namespace tsbindgen.Shape;

/// <summary>
/// Normalizes NRT contracts across inheritance hierarchies for TypeScript compatibility.
///
/// TypeScript's structural typing requires compatible return types between base and derived.
/// This normalizer ensures all members in an override chain have compatible NRT annotations.
///
/// CONTRACT NORMALIZATION RULES (contract-source aware):
///
/// 1. Contract sources are bases and interfaces; derived types must conform.
/// 2. If ANY base/interface declaration is NotNull → contract is NotNull
///    - Derived Nullable must be stripped to NotNull
/// 3. If ANY base/interface declaration is Nullable → contract is Nullable
///    - Derived Oblivious must be lifted to Nullable
/// 4. If ALL base/interface declarations are Oblivious:
///    - If any derived is Nullable → lift bases to Nullable (for TS compatibility)
///    - Otherwise leave Oblivious
///
/// ALGORITHM:
/// - Group members by compiler-grade identity key (name + arity + param signature + ref/out/in)
/// - Cluster types by common ancestor (including ancestors not in the type set)
/// - Compute canonical contract from base/interface declarations
/// - Apply edits to ensure all members conform
/// </summary>
public static class NrtContractNormalizer
{
    /// <summary>
    /// Compiler-grade identity key for a method.
    /// Includes all components needed to distinguish overloads and explicit interface implementations.
    /// </summary>
    private readonly record struct MethodIdentityKey(
        string ClrName,
        int Arity,
        string ParameterSignature,  // Includes ref/out/in modifiers
        string? ExplicitInterfaceFullName);  // Non-null for explicit interface implementations

    /// <summary>
    /// Compiler-grade identity key for a property/indexer.
    /// </summary>
    private readonly record struct PropertyIdentityKey(
        string ClrName,
        bool IsIndexer,
        string IndexerSignature,  // For indexers: parameter types; for properties: empty
        string? ExplicitInterfaceFullName);

    /// <summary>
    /// Edit to apply to a member's return type nullability.
    /// </summary>
    private record NullabilityEdit(
        string TypeStableId,
        MethodIdentityKey? MethodKey,
        PropertyIdentityKey? PropertyKey,
        NrtState NewNullability,
        string Reason);

    /// <summary>
    /// Member info collected during grouping phase.
    /// </summary>
    private record MethodMemberInfo(
        TypeSymbol Type,
        MethodSymbol Method,
        NrtState Nullability,
        bool IsContractSource);  // True if this is a base/interface declaration

    private record PropertyMemberInfo(
        TypeSymbol Type,
        PropertySymbol Property,
        NrtState Nullability,
        bool IsContractSource);

    /// <summary>
    /// Normalize NRT contracts across all inheritance hierarchies.
    /// </summary>
    public static SymbolGraph Normalize(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("NrtContractNormalizer", "Normalizing NRT contracts for TypeScript compatibility...");
        ctx.Log("NrtContractNormalizer", $"Processing {graph.TypeIndex.Count} types in graph");

        // Build ancestor index for efficient common-ancestor detection
        var ancestorIndex = BuildAncestorIndex(graph);

        // Phase A: Collect all edits
        var edits = CollectEdits(ctx, graph, ancestorIndex);

        if (edits.Count == 0)
        {
            ctx.Log("NrtContractNormalizer", "No NRT contract normalization needed");
            return graph;
        }

        ctx.Log("NrtContractNormalizer", $"Collected {edits.Count} edits across {edits.Select(e => e.TypeStableId).Distinct().Count()} types");

        // Phase B: Apply edits grouped by type
        var updatedGraph = ApplyEdits(ctx, graph, edits);

        var stripCount = edits.Count(e => e.NewNullability == NrtState.NotNull);
        var liftCount = edits.Count(e => e.NewNullability == NrtState.Nullable);
        ctx.Log("NrtContractNormalizer", $"Applied {stripCount} strip-to-NotNull, {liftCount} lift-to-Nullable edits");

        return updatedGraph;
    }

    /// <summary>
    /// Build index mapping each type to its complete ancestor set (transitive closure).
    /// Includes base classes and all interfaces (direct and inherited).
    /// </summary>
    private static Dictionary<string, HashSet<string>> BuildAncestorIndex(SymbolGraph graph)
    {
        var ancestorIndex = new Dictionary<string, HashSet<string>>();

        foreach (var type in graph.TypeIndex.Values)
        {
            var ancestors = new HashSet<string>();
            CollectAllAncestors(graph, type, ancestors, new HashSet<string>());
            ancestorIndex[type.ClrFullName] = ancestors;
        }

        return ancestorIndex;
    }

    /// <summary>
    /// Recursively collect all ancestors (transitive closure of bases and interfaces).
    /// </summary>
    private static void CollectAllAncestors(SymbolGraph graph, TypeSymbol type, HashSet<string> ancestors, HashSet<string> visited)
    {
        if (visited.Contains(type.ClrFullName))
            return;
        visited.Add(type.ClrFullName);

        // Add base class chain
        if (type.BaseType is NamedTypeReference baseRef)
        {
            ancestors.Add(baseRef.FullName);

            if (graph.TypeIndex.TryGetValue(baseRef.FullName, out var baseType))
            {
                CollectAllAncestors(graph, baseType, ancestors, visited);
            }
        }

        // Add all interfaces (direct)
        foreach (var ifaceRef in type.Interfaces)
        {
            if (ifaceRef is NamedTypeReference namedIface)
            {
                ancestors.Add(namedIface.FullName);

                // Recursively add inherited interfaces
                if (graph.TypeIndex.TryGetValue(namedIface.FullName, out var iface))
                {
                    CollectAllAncestors(graph, iface, ancestors, visited);
                }
            }
        }
    }

    /// <summary>
    /// Phase A: Collect all required edits by analyzing inheritance hierarchies.
    ///
    /// TWO-PASS ALGORITHM:
    /// 1. Direct Override Enforcement: Walk inheritance edges and enforce base return contract
    ///    If base returns NotNull and derived returns Nullable → set derived to NotNull
    /// 2. Cluster-based Normalization: Handle sibling relationships through common ancestors
    /// </summary>
    private static List<NullabilityEdit> CollectEdits(
        BuildContext ctx,
        SymbolGraph graph,
        Dictionary<string, HashSet<string>> ancestorIndex)
    {
        var edits = new List<NullabilityEdit>();

        // PASS 1: Direct Override Return Contract Enforcement
        // This is the deterministic fix for TS2430 - if base is NotNull, derived MUST be NotNull
        var directOverrideEdits = CollectDirectOverrideEdits(ctx, graph, ancestorIndex);
        edits.AddRange(directOverrideEdits);

        // Track which (type, member) pairs already have edits to avoid duplicates
        var existingEdits = new HashSet<(string typeId, string memberKey)>();
        foreach (var edit in directOverrideEdits)
        {
            var key = edit.MethodKey?.ToString() ?? edit.PropertyKey?.ToString() ?? "";
            existingEdits.Add((edit.TypeStableId, key));
        }

        // PASS 2: Cluster-based normalization (for sibling relationships)
        // Group methods by compiler-grade identity key
        var methodGroups = new Dictionary<MethodIdentityKey, List<MethodMemberInfo>>();

        // Group properties/indexers by compiler-grade identity key
        var propertyGroups = new Dictionary<PropertyIdentityKey, List<PropertyMemberInfo>>();

        foreach (var type in graph.TypeIndex.Values)
        {
            var isInterface = type.Kind == TypeKind.Interface;

            foreach (var method in type.Members.Methods.Where(m => !m.IsStatic && m.EmitScope == EmitScope.ClassSurface))
            {
                var key = CreateMethodKey(method);
                if (!methodGroups.TryGetValue(key, out var group))
                {
                    group = new List<MethodMemberInfo>();
                    methodGroups[key] = group;
                }

                // Contract sources: interfaces, or methods that are NOT overrides (i.e., original declarations)
                var isContractSource = isInterface || (!method.IsOverride && method.Provenance == MemberProvenance.Original);

                group.Add(new MethodMemberInfo(type, method, GetNullability(method.ReturnType), isContractSource));
            }

            foreach (var prop in type.Members.Properties.Where(p => !p.IsStatic && p.EmitScope == EmitScope.ClassSurface))
            {
                var key = CreatePropertyKey(prop);
                if (!propertyGroups.TryGetValue(key, out var group))
                {
                    group = new List<PropertyMemberInfo>();
                    propertyGroups[key] = group;
                }

                var isContractSource = isInterface || (!prop.IsOverride && prop.Provenance == MemberProvenance.Original);

                group.Add(new PropertyMemberInfo(type, prop, GetNullability(prop.PropertyType), isContractSource));
            }
        }

        // Process method groups
        foreach (var (key, group) in methodGroups)
        {
            if (group.Count < 2) continue;

            // Find inheritance clusters (types with common ancestors)
            var clusters = FindInheritanceClustersViaTCA(ancestorIndex, group.Select(g => g.Type.ClrFullName).ToList());

            foreach (var clusterTypeNames in clusters)
            {
                if (clusterTypeNames.Count < 2) continue;

                var clusterMembers = group.Where(g => clusterTypeNames.Contains(g.Type.ClrFullName)).ToList();

                var methodEdits = ComputeClusterEdits(clusterMembers, key);
                // Filter out edits that are already covered by direct override enforcement
                foreach (var edit in methodEdits)
                {
                    var editKey = edit.MethodKey?.ToString() ?? "";
                    if (!existingEdits.Contains((edit.TypeStableId, editKey)))
                    {
                        edits.Add(edit);
                        existingEdits.Add((edit.TypeStableId, editKey));
                    }
                }
            }
        }

        // Process property groups
        foreach (var (key, group) in propertyGroups)
        {
            if (group.Count < 2) continue;

            var clusters = FindInheritanceClustersViaTCA(ancestorIndex, group.Select(g => g.Type.ClrFullName).ToList());

            foreach (var clusterTypeNames in clusters)
            {
                if (clusterTypeNames.Count < 2) continue;

                var clusterMembers = group.Where(g => clusterTypeNames.Contains(g.Type.ClrFullName)).ToList();

                var propEdits = ComputeClusterEditsForProperties(clusterMembers, key);
                foreach (var edit in propEdits)
                {
                    var editKey = edit.PropertyKey?.ToString() ?? "";
                    if (!existingEdits.Contains((edit.TypeStableId, editKey)))
                    {
                        edits.Add(edit);
                        existingEdits.Add((edit.TypeStableId, editKey));
                    }
                }
            }
        }

        return edits;
    }

    /// <summary>
    /// PASS 1: Direct Override Return Contract Enforcement
    ///
    /// For every derived member that overrides/implements a base member:
    /// - If base return is NotNull and derived return is Nullable → set derived to NotNull
    ///
    /// This is the deterministic fix for TS2430 - it walks actual inheritance edges,
    /// not group heuristics.
    /// </summary>
    private static List<NullabilityEdit> CollectDirectOverrideEdits(
        BuildContext ctx,
        SymbolGraph graph,
        Dictionary<string, HashSet<string>> ancestorIndex)
    {
        var edits = new List<NullabilityEdit>();

        foreach (var type in graph.TypeIndex.Values)
        {
            // Skip interfaces - they define contracts, don't override
            if (type.Kind == TypeKind.Interface)
                continue;

            // Process methods
            foreach (var method in type.Members.Methods.Where(m => !m.IsStatic && m.EmitScope == EmitScope.ClassSurface))
            {
                // Skip if method is not an override
                if (!method.IsOverride && method.Provenance == MemberProvenance.Original)
                    continue;

                var derivedNullability = GetNullability(method.ReturnType);

                // Skip if derived is already NotNull - no strengthening needed
                // But DO NOT skip Oblivious - when base is NotNull, Oblivious must be strengthened
                // to NotNull to avoid TS2430 (weaker return type in override)
                if (derivedNullability == NrtState.NotNull)
                    continue;

                // Find base declaration(s) and check their return nullability
                var baseNotNull = FindBaseMethodHasNotNullReturn(graph, type, method, ancestorIndex);

                if (baseNotNull)
                {
                    var key = CreateMethodKey(method);
                    edits.Add(new NullabilityEdit(
                        type.StableId.ToString(),
                        key,
                        null,
                        NrtState.NotNull,
                        $"Direct override enforcement: {type.ClrFullName}.{method.ClrName} base is NotNull"));
                }
            }

            // Process properties
            foreach (var prop in type.Members.Properties.Where(p => !p.IsStatic && p.EmitScope == EmitScope.ClassSurface))
            {
                if (!prop.IsOverride && prop.Provenance == MemberProvenance.Original)
                    continue;

                var derivedNullability = GetNullability(prop.PropertyType);

                // Skip if derived is already NotNull - no strengthening needed
                // But DO NOT skip Oblivious - when base is NotNull, Oblivious must be strengthened
                if (derivedNullability == NrtState.NotNull)
                    continue;

                var baseNotNull = FindBasePropertyHasNotNullReturn(graph, type, prop, ancestorIndex);

                if (baseNotNull)
                {
                    var key = CreatePropertyKey(prop);
                    edits.Add(new NullabilityEdit(
                        type.StableId.ToString(),
                        null,
                        key,
                        NrtState.NotNull,
                        $"Direct override enforcement: {type.ClrFullName}.{prop.ClrName} base is NotNull"));
                }
            }
        }

        return edits;
    }

    /// <summary>
    /// Check if any base declaration of this method has NotNull return type.
    /// Walks: base class chain + implemented interfaces.
    /// </summary>
    private static bool FindBaseMethodHasNotNullReturn(
        SymbolGraph graph,
        TypeSymbol derivedType,
        MethodSymbol derivedMethod,
        Dictionary<string, HashSet<string>> ancestorIndex)
    {
        var methodKey = CreateMethodKey(derivedMethod);

        // Get all ancestors of this type
        if (!ancestorIndex.TryGetValue(derivedType.ClrFullName, out var ancestors))
            return false;

        foreach (var ancestorName in ancestors)
        {
            if (!graph.TypeIndex.TryGetValue(ancestorName, out var ancestorType))
                continue;

            // Look for matching method in ancestor
            foreach (var baseMethod in ancestorType.Members.Methods.Where(m => !m.IsStatic && m.EmitScope == EmitScope.ClassSurface))
            {
                var baseKey = CreateMethodKey(baseMethod);

                // Match by compiler-grade identity (ignoring explicit interface on derived side)
                if (MethodKeysMatch(methodKey, baseKey))
                {
                    var baseNullability = GetNullability(baseMethod.ReturnType);
                    if (baseNullability == NrtState.NotNull)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Check if any base declaration of this property has NotNull return type.
    /// </summary>
    private static bool FindBasePropertyHasNotNullReturn(
        SymbolGraph graph,
        TypeSymbol derivedType,
        PropertySymbol derivedProp,
        Dictionary<string, HashSet<string>> ancestorIndex)
    {
        var propKey = CreatePropertyKey(derivedProp);

        if (!ancestorIndex.TryGetValue(derivedType.ClrFullName, out var ancestors))
            return false;

        foreach (var ancestorName in ancestors)
        {
            if (!graph.TypeIndex.TryGetValue(ancestorName, out var ancestorType))
                continue;

            foreach (var baseProp in ancestorType.Members.Properties.Where(p => !p.IsStatic && p.EmitScope == EmitScope.ClassSurface))
            {
                var baseKey = CreatePropertyKey(baseProp);

                if (PropertyKeysMatch(propKey, baseKey))
                {
                    var baseNullability = GetNullability(baseProp.PropertyType);
                    if (baseNullability == NrtState.NotNull)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Check if two method keys match for override purposes.
    /// Ignores explicit interface name on derived (derived might be implicit impl of interface).
    /// </summary>
    private static bool MethodKeysMatch(MethodIdentityKey derived, MethodIdentityKey @base)
    {
        // Name must match (or derived implements base interface method implicitly)
        if (derived.ClrName != @base.ClrName)
            return false;

        // Arity must match
        if (derived.Arity != @base.Arity)
            return false;

        // Parameter signature must match
        if (derived.ParameterSignature != @base.ParameterSignature)
            return false;

        // If base is explicit interface impl, derived must match same interface
        // But if base is NOT explicit, derived can be either explicit or implicit
        if (@base.ExplicitInterfaceFullName != null &&
            derived.ExplicitInterfaceFullName != @base.ExplicitInterfaceFullName)
            return false;

        return true;
    }

    /// <summary>
    /// Check if two property keys match for override purposes.
    /// </summary>
    private static bool PropertyKeysMatch(PropertyIdentityKey derived, PropertyIdentityKey @base)
    {
        if (derived.ClrName != @base.ClrName)
            return false;

        if (derived.IsIndexer != @base.IsIndexer)
            return false;

        if (derived.IndexerSignature != @base.IndexerSignature)
            return false;

        if (@base.ExplicitInterfaceFullName != null &&
            derived.ExplicitInterfaceFullName != @base.ExplicitInterfaceFullName)
            return false;

        return true;
    }

    /// <summary>
    /// Find inheritance clusters for a member group.
    ///
    /// IMPORTANT: We only cluster types that are connected via an actual inheritance edge
    /// BETWEEN TYPES THAT DECLARE THE MEMBER (i.e., both types appear in the member group).
    ///
    /// We MUST NOT cluster unrelated types just because they share a common ancestor like
    /// System.Object. Doing so would incorrectly normalize nullability across unrelated
    /// members that happen to share a CLR name (e.g., many types have a "Value" property).
    /// </summary>
    private static List<HashSet<string>> FindInheritanceClustersViaTCA(
        Dictionary<string, HashSet<string>> ancestorIndex,
        List<string> typeNames)
    {
        var typeSet = new HashSet<string>(typeNames);

        // Union-Find to cluster types
        var parent = new Dictionary<string, string>();
        foreach (var t in typeNames) parent[t] = t;

        string Find(string t)
        {
            if (parent[t] != t)
                parent[t] = Find(parent[t]);
            return parent[t];
        }

        void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
                parent[ra] = rb;
        }

        // Union types where an ancestor that DECLARES THE MEMBER is also in the type set.
        foreach (var typeName in typeNames)
        {
            if (!ancestorIndex.TryGetValue(typeName, out var ancestors))
                continue;

            foreach (var ancestor in ancestors)
            {
                if (typeSet.Contains(ancestor))
                {
                    // typeName inherits from ancestor, and ancestor is in our type set
                    Union(typeName, ancestor);
                }
            }
        }

        // Group by root
        var clusters = new Dictionary<string, HashSet<string>>();
        foreach (var typeName in typeNames)
        {
            var root = Find(typeName);
            if (!clusters.TryGetValue(root, out var cluster))
            {
                cluster = new HashSet<string>();
                clusters[root] = cluster;
            }
            cluster.Add(typeName);
        }

        return clusters.Values.ToList();
    }

    /// <summary>
    /// Compute edits for a cluster of related method declarations.
    /// Respects contract direction: base/interface declarations define the contract.
    /// </summary>
    private static List<NullabilityEdit> ComputeClusterEdits(
        List<MethodMemberInfo> clusterMembers,
        MethodIdentityKey key)
    {
        var edits = new List<NullabilityEdit>();

        // Separate contract sources (bases/interfaces) from derived
        var contractSources = clusterMembers.Where(m => m.IsContractSource).ToList();
        var derived = clusterMembers.Where(m => !m.IsContractSource).ToList();

        // Determine canonical contract from sources
        NrtState? canonicalContract = null;

        // Rule 1: If ANY contract source is NotNull → contract is NotNull
        if (contractSources.Any(s => s.Nullability == NrtState.NotNull))
        {
            canonicalContract = NrtState.NotNull;
        }
        // Rule 2: If ANY contract source is Nullable → contract is Nullable
        else if (contractSources.Any(s => s.Nullability == NrtState.Nullable))
        {
            canonicalContract = NrtState.Nullable;
        }
        // Rule 3: All contract sources are Oblivious
        else if (contractSources.All(s => s.Nullability == NrtState.Oblivious))
        {
            // If any derived is Nullable, we need to lift Oblivious bases to Nullable for TS compatibility
            if (clusterMembers.Any(m => m.Nullability == NrtState.Nullable))
            {
                canonicalContract = NrtState.Nullable;
            }
            // Otherwise leave Oblivious - no edits needed
        }

        if (canonicalContract == null)
            return edits;

        // Apply edits to make all members conform to canonical contract
        foreach (var member in clusterMembers)
        {
            if (member.Nullability == canonicalContract)
                continue;

            // Only edit if necessary for TS compatibility
            // Strip Nullable to NotNull when contract is NotNull
            // Lift Oblivious to Nullable when contract is Nullable
            if ((canonicalContract == NrtState.NotNull && member.Nullability == NrtState.Nullable) ||
                (canonicalContract == NrtState.Nullable && member.Nullability == NrtState.Oblivious))
            {
                edits.Add(new NullabilityEdit(
                    member.Type.StableId.ToString(),
                    key,
                    null,
                    canonicalContract.Value,
                    $"Normalize {member.Type.ClrFullName}.{member.Method.ClrName} to {canonicalContract}"));
            }
        }

        return edits;
    }

    /// <summary>
    /// Compute edits for a cluster of related property declarations.
    /// </summary>
    private static List<NullabilityEdit> ComputeClusterEditsForProperties(
        List<PropertyMemberInfo> clusterMembers,
        PropertyIdentityKey key)
    {
        var edits = new List<NullabilityEdit>();

        var contractSources = clusterMembers.Where(m => m.IsContractSource).ToList();
        var derived = clusterMembers.Where(m => !m.IsContractSource).ToList();

        NrtState? canonicalContract = null;

        if (contractSources.Any(s => s.Nullability == NrtState.NotNull))
        {
            canonicalContract = NrtState.NotNull;
        }
        else if (contractSources.Any(s => s.Nullability == NrtState.Nullable))
        {
            canonicalContract = NrtState.Nullable;
        }
        else if (contractSources.All(s => s.Nullability == NrtState.Oblivious))
        {
            if (clusterMembers.Any(m => m.Nullability == NrtState.Nullable))
            {
                canonicalContract = NrtState.Nullable;
            }
        }

        if (canonicalContract == null)
            return edits;

        foreach (var member in clusterMembers)
        {
            if (member.Nullability == canonicalContract)
                continue;

            if ((canonicalContract == NrtState.NotNull && member.Nullability == NrtState.Nullable) ||
                (canonicalContract == NrtState.Nullable && member.Nullability == NrtState.Oblivious))
            {
                edits.Add(new NullabilityEdit(
                    member.Type.StableId.ToString(),
                    null,
                    key,
                    canonicalContract.Value,
                    $"Normalize {member.Type.ClrFullName}.{member.Property.ClrName} to {canonicalContract}"));
            }
        }

        return edits;
    }

    /// <summary>
    /// Create compiler-grade method identity key.
    /// </summary>
    private static MethodIdentityKey CreateMethodKey(MethodSymbol method)
    {
        // Build parameter signature including ref/out/in modifiers
        var paramParts = method.Parameters.Select(p =>
        {
            var typeName = GetClrTypeName(p.Type);
            if (p.IsOut) return $"out:{typeName}";
            if (p.IsIn) return $"in:{typeName}";
            if (p.IsRef) return $"ref:{typeName}";
            return typeName;
        });

        var paramSig = string.Join(",", paramParts);

        // Explicit interface implementation detection via SourceInterface
        string? explicitIfaceName = null;
        if (method.SourceInterface != null)
        {
            explicitIfaceName = GetClrTypeName(method.SourceInterface);
        }

        return new MethodIdentityKey(
            method.ClrName,
            method.Arity,
            paramSig,
            explicitIfaceName);
    }

    /// <summary>
    /// Create compiler-grade property identity key.
    /// </summary>
    private static PropertyIdentityKey CreatePropertyKey(PropertySymbol prop)
    {
        string indexerSig = "";
        if (prop.IsIndexer)
        {
            var indexParts = prop.IndexParameters.Select(p =>
            {
                var typeName = GetClrTypeName(p.Type);
                if (p.IsOut) return $"out:{typeName}";
                if (p.IsIn) return $"in:{typeName}";
                if (p.IsRef) return $"ref:{typeName}";
                return typeName;
            });
            indexerSig = string.Join(",", indexParts);
        }

        string? explicitIfaceName = null;
        if (prop.SourceInterface != null)
        {
            explicitIfaceName = GetClrTypeName(prop.SourceInterface);
        }

        return new PropertyIdentityKey(
            prop.ClrName,
            prop.IsIndexer,
            indexerSig,
            explicitIfaceName);
    }

    /// <summary>
    /// Phase B: Apply edits to the graph, grouped by type.
    /// </summary>
    private static SymbolGraph ApplyEdits(BuildContext ctx, SymbolGraph graph, List<NullabilityEdit> edits)
    {
        var updatedGraph = graph;

        // Group edits by type
        var editsByType = edits.GroupBy(e => e.TypeStableId).ToList();

        ctx.Log("NrtContractNormalizer", $"Applying {edits.Count} edits to {editsByType.Count} types");

        foreach (var typeGroup in editsByType)
        {
            var typeStableId = typeGroup.Key;
            var typeEdits = typeGroup.ToList();

            updatedGraph = updatedGraph.WithUpdatedType(typeStableId, type =>
            {
                // Build lookup for method edits
                var methodEditsByKey = typeEdits
                    .Where(e => e.MethodKey.HasValue)
                    .GroupBy(e => e.MethodKey!.Value)
                    .ToDictionary(g => g.Key, g => MergeDuplicateEdits(ctx, typeStableId, g));

                // Build lookup for property edits
                var propertyEditsByKey = typeEdits
                    .Where(e => e.PropertyKey.HasValue)
                    .GroupBy(e => e.PropertyKey!.Value)
                    .ToDictionary(g => g.Key, g => MergeDuplicateEdits(ctx, typeStableId, g));

                var updatedMethods = type.Members.Methods.Select(m =>
                {
                    if (m.EmitScope != EmitScope.ClassSurface)
                        return m;

                    var key = CreateMethodKey(m);
                    if (!methodEditsByKey.TryGetValue(key, out var edit))
                        return m;

                    var newReturnType = SetNullability(m.ReturnType, edit.NewNullability);
                    return m with { ReturnType = newReturnType };
                }).ToImmutableArray();

                var updatedProperties = type.Members.Properties.Select(p =>
                {
                    if (p.EmitScope != EmitScope.ClassSurface)
                        return p;

                    var key = CreatePropertyKey(p);
                    if (!propertyEditsByKey.TryGetValue(key, out var edit))
                        return p;

                    var newPropertyType = SetNullability(p.PropertyType, edit.NewNullability);
                    return p with { PropertyType = newPropertyType };
                }).ToImmutableArray();

                return type with
                {
                    Members = type.Members with
                    {
                        Methods = updatedMethods,
                        Properties = updatedProperties
                    }
                };
            });
        }

        return updatedGraph;
    }

    private static NullabilityEdit MergeDuplicateEdits(
        BuildContext ctx,
        string typeStableId,
        IEnumerable<NullabilityEdit> edits)
    {
        var merged = edits.First();
        var mergedState = merged.NewNullability;
        var mergedReason = merged.Reason;
        var conflict = false;

        foreach (var edit in edits.Skip(1))
        {
            if (edit.NewNullability != mergedState)
            {
                conflict = true;
                mergedState = MergeNullability(mergedState, edit.NewNullability);
            }

            // Preserve all provenance for debugging; this does not affect output determinism.
            mergedReason = $"{mergedReason}; {edit.Reason}";
        }

        if (conflict)
        {
            ctx.Log(
                "NrtContractNormalizer",
                $"Merged conflicting NRT edits for {typeStableId}: {merged.NewNullability} → {mergedState} ({mergedReason})");
        }

        if (mergedState == merged.NewNullability && mergedReason == merged.Reason)
            return merged;

        return merged with { NewNullability = mergedState, Reason = mergedReason };
    }

    private static NrtState MergeNullability(NrtState a, NrtState b)
    {
        // Contract-source rule: NotNull is the strictest and is always safe for TS structural compatibility
        // when multiple base/interface declarations disagree (string is assignable to string|undefined).
        if (a == NrtState.NotNull || b == NrtState.NotNull)
            return NrtState.NotNull;

        if (a == NrtState.Nullable || b == NrtState.Nullable)
            return NrtState.Nullable;

        return NrtState.Oblivious;
    }

    // ========== Helper Methods ==========

    private static NrtState GetNullability(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.Nullability,
            GenericParameterReference gp => gp.Nullability,
            ArrayTypeReference arr => arr.Nullability,
            _ => NrtState.Oblivious
        };
    }

    private static TypeReference SetNullability(TypeReference typeRef, NrtState nullability)
    {
        return typeRef switch
        {
            NamedTypeReference named => named with { Nullability = nullability },
            GenericParameterReference gp => gp with { Nullability = nullability },
            ArrayTypeReference arr => arr with { Nullability = nullability },
            _ => typeRef
        };
    }

    private static string GetClrTypeName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            GenericParameterReference gp => gp.Name,
            ArrayTypeReference arr => $"{GetClrTypeName(arr.ElementType)}[]",
            PointerTypeReference ptr => $"{GetClrTypeName(ptr.PointeeType)}*",
            ByRefTypeReference byRef => $"{GetClrTypeName(byRef.ReferencedType)}&",
            _ => typeRef.ToString() ?? ""
        };
    }
}
