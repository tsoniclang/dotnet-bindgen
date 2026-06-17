using System.Collections.Generic;
using System.Linq;
using DotnetBindgen.Model;
using DotnetBindgen.Model.Symbols;
using DotnetBindgen.Model.Symbols.MemberSymbols;

namespace DotnetBindgen.Shape;

/// <summary>
/// D1: Handles static-only type hierarchies to eliminate TS2417 errors.
///
/// For classes with ONLY static members (e.g., SIMD intrinsics like Sse, Avx),
/// TypeScript's static-side inheritance checking causes TS2417 errors when
/// derived classes add static methods with incompatible signatures.
///
/// Solution: Flatten the hierarchy by:
/// 1. Removing the 'extends' relationship from TypeScript emission
/// 2. Copying all base class static members into derived classes
/// 3. Preserving CLR inheritance in metadata for runtime binding
///
/// This is safe because:
/// - No instance polymorphism exists (everything is static)
/// - All members remain accessible (nothing omitted)
/// - Runtime behavior unchanged (methods are inherited in CLR anyway)
/// </summary>
public static class StaticHierarchyFlattener
{
    /// <summary>
    /// Analyzes the symbol graph and marks static-only types for hierarchy flattening.
    /// Returns a StaticFlatteningPlan containing:
    /// - Which types should have their extends suppressed
    /// - Additional static members to emit on each type (copied from bases)
    /// </summary>
    public static StaticFlatteningPlan Plan(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("StaticHierarchyFlattener", "Analyzing static-only hierarchies...");

        var plan = new StaticFlatteningPlan();

        // Step 1: Identify static-only types
        var staticOnlyTypes = IdentifyStaticOnlyTypes(ctx, graph);
        ctx.Log("StaticHierarchyFlattener", $"Found {staticOnlyTypes.Count} static-only types");

        // Step 2: Build inheritance graph for static-only types
        var hierarchies = BuildStaticHierarchies(ctx, graph, staticOnlyTypes);
        ctx.Log("StaticHierarchyFlattener", $"Found {hierarchies.Count} static-only hierarchies");

        // Step 3: For each static-only type, compute flattened member set
        foreach (var type in staticOnlyTypes)
        {
            // Skip types not in TypeIndex (platform-specific intrinsics, internal types, etc.)
            if (!graph.IsEmittableType(type.StableId.ToString()))
                continue;

            var baseMembers = CollectBaseStaticMembers(ctx, graph, type, staticOnlyTypes);

            if (baseMembers.Count > 0)
            {
                plan.TypesRequiringFlattening.Add(type.StableId.ToString());
                plan.InheritedStaticMembers[type.StableId.ToString()] = baseMembers;

                ctx.Log("StaticHierarchyFlattener",
                    $"  {type.ClrFullName}: will inherit {baseMembers.Count} base static members");
            }
        }

        ctx.Log("StaticHierarchyFlattener",
            $"Marked {plan.TypesRequiringFlattening.Count} types for static hierarchy flattening");

        return plan;
    }

    /// <summary>
    /// Identifies types that have ONLY static members (no instance constructors, fields, properties, or methods).
    /// These are candidates for hierarchy flattening.
    /// </summary>
    private static HashSet<TypeSymbol> IdentifyStaticOnlyTypes(BuildContext ctx, SymbolGraph graph)
    {
        var staticOnly = new HashSet<TypeSymbol>();

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Only consider classes (interfaces/structs/enums don't have this issue)
                if (type.Kind != TypeKind.Class)
                    continue;

                // Check if type has ANY instance members
                var hasInstanceMembers =
                    type.Members.Constructors.Any(c => !c.IsStatic) ||
                    type.Members.Methods.Any(m => !m.IsStatic) ||
                    type.Members.Properties.Any(p => !p.IsStatic) ||
                    type.Members.Fields.Any(f => !f.IsStatic);

                if (!hasInstanceMembers)
                {
                    // This is a static-only class
                    var staticMemberCount =
                        type.Members.Methods.Count(m => m.IsStatic) +
                        type.Members.Properties.Count(p => p.IsStatic) +
                        type.Members.Fields.Count(f => f.IsStatic);

                    if (staticMemberCount > 0)
                    {
                        staticOnly.Add(type);
                        ctx.Log("StaticHierarchyFlattener",
                            $"  Static-only: {type.ClrFullName} ({staticMemberCount} static members)");
                    }
                }
            }
        }

        return staticOnly;
    }

    /// <summary>
    /// Builds inheritance hierarchies among static-only types.
    /// Returns list of (derived, base) pairs.
    /// </summary>
    private static List<(TypeSymbol Derived, TypeSymbol Base)> BuildStaticHierarchies(
        BuildContext ctx,
        SymbolGraph graph,
        HashSet<TypeSymbol> staticOnlyTypes)
    {
        var hierarchies = new List<(TypeSymbol, TypeSymbol)>();

        foreach (var derived in staticOnlyTypes)
        {
            if (derived.BaseType == null)
                continue;

            var baseType = FindTypeByReference(graph, derived.BaseType);
            if (baseType != null && staticOnlyTypes.Contains(baseType))
            {
                hierarchies.Add((derived, baseType));
                ctx.Log("StaticHierarchyFlattener",
                    $"  Hierarchy: {derived.ClrFullName} → {baseType.ClrFullName}");
            }
        }

        return hierarchies;
    }

    /// <summary>
    /// Collects ALL static members from base classes (transitively).
    /// Returns the complete set of static members that should be copied into the derived class.
    /// </summary>
    private static InheritedStaticMembers CollectBaseStaticMembers(
        BuildContext ctx,
        SymbolGraph graph,
        TypeSymbol derived,
        HashSet<TypeSymbol> staticOnlyTypes)
    {
        var inherited = new InheritedStaticMembers();
        var visited = new HashSet<string>();

        // Walk up the inheritance chain
        var current = derived;
        while (current.BaseType != null)
        {
            var baseType = FindTypeByReference(graph, current.BaseType);
            if (baseType == null)
                break;

            // Only process if base is also static-only (otherwise we keep normal inheritance)
            if (!staticOnlyTypes.Contains(baseType))
                break;

            // Collect static members from this base
            CollectStaticMembersFromType(baseType, inherited, visited);

            current = baseType;
        }

        return inherited;
    }

    /// <summary>
    /// Collects static members from a single type, avoiding duplicates.
    /// </summary>
    private static void CollectStaticMembersFromType(
        TypeSymbol type,
        InheritedStaticMembers inherited,
        HashSet<string> visited)
    {
        // Add static methods
        foreach (var method in type.Members.Methods.Where(m => m.IsStatic))
        {
            // Use CLR signature as key to avoid duplicates
            var key = $"M:{method.ClrName}:{string.Join(",", method.Parameters.Select(p => p.Type.ToString()))}";
            if (visited.Add(key))
            {
                inherited.Methods.Add(method);
            }
        }

        // Add static properties
        foreach (var property in type.Members.Properties.Where(p => p.IsStatic))
        {
            var key = $"P:{property.ClrName}";
            if (visited.Add(key))
            {
                inherited.Properties.Add(property);
            }
        }

        // Add static fields
        foreach (var field in type.Members.Fields.Where(f => f.IsStatic))
        {
            var key = $"F:{field.ClrName}";
            if (visited.Add(key))
            {
                inherited.Fields.Add(field);
            }
        }
    }

    /// <summary>
    /// Finds a TypeSymbol in the graph by TypeReference.
    /// </summary>
    private static TypeSymbol? FindTypeByReference(SymbolGraph graph, Model.Types.TypeReference typeRef)
    {
        var fullName = typeRef switch
        {
            Model.Types.NamedTypeReference named => named.FullName,
            Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            _ => null
        };

        if (fullName == null)
            return null;

        // Skip System.Object and System.ValueType
        if (fullName == "System.Object" || fullName == "System.ValueType")
            return null;

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName);
    }
}

/// <summary>
/// Plan for flattening static-only type hierarchies.
/// Contains information about which types need flattening and what members to copy.
/// </summary>
public sealed class StaticFlatteningPlan
{
    /// <summary>
    /// Set of type StableIds that should have their 'extends' suppressed in emission.
    /// </summary>
    public HashSet<string> TypesRequiringFlattening { get; } = new();

    /// <summary>
    /// Maps type StableId → inherited static members to emit.
    /// These members come from base classes and should be emitted on the derived class.
    /// </summary>
    public Dictionary<string, InheritedStaticMembers> InheritedStaticMembers { get; } = new();

    /// <summary>
    /// Checks if a type should have its inheritance flattened.
    /// </summary>
    public bool ShouldFlattenType(string typeStableId)
    {
        return TypesRequiringFlattening.Contains(typeStableId);
    }

    /// <summary>
    /// Gets inherited static members for a type (empty if none).
    /// </summary>
    public InheritedStaticMembers GetInheritedMembers(string typeStableId)
    {
        return InheritedStaticMembers.TryGetValue(typeStableId, out var members)
            ? members
            : new InheritedStaticMembers();
    }
}

/// <summary>
/// Collection of inherited static members from base classes.
/// Used for static hierarchy flattening to copy base members into derived classes.
/// </summary>
public sealed class InheritedStaticMembers
{
    public List<MethodSymbol> Methods { get; } = new();
    public List<PropertySymbol> Properties { get; } = new();
    public List<FieldSymbol> Fields { get; } = new();

    public int Count => Methods.Count + Properties.Count + Fields.Count;
}
