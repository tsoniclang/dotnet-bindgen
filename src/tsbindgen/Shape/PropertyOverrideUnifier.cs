using System.Collections.Generic;
using System.Linq;
using tsbindgen.Emit;
using tsbindgen.Emit.Printers;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Types;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Plan;

namespace tsbindgen.Shape;

/// <summary>
/// Unifies property override types across inheritance hierarchies to eliminate TS2416 errors.
///
/// Problem:
///   class Base { readonly level: RequestCacheLevel; }
///   class Derived : Base { readonly level: HttpRequestCacheLevel; } // TS2416!
///
/// Solution:
///   Use union type in both: readonly level: RequestCacheLevel | HttpRequestCacheLevel;
///
/// This pass walks all inheritance chains, finds properties with conflicting types,
/// and computes unified union types for emission.
/// </summary>
public static class PropertyOverrideUnifier
{
    public static PropertyOverridePlan Build(SymbolGraph graph, BuildContext ctx)
    {
        ctx.Log("PropertyOverrideUnifier", "Analyzing property override chains...");

        var plan = new PropertyOverridePlan();

        // Process all types that have a base class
        var typesWithBase = graph.TypeIndex.Values.Where(t => t.BaseType != null).ToList();
        ctx.Log("PropertyOverrideUnifier", $"Found {typesWithBase.Count} types with base classes");

        foreach (var type in typesWithBase)
        {
            // Skip types not in TypeIndex (defensive check - should already be filtered)
            if (!graph.IsEmittableType(type.StableId.ToString()))
                continue;

            UnifyPropertiesInHierarchy(type, graph, ctx, plan);
        }

        ctx.Log("PropertyOverrideUnifier", $"Unified {plan.PropertyTypeOverrides.Count / 2} property chains ({plan.PropertyTypeOverrides.Count} total entries)");

        return plan;
    }

    private static void UnifyPropertiesInHierarchy(
        TypeSymbol type,
        SymbolGraph graph,
        BuildContext ctx,
        PropertyOverridePlan plan)
    {
        // Get full inheritance chain for this type
        var hierarchy = GetHierarchy(type, graph).ToList();

        if (hierarchy.Count <= 1)
            return; // No base classes, nothing to unify

        // Collect all ClassSurface properties from the entire hierarchy
        // ViewOnly properties have different nullability contracts and are handled separately
        var allPropertiesInHierarchy = hierarchy
            .SelectMany(t => t.Members.Properties
                .Where(p => p.EmitScope == EmitScope.ClassSurface)
                .Select(p => (Type: t, Property: p)))
            .ToList();

        // Group by CLR property name (properties with same name across hierarchy)
        var propertyGroups = allPropertiesInHierarchy
            .GroupBy(tp => tp.Property.ClrName)
            .ToList();

        // For each property name, check if types differ across hierarchy
        foreach (var group in propertyGroups)
        {
            UnifyPropertyGroup(group.ToList(), graph, ctx, plan);
        }
    }

    private static void UnifyPropertyGroup(
        List<(TypeSymbol Type, Model.Symbols.MemberSymbols.PropertySymbol Property)> group,
        SymbolGraph graph,
        BuildContext ctx,
        PropertyOverridePlan plan)
    {
        if (group.Count == 0)
            return;

        // Collect TypeScript type strings for each property in the group
        // Key: TypeScript type string, Value: count (for deduplication)
        var typeStringCounts = new Dictionary<string, int>();

        foreach (var (declType, prop) in group)
        {
            // Use alias-centric type resolution (forValuePosition: false)
            // This ensures we get the same type strings that emission will use
            var resolver = new TypeNameResolver(ctx, graph, importPlan: null, declType.Namespace);
            var tsType = TypeRefPrinter.Print(
                prop.PropertyType,
                resolver,
                ctx,
                forValuePosition: false);

            if (!typeStringCounts.ContainsKey(tsType))
                typeStringCounts[tsType] = 0;
            typeStringCounts[tsType]++;
        }

        // If all properties in the group have the same TypeScript type, no unification needed
        if (typeStringCounts.Count <= 1)
            return;

        // SAFETY: Skip unification if any override type references generic parameters.
        // Generic unions like "DbContextOptions | DbContextOptions_1<TContext>" are invalid
        // because the generic parameters come from different declaring scopes.
        if (group.Any((it) => ContainsGenericParameters(it.Property.PropertyType)))
            return;

        // `unknown` dominates unions in TypeScript: `T | unknown` is semantically just `unknown`.
        // When property override unification includes `unknown`, we MUST collapse the union to
        // `unknown` to avoid:
        // - noisy / unstable "hint" unions in generated .d.ts
        // - invalid output when a unified union mentions a derived-only type name that is out of
        //   scope in the base namespace module (because union override strings are injected after
        //   import planning).
        var unionType = typeStringCounts.Keys.Any(ContainsTopLevelUnknown)
            ? "unknown"
            // Create union type from all distinct TypeScript types (sorted for deterministic output)
            : string.Join(" | ", typeStringCounts.Keys.OrderBy(s => s));

        // Record which CLR types appear in the unified override type so import planning can bring them
        // into scope in every namespace where we apply this override string.
        var referencedClrTypes = new HashSet<string>(System.StringComparer.Ordinal);
        if (unionType != "unknown")
        {
            foreach (var (_, prop) in group)
            {
                CollectReferencedClrTypes(prop.PropertyType, referencedClrTypes);
            }
        }

        ctx.Log("PropertyOverrideUnifier",
            $"Property '{group[0].Property.ClrName}' has {typeStringCounts.Count} different types across hierarchy: {unionType}");

        // Record this union type for ALL properties in the group
        // This ensures base and derived all use the same type string
        foreach (var (declType, prop) in group)
        {
            var key = (declType.StableId.ToString(), prop.StableId.ToString());
            plan.PropertyTypeOverrides[key] = unionType;
            plan.PropertyOverrideReferencedClrTypes[key] = referencedClrTypes;
        }
    }

    private static bool ContainsTopLevelUnknown(string tsType)
    {
        // TypeRefPrinter uses "unknown" and formats unions with " | " separators.
        // We only treat `unknown` as a top-level union member (not inside generics/tuples/etc).
        return tsType == "unknown"
            || tsType.StartsWith("unknown | ", System.StringComparison.Ordinal)
            || tsType.EndsWith(" | unknown", System.StringComparison.Ordinal)
            || tsType.Contains(" | unknown | ", System.StringComparison.Ordinal);
    }

    private static void CollectReferencedClrTypes(TypeReference typeRef, HashSet<string> collected)
    {
        switch (typeRef.Kind)
        {
            case TypeReferenceKind.Named:
            {
                var named = (NamedTypeReference)typeRef;
                if (!TypeMap.TryMapBuiltin(named.FullName, out _))
                {
                    collected.Add(named.FullName);
                }

                foreach (var arg in named.TypeArguments)
                {
                    CollectReferencedClrTypes(arg, collected);
                }
                break;
            }
            case TypeReferenceKind.Nested:
            {
                var nested = (NestedTypeReference)typeRef;
                CollectReferencedClrTypes(nested.FullReference, collected);
                break;
            }
            case TypeReferenceKind.Array:
                CollectReferencedClrTypes(((ArrayTypeReference)typeRef).ElementType, collected);
                break;
            case TypeReferenceKind.Pointer:
                CollectReferencedClrTypes(((PointerTypeReference)typeRef).PointeeType, collected);
                break;
            case TypeReferenceKind.ByRef:
                CollectReferencedClrTypes(((ByRefTypeReference)typeRef).ReferencedType, collected);
                break;
            case TypeReferenceKind.GenericParameter:
            case TypeReferenceKind.Placeholder:
            default:
                break;
        }
    }

    private static bool ContainsGenericParameters(TypeReference typeRef)
    {
        return typeRef.Kind switch
        {
            TypeReferenceKind.GenericParameter => true,
            TypeReferenceKind.Array => ContainsGenericParameters(((ArrayTypeReference)typeRef).ElementType),
            TypeReferenceKind.Pointer => ContainsGenericParameters(((PointerTypeReference)typeRef).PointeeType),
            TypeReferenceKind.ByRef => ContainsGenericParameters(((ByRefTypeReference)typeRef).ReferencedType),
            TypeReferenceKind.Nested => ContainsGenericParameters(((NestedTypeReference)typeRef).FullReference),
            TypeReferenceKind.Named => ((NamedTypeReference)typeRef).TypeArguments.Any(ContainsGenericParameters),
            TypeReferenceKind.Placeholder => false,
            _ => false
        };
    }

    /// <summary>
    /// Gets the full inheritance hierarchy for a type, from most derived to most base.
    /// Returns: [type, base, base.base, ..., Object]
    /// </summary>
    private static IEnumerable<TypeSymbol> GetHierarchy(TypeSymbol type, SymbolGraph graph)
    {
        var current = type;
        while (current != null)
        {
            yield return current;
            current = ResolveBase(current, graph);
        }
    }

    /// <summary>
    /// Resolves the base type symbol from a TypeReference.
    /// Returns null if no base or base not found in graph.
    /// </summary>
    private static TypeSymbol? ResolveBase(TypeSymbol type, SymbolGraph graph)
    {
        if (type.BaseType == null)
            return null;

        // BaseType is a TypeReference - need to resolve to TypeSymbol
        if (type.BaseType is not NamedTypeReference named)
            return null;

        // FIX: TypeIndex keys use the actual assembly where types are defined,
        // but BaseType references may use forwarding/facade assembly names.
        // Search by ClrFullName instead of exact StableId match.
        var baseType = graph.TypeIndex.Values
            .FirstOrDefault(t => t.ClrFullName == named.FullName);

        return baseType;
    }
}
