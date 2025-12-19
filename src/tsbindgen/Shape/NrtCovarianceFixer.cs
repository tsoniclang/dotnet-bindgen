using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Model.Types;

namespace tsbindgen.Shape;

/// <summary>
/// Fixes NRT covariance issues where derived class has nullable return type
/// but base class has non-nullable return type.
///
/// Problem (TS2430):
///   base:    createNavigator(): XPathNavigator
///   derived: createNavigator(): XPathNavigator | undefined
///
/// Solution:
///   Strip the NRT annotation from derived to match base:
///   derived: createNavigator(): XPathNavigator
///
/// This is correct because if the base says non-nullable, the derived
/// must also be non-nullable (Liskov substitution).
/// </summary>
public static class NrtCovarianceFixer
{
    /// <summary>
    /// Find all methods where derived has nullable return but base has non-nullable,
    /// and strip the NRT from derived's return type.
    /// </summary>
    public static SymbolGraph Fix(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("NrtCovarianceFixer", "Checking for NRT covariance issues...");

        var fixCount = 0;
        var updatedGraph = graph;

        // Process all classes with base types
        var classesWithBase = graph.TypeIndex.Values
            .Where(t => t.Kind == TypeKind.Class && t.BaseType != null)
            .ToList();

        foreach (var derivedClass in classesWithBase)
        {
            var (newGraph, count) = FixClassMethods(ctx, updatedGraph, derivedClass);
            updatedGraph = newGraph;
            fixCount += count;
        }

        ctx.Log("NrtCovarianceFixer", $"Fixed {fixCount} NRT covariance issues");
        return updatedGraph;
    }

    private static (SymbolGraph, int) FixClassMethods(BuildContext ctx, SymbolGraph graph, TypeSymbol derivedClass)
    {
        var methodsToFix = new List<MethodSymbol>();

        // Walk up the base class chain
        var baseRef = derivedClass.BaseType;
        while (baseRef != null)
        {
            var baseClass = FindType(graph, baseRef);
            if (baseClass == null)
                break;

            // Check each derived method against base methods
            foreach (var derivedMethod in derivedClass.Members.Methods
                .Where(m => !m.IsStatic && m.EmitScope == EmitScope.ClassSurface))
            {
                // Find matching base method (same name, same parameter types)
                var baseMethod = FindMatchingMethod(baseClass, derivedMethod);
                if (baseMethod == null)
                    continue;

                // Check if derived has nullable return but base has non-nullable
                if (HasNullableReturn(derivedMethod.ReturnType) && !HasNullableReturn(baseMethod.ReturnType))
                {
                    // Same CLR type, different nullability
                    if (GetClrTypeName(derivedMethod.ReturnType) == GetClrTypeName(baseMethod.ReturnType))
                    {
                        methodsToFix.Add(derivedMethod);
                        ctx.Log("NrtCovarianceFixer",
                            $"Fixing {derivedClass.ClrFullName}.{derivedMethod.ClrName}: " +
                            $"stripping nullable from return type to match base {baseClass.ClrFullName}");
                    }
                }
            }

            baseRef = baseClass.BaseType;
        }

        if (methodsToFix.Count == 0)
            return (graph, 0);

        // Update the graph with fixed methods
        var fixedMethodIds = methodsToFix.Select(m => m.StableId).ToHashSet();

        var updatedGraph = graph.WithUpdatedType(derivedClass.StableId.ToString(), type =>
        {
            var updatedMethods = type.Members.Methods.Select(m =>
            {
                if (!fixedMethodIds.Contains(m.StableId))
                    return m;

                // Strip NRT from return type
                var fixedReturnType = StripNullability(m.ReturnType);
                return m with { ReturnType = fixedReturnType };
            }).ToImmutableArray();

            return type with
            {
                Members = type.Members with { Methods = updatedMethods }
            };
        });

        return (updatedGraph, methodsToFix.Count);
    }

    private static MethodSymbol? FindMatchingMethod(TypeSymbol baseClass, MethodSymbol derivedMethod)
    {
        foreach (var baseMethod in baseClass.Members.Methods.Where(m => !m.IsStatic && m.ClrName == derivedMethod.ClrName))
        {
            // Check parameter count
            if (baseMethod.Parameters.Length != derivedMethod.Parameters.Length)
                continue;

            // Check parameter types match (ignoring NRT)
            var match = true;
            for (int i = 0; i < baseMethod.Parameters.Length; i++)
            {
                if (GetClrTypeName(baseMethod.Parameters[i].Type) != GetClrTypeName(derivedMethod.Parameters[i].Type))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return baseMethod;
        }

        return null;
    }

    private static TypeSymbol? FindType(SymbolGraph graph, TypeReference typeRef)
    {
        if (typeRef is NamedTypeReference named)
        {
            graph.TypeIndex.TryGetValue(named.FullName, out var type);
            return type;
        }
        return null;
    }

    private static bool HasNullableReturn(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.IsNullableReference,
            GenericParameterReference gp => gp.IsNullableReference,
            ArrayTypeReference arr => arr.IsNullableReference,
            _ => false
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

    private static TypeReference StripNullability(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named with { IsNullableReference = false },
            GenericParameterReference gp => gp with { IsNullableReference = false },
            ArrayTypeReference arr => arr with { IsNullableReference = false },
            _ => typeRef
        };
    }
}
