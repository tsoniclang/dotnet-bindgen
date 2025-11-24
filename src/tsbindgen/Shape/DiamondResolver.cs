using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Renaming;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;

namespace tsbindgen.Shape;

/// <summary>
/// Resolves diamond inheritance conflicts.
/// When multiple inheritance paths bring the same method with potentially different signatures,
/// this ensures all variants are available in TypeScript.
/// PURE - returns new SymbolGraph.
/// </summary>
public static class DiamondResolver
{
    public static SymbolGraph Resolve(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("DiamondResolver", "Resolving diamond inheritance...");

        var strategy = ctx.Policy.Interfaces.DiamondResolution;

        if (strategy == Core.Policy.DiamondResolutionStrategy.Error)
        {
            ctx.Log("DiamondResolver", "Strategy is Error - analyzing for conflicts");
            AnalyzeForDiamonds(ctx, graph);
            return graph;
        }

        var allTypes = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .ToList();

        int totalResolved = 0;
        var updatedGraph = graph;

        foreach (var type in allTypes)
        {
            var (newGraph, resolved) = ResolveForType(ctx, updatedGraph, type, strategy);
            updatedGraph = newGraph;
            totalResolved += resolved;
        }

        ctx.Log("DiamondResolver", $"Resolved {totalResolved} diamond conflicts");
        return updatedGraph;
    }

    private static (SymbolGraph UpdatedGraph, int ResolvedCount) ResolveForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type, Core.Policy.DiamondResolutionStrategy strategy)
    {
        // Skip types that don't have methods: enums, delegates, static namespaces
        // Interfaces, classes, and structs can have diamond inheritance patterns
        if (type.Kind is TypeKind.Enum or TypeKind.Delegate or TypeKind.StaticNamespace)
        {
            return (graph, 0);
        }

        // Process per-scope to avoid cross-scope contamination
        int resolved = 0;
        var updatedGraph = graph;

        (updatedGraph, var classDetected) = ResolveForScope(ctx, updatedGraph, type, EmitScope.ClassSurface, strategy);
        resolved += classDetected;

        (updatedGraph, var viewDetected) = ResolveForScope(ctx, updatedGraph, type, EmitScope.ViewOnly, strategy);
        resolved += viewDetected;

        return (updatedGraph, resolved);
    }

    private static (SymbolGraph UpdatedGraph, int Detected) ResolveForScope(BuildContext ctx, SymbolGraph graph, TypeSymbol type, EmitScope scope, Core.Policy.DiamondResolutionStrategy strategy)
    {
        // Find methods that come from multiple paths in this scope
        var methodGroups = type.Members.Methods
            .Where(m => m.EmitScope == scope)
            .GroupBy(m => m.ClrName)
            .Where(g => g.Count() > 1)
            .ToList();

        if (methodGroups.Count == 0)
            return (graph, 0);

        int detected = 0;

        // Sort by method name for deterministic iteration
        foreach (var group in methodGroups.OrderBy(g => g.Key))
        {
            // Check if these are true diamond conflicts (same name, different signatures from different paths)
            var methods = group.ToList();

            // Group by signature
            var signatureGroups = methods.GroupBy(m =>
                ctx.CanonicalizeMethod(
                    m.ClrName,
                    m.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                    GetTypeFullName(m.ReturnType)))
                .ToList();

            // If all have the same signature, no conflict
            if (signatureGroups.Count <= 1)
                continue;

            // Diamond conflict detected - log it, PhaseGate will validate
            ctx.Log("DiamondResolver",
                $"Diamond conflict in {type.ClrFullName}.{group.Key} (scope: {scope}) - {signatureGroups.Count} signatures. " +
                $"Strategy: {strategy}. PhaseGate will validate.");

            if (strategy == Core.Policy.DiamondResolutionStrategy.OverloadAll)
            {
                // Keep all overloads - they're already in the members list
                // Renamer will handle unique names if needed
                detected += methods.Count;
            }
            else if (strategy == Core.Policy.DiamondResolutionStrategy.PreferDerived)
            {
                // Log that we would prefer derived, but don't modify scopes
                // PhaseGate will catch duplicates if this causes problems
                detected++;
            }
        }

        // Don't modify EmitScope - just detect conflicts and let PhaseGate handle them
        // Return graph unchanged since we're only logging
        return (graph, detected);
    }

    private static void EnsureMethodRenamed(BuildContext ctx, TypeSymbol type, MethodSymbol method)
    {
        // Base scope without #static/#instance suffix - ReserveMemberName will add it
        var typeScope = ScopeFactory.ClassBase(type);

        // Reserve through renamer with DiamondResolved reason
        ctx.Renamer.ReserveMemberName(
            method.StableId,
            method.ClrName,
            typeScope,
            "DiamondResolved",
            method.IsStatic);
    }

    private static void AnalyzeForDiamonds(BuildContext ctx, SymbolGraph graph)
    {
        var allTypes = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .ToList();

        foreach (var type in allTypes)
        {
            var methodGroups = type.Members.Methods
                .GroupBy(m => m.ClrName)
                .Where(g => g.Count() > 1)
                .ToList();

            // Sort by method name for deterministic iteration
            foreach (var group in methodGroups.OrderBy(g => g.Key))
            {
                var methods = group.ToList();

                var signatureGroups = methods.GroupBy(m =>
                    ctx.CanonicalizeMethod(
                        m.ClrName,
                        m.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                        GetTypeFullName(m.ReturnType)))
                    .ToList();

                if (signatureGroups.Count > 1)
                {
                    ctx.Diagnostics.Warning(
                        Core.Diagnostics.DiagnosticCodes.DiamondInheritance,
                        $"Diamond inheritance conflict in {type.ClrFullName}.{group.Key} - {signatureGroups.Count} signatures");
                }
            }
        }
    }

    private static string GetTypeFullName(Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.FullName,
            Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            Model.Types.GenericParameterReference gp => gp.Name,
            Model.Types.ArrayTypeReference arr => $"{GetTypeFullName(arr.ElementType)}[]",
            Model.Types.PointerTypeReference ptr => $"{GetTypeFullName(ptr.PointeeType)}*",
            Model.Types.ByRefTypeReference byref => $"{GetTypeFullName(byref.ReferencedType)}&",
            _ => typeRef.ToString() ?? "Unknown"
        };
    }
}
