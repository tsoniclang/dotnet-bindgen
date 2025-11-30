using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Renaming;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Model.Types;

namespace tsbindgen.Shape;

/// <summary>
/// Ensures enumerator types have reset() on their class surface for structural typing compatibility.
///
/// Problem:
/// - Types like List_1_Enumerator implement IEnumerator.Reset() explicitly
/// - This makes Reset() a ViewOnly member (only accessible via As_IEnumerator())
/// - But TypeScript structural typing requires reset() on the class surface
///   for assignment: let e: IEnumerator = list.getEnumerator();
///
/// Solution:
/// - Find all types implementing IEnumerator or IEnumerator&lt;T&gt;
/// - If they have a ViewOnly Reset() method, promote it to ClassSurface
/// - If they don't have Reset() at all, synthesize one
///
/// Runs after StructuralConformance and ExplicitImplSynthesizer.
/// </summary>
public static class EnumeratorConformancePass
{
    private static readonly HashSet<string> EnumeratorInterfaces = new(StringComparer.Ordinal)
    {
        "System.Collections.IEnumerator",
        "System.Collections.Generic.IEnumerator`1"
    };

    public static SymbolGraph Run(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("EnumeratorConformance", "Promoting reset() to ClassSurface for enumerator types...");

        int promotedCount = 0;
        int synthesizedCount = 0;

        var updatedNamespaces = graph.Namespaces.Select(ns =>
        {
            var updatedTypes = ns.Types.Select(type =>
            {
                // Only process classes and structs
                if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct)
                    return type;

                // Check if type implements IEnumerator or IEnumerator<T>
                if (!ImplementsEnumerator(type))
                    return type;

                ctx.Log("EnumeratorConformance", $"Processing enumerator type: {type.ClrFullName}");

                // Look for existing Reset() method (may be ViewOnly from explicit impl)
                var resetMethod = type.Members.Methods
                    .FirstOrDefault(m =>
                        m.ClrName == "Reset" &&
                        !m.IsStatic &&
                        m.Parameters.Length == 0);

                if (resetMethod != null)
                {
                    // Found Reset() - check if it needs promotion
                    if (resetMethod.EmitScope == EmitScope.ViewOnly)
                    {
                        ctx.Log("EnumeratorConformance", $"  Promoting ViewOnly Reset() to ClassSurface");

                        // Promote to ClassSurface by creating updated method
                        // Clear SourceInterface since ClassSurface members shouldn't have it
                        var promotedMethod = resetMethod with
                        {
                            EmitScope = EmitScope.ClassSurface,
                            SourceInterface = null
                        };

                        // Replace the method in the type
                        var updatedMethods = type.Members.Methods
                            .Select(m => m.StableId.Equals(resetMethod.StableId) ? promotedMethod : m)
                            .ToImmutableArray();

                        promotedCount++;
                        return type with
                        {
                            Members = type.Members with { Methods = updatedMethods }
                        };
                    }
                    else
                    {
                        ctx.Log("EnumeratorConformance", $"  Reset() already on ClassSurface");
                        return type; // Already on class surface
                    }
                }
                else
                {
                    // No Reset() found - synthesize one
                    ctx.Log("EnumeratorConformance", $"  Synthesizing Reset() method");

                    var assemblyName = type.StableId.AssemblyName;
                    var resetStableId = new MemberStableId
                    {
                        AssemblyName = assemblyName,
                        DeclaringClrFullName = type.ClrFullName,
                        MemberName = "Reset",
                        CanonicalSignature = ctx.CanonicalizeMethod("Reset", new List<string>(), "System.Void")
                    };

                    var synthesizedReset = new MethodSymbol
                    {
                        StableId = resetStableId,
                        ClrName = "Reset",
                        TsEmitName = "", // Will be set by NameReservation pass
                        ReturnType = new NamedTypeReference
                        {
                            AssemblyName = "System.Private.CoreLib",
                            FullName = "System.Void",
                            Namespace = "System",
                            Name = "Void",
                            Arity = 0,
                            TypeArguments = ImmutableArray<TypeReference>.Empty,
                            IsValueType = true
                        },
                        Parameters = ImmutableArray<ParameterSymbol>.Empty,
                        GenericParameters = ImmutableArray<GenericParameterSymbol>.Empty,
                        IsStatic = false,
                        IsAbstract = false,
                        IsVirtual = true,
                        IsOverride = false,
                        IsSealed = false,
                        IsNew = false,
                        Visibility = Visibility.Public,
                        Provenance = MemberProvenance.Synthesized,
                        EmitScope = EmitScope.ClassSurface
                        // SourceInterface not set - ClassSurface members shouldn't have it
                    };

                    synthesizedCount++;
                    return type.WithAddedMethods(new[] { synthesizedReset });
                }
            }).ToImmutableArray();

            return ns with { Types = updatedTypes };
        }).ToImmutableArray();

        ctx.Log("EnumeratorConformance", $"Promoted {promotedCount} Reset() methods, synthesized {synthesizedCount}");

        return (graph with { Namespaces = updatedNamespaces }).WithIndices();
    }

    /// <summary>
    /// Check if a type implements IEnumerator or IEnumerator&lt;T&gt;.
    /// </summary>
    private static bool ImplementsEnumerator(TypeSymbol type)
    {
        return type.Interfaces.Any(iface =>
        {
            var fullName = GetTypeFullName(iface);

            // Check for exact match or generic version
            return EnumeratorInterfaces.Contains(fullName) ||
                   fullName.StartsWith("System.Collections.Generic.IEnumerator`1", StringComparison.Ordinal);
        });
    }

    private static string GetTypeFullName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            GenericParameterReference gp => gp.Name,
            ArrayTypeReference arr => $"{GetTypeFullName(arr.ElementType)}[]",
            _ => typeRef.ToString() ?? "Unknown"
        };
    }
}
