using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DotnetBindgen.Core.Canon;
using DotnetBindgen.Load;
using DotnetBindgen.Model;
using DotnetBindgen.Model.Symbols;
using DotnetBindgen.Model.Symbols.MemberSymbols;
using DotnetBindgen.Model.Types;

namespace DotnetBindgen.Shape;

/// <summary>
/// Inlines interface hierarchies - removes extends chains.
/// Flattens all inherited members into each interface so TypeScript doesn't need extends.
/// PURE - returns new SymbolGraph.
/// </summary>
public static class InterfaceInliner
{
    public static SymbolGraph Inline(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("InterfaceInliner", "Inlining interface hierarchies...");

        var interfacesToInline = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Where(t => t.Kind == TypeKind.Interface)
            .ToList();

        ctx.Log("InterfaceInliner", $"Found {interfacesToInline.Count} interfaces to inline");

        var updatedGraph = graph;
        foreach (var iface in interfacesToInline)
        {
            updatedGraph = InlineInterface(ctx, updatedGraph, iface);
        }

        ctx.Log("InterfaceInliner", "Complete");
        return updatedGraph;
    }

    private static SymbolGraph InlineInterface(BuildContext ctx, SymbolGraph graph, TypeSymbol iface)
    {
        // Collect all members from this interface and all base interfaces
        var allMembers = new List<MethodSymbol>(iface.Members.Methods);
        var allProperties = new List<PropertySymbol>(iface.Members.Properties);
        var allEvents = new List<EventSymbol>(iface.Members.Events);

        // Walk up the interface hierarchy and collect all inherited members
        var visited = new HashSet<string>(); // Track visited interfaces by full name
        var toVisit = new Queue<(TypeReference Ref, Dictionary<string, TypeReference> SubstitutionMap)>();

        // Seed with direct base interfaces (no substitution yet, will be built per interface)
        foreach (var baseIfaceRef in iface.Interfaces)
        {
            toVisit.Enqueue((baseIfaceRef, new Dictionary<string, TypeReference>()));
        }

        while (toVisit.Count > 0)
        {
            var (baseIfaceRef, parentSubstitution) = toVisit.Dequeue();

            // Get the full name for tracking
            var fullName = GetTypeFullName(baseIfaceRef);
            if (visited.Contains(fullName))
                continue;

            visited.Add(fullName);

            // Find the base interface symbol in the graph
            var baseIface = FindInterfaceByReference(graph, baseIfaceRef);
            if (baseIface == null)
            {
                // External interface - we can't inline it, but log it
                ctx.Log("InterfaceInliner", $"Skipping external interface {fullName}");
                continue;
            }

            // FIX B: Build substitution map for this base interface reference
            // Example: IDictionary<TKey, TValue> : ICollection<KeyValuePair<TKey, TValue>>
            //   baseIfaceRef = ICollection<KeyValuePair<TKey, TValue>>
            //   baseIface = ICollection<T> (generic definition)
            //   substitutionMap = { T -> KeyValuePair<TKey, TValue> }
            var substitutionMap = BuildSubstitutionMapForInterface(baseIface, baseIfaceRef);

            // Compose with parent substitution (for chained generics)
            // If parent had { T -> string }, and we have { U -> T }, result is { U -> string }
            substitutionMap = ComposeSubstitutions(parentSubstitution, substitutionMap);

            // FIX B: Apply substitution to all members from base interface before adding them
            var substitutedMethods = SubstituteMethodMembers(baseIface.Members.Methods, substitutionMap);
            var substitutedProperties = SubstitutePropertyMembers(baseIface.Members.Properties, substitutionMap);
            var substitutedEvents = SubstituteEventMembers(baseIface.Members.Events, substitutionMap);

            allMembers.AddRange(substitutedMethods);
            allProperties.AddRange(substitutedProperties);
            allEvents.AddRange(substitutedEvents);

            // Queue base interface's bases for visiting, passing along the substitution map
            foreach (var grandparent in baseIface.Interfaces)
            {
                toVisit.Enqueue((grandparent, substitutionMap));
            }
        }

        // Deduplicate members by canonical signature
        var uniqueMethods = DeduplicateMethods(ctx, allMembers);
        var uniqueProperties = DeduplicateProperties(ctx, allProperties);
        var uniqueEvents = DeduplicateEvents(ctx, allEvents);

        // Update the interface with inlined members (keep original constructors/fields)
        var newMembers = new TypeMembers
        {
            Methods = uniqueMethods.ToImmutableArray(),
            Properties = uniqueProperties.ToImmutableArray(),
            Fields = iface.Members.Fields, // Interfaces rarely have fields
            Events = uniqueEvents.ToImmutableArray(),
            Constructors = iface.Members.Constructors // Interfaces don't have constructors
        };

        ctx.Log("InterfaceInliner", $"Inlined {iface.ClrFullName} - {uniqueMethods.Count} methods, {uniqueProperties.Count} properties");

        // Create updated type with inlined members
        // NOTE: We preserve the Interfaces list for merged interface extends (LINQ assignability).
        // The interfaces are needed so we can emit "interface IFoo$instance<T> extends IBar$instance<T> {}"
        // to make types assignable to their base interfaces in TypeScript.
        return graph.WithUpdatedType(iface.StableId.ToString(), t => t with
        {
            Members = newMembers
            // Interfaces intentionally NOT cleared - needed for merged interface extends
        });
    }

    private static string GetTypeFullName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    private static TypeSymbol? FindInterfaceByReference(SymbolGraph graph, TypeReference typeRef)
    {
        var fullName = GetTypeFullName(typeRef);

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName && t.Kind == TypeKind.Interface);
    }

    private static IReadOnlyList<MethodSymbol> DeduplicateMethods(BuildContext ctx, List<MethodSymbol> methods)
    {
        var seen = new Dictionary<string, MethodSymbol>();

        foreach (var method in methods)
        {
            var sig = ctx.CanonicalizeMethod(
                method.ClrName,
                method.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                GetTypeFullName(method.ReturnType));

            if (!seen.ContainsKey(sig))
            {
                seen[sig] = method;
            }
            // If duplicate, keep the first one (deterministic)
        }

        return seen.Values.ToList();
    }

    private static IReadOnlyList<PropertySymbol> DeduplicateProperties(BuildContext ctx, List<PropertySymbol> properties)
    {
        var seen = new Dictionary<string, PropertySymbol>();

        foreach (var prop in properties)
        {
            // TypeScript doesn't support property overloads, so deduplicate by name only
            // (not by full signature including type)
            // For indexers, include parameters to distinguish different indexer overloads
            var indexParams = prop.IndexParameters.Select(p => GetTypeFullName(p.Type)).ToList();

            string key;
            if (prop.IsIndexer)
            {
                // Indexers: use full signature (name + params + type)
                key = ctx.CanonicalizeProperty(
                    prop.ClrName,
                    indexParams,
                    GetTypeFullName(prop.PropertyType));
            }
            else
            {
                // Regular properties: use name only (TypeScript doesn't allow property overloads)
                key = prop.ClrName;
            }

            if (!seen.ContainsKey(key))
            {
                seen[key] = prop;
            }
            // If duplicate, keep the first one (most derived/specific)
        }

        return seen.Values.ToList();
    }

    private static IReadOnlyList<EventSymbol> DeduplicateEvents(BuildContext ctx, List<EventSymbol> events)
    {
        var seen = new Dictionary<string, EventSymbol>();

        foreach (var evt in events)
        {
            var sig = SignatureCanonicalizer.CanonicalizeEvent(
                evt.ClrName,
                GetTypeFullName(evt.EventHandlerType));

            if (!seen.ContainsKey(sig))
            {
                seen[sig] = evt;
            }
        }

        return seen.Values.ToList();
    }

    /// <summary>
    /// FIX B: Build substitution map for a base interface reference.
    /// Example: For IDictionary&lt;TKey, TValue&gt; : ICollection&lt;KeyValuePair&lt;TKey, TValue&gt;&gt;
    ///   baseIface = ICollection&lt;T&gt; (symbol)
    ///   baseIfaceRef = ICollection&lt;KeyValuePair&lt;TKey, TValue&gt;&gt; (reference)
    ///   Returns: { T -> KeyValuePair&lt;TKey, TValue&gt; }
    /// </summary>
    private static Dictionary<string, TypeReference> BuildSubstitutionMapForInterface(
        TypeSymbol baseIface,
        TypeReference baseIfaceRef)
    {
        var map = new Dictionary<string, TypeReference>();

        // Only NamedTypeReference can have type arguments
        if (baseIfaceRef is not NamedTypeReference namedRef)
            return map;

        // If no type arguments, no substitution needed (non-generic interface)
        if (namedRef.TypeArguments.Count == 0)
            return map;

        // Build map: GenericParameter -> TypeArgument
        if (baseIface.GenericParameters.Length != namedRef.TypeArguments.Count)
        {
            // Mismatch - should not happen, but be defensive
            return map;
        }

        for (int i = 0; i < baseIface.GenericParameters.Length; i++)
        {
            var param = baseIface.GenericParameters[i];
            var arg = namedRef.TypeArguments[i];
            map[param.Name] = arg;
        }

        return map;
    }

    /// <summary>
    /// FIX B: Compose two substitution maps for chained generics.
    /// If parent has { T -> string }, and current has { U -> T }, result is { U -> string }.
    /// </summary>
    private static Dictionary<string, TypeReference> ComposeSubstitutions(
        Dictionary<string, TypeReference> parent,
        Dictionary<string, TypeReference> current)
    {
        if (parent.Count == 0)
            return current;

        // Apply parent substitution to values in current map
        var composed = new Dictionary<string, TypeReference>();
        foreach (var (key, value) in current)
        {
            composed[key] = InterfaceMemberSubstitution.SubstituteTypeReference(value, parent);
        }

        // Add any parent mappings not in current
        foreach (var (key, value) in parent)
        {
            if (!composed.ContainsKey(key))
                composed[key] = value;
        }

        return composed;
    }

    /// <summary>
    /// FIX B: Apply generic parameter substitution to method members.
    /// CRITICAL: Only substitute type-level generics, NOT method-level generics.
    /// </summary>
    private static IReadOnlyList<MethodSymbol> SubstituteMethodMembers(
        ImmutableArray<MethodSymbol> methods,
        Dictionary<string, TypeReference> substitutionMap)
    {
        if (substitutionMap.Count == 0)
            return methods.ToList();

        var substitutedMethods = new List<MethodSymbol>();

        foreach (var method in methods)
        {
            // CRITICAL: Build exclusion set for method-level generic parameters
            // Example: Method<T>() should NOT substitute method's own T parameter
            var methodLevelParams = new HashSet<string>(
                method.GenericParameters.Select(gp => gp.Name));

            // Create filtered substitution map excluding method-level generics
            var filteredMap = substitutionMap
                .Where(kv => !methodLevelParams.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            if (filteredMap.Count == 0)
            {
                // No substitution needed (all parameters are method-level)
                substitutedMethods.Add(method);
                continue;
            }

            // Substitute return type and parameters
            var newReturnType = InterfaceMemberSubstitution.SubstituteTypeReference(
                method.ReturnType,
                filteredMap);

            var newParameters = method.Parameters
                .Select(p => p with
                {
                    Type = InterfaceMemberSubstitution.SubstituteTypeReference(p.Type, filteredMap)
                })
                .ToImmutableArray();

            // Create substituted method symbol
            substitutedMethods.Add(method with
            {
                ReturnType = newReturnType,
                Parameters = newParameters
            });
        }

        return substitutedMethods;
    }

    /// <summary>
    /// FIX B: Apply generic parameter substitution to property members.
    /// </summary>
    private static IReadOnlyList<PropertySymbol> SubstitutePropertyMembers(
        ImmutableArray<PropertySymbol> properties,
        Dictionary<string, TypeReference> substitutionMap)
    {
        if (substitutionMap.Count == 0)
            return properties.ToList();

        var substitutedProperties = new List<PropertySymbol>();

        foreach (var prop in properties)
        {
            // Substitute property type
            var newPropertyType = InterfaceMemberSubstitution.SubstituteTypeReference(
                prop.PropertyType,
                substitutionMap);

            // Substitute index parameters (for indexers)
            var newIndexParameters = prop.IndexParameters
                .Select(p => p with
                {
                    Type = InterfaceMemberSubstitution.SubstituteTypeReference(p.Type, substitutionMap)
                })
                .ToImmutableArray();

            substitutedProperties.Add(prop with
            {
                PropertyType = newPropertyType,
                IndexParameters = newIndexParameters
            });
        }

        return substitutedProperties;
    }

    /// <summary>
    /// FIX B: Apply generic parameter substitution to event members.
    /// </summary>
    private static IReadOnlyList<EventSymbol> SubstituteEventMembers(
        ImmutableArray<EventSymbol> events,
        Dictionary<string, TypeReference> substitutionMap)
    {
        if (substitutionMap.Count == 0)
            return events.ToList();

        var substitutedEvents = new List<EventSymbol>();

        foreach (var evt in events)
        {
            // Substitute event handler type
            var newHandlerType = InterfaceMemberSubstitution.SubstituteTypeReference(
                evt.EventHandlerType,
                substitutionMap);

            substitutedEvents.Add(evt with
            {
                EventHandlerType = newHandlerType
            });
        }

        return substitutedEvents;
    }
}
