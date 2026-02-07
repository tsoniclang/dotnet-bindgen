using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Emit;
using tsbindgen.Emit.Printers;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Model.Types;

namespace tsbindgen.Plan;

/// <summary>
/// Analyzes which interfaces are safe to extend in TypeScript declaration merging.
///
/// This is different from "implements" conformance:
/// - "implements" requires class to have all interface members with compatible signatures
/// - "extends" (declaration merge) requires no conflicting member signatures
///
/// TS2430: Interface incorrectly extends interface (member signature mismatch)
/// TS2320: Cannot simultaneously extend types (multiple bases have conflicting members)
///
/// MUST use exact printed TS surface (TypeRefPrinter output) for accuracy.
/// </summary>
public static class SafeToExtendAnalyzer
{
    /// <summary>
    /// Result of safe-to-extend analysis for a single type.
    /// </summary>
    public record SafeToExtendResult(
        IReadOnlyList<TypeReference> AssignableInterfaces,
        IReadOnlyList<(TypeReference Interface, string Reason)> NonAssignableInterfaces);

    /// <summary>
    /// Analyzes all types and produces safe-to-extend mappings.
    /// </summary>
    public static Dictionary<string, SafeToExtendResult> Analyze(
        BuildContext ctx,
        SymbolGraph graph,
        TypeNameResolver resolver)
    {
        ctx.Log("SafeToExtendAnalyzer", "Analyzing safe-to-extend interfaces...");

        var results = new Dictionary<string, SafeToExtendResult>();
        var totalAssignable = 0;
        var totalNonAssignable = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Analyze classes, structs, and interfaces
                if (type.Kind != TypeKind.Class &&
                    type.Kind != TypeKind.Struct &&
                    type.Kind != TypeKind.Interface)
                    continue;

                if (type.Interfaces.Length == 0)
                    continue;

                var result = AnalyzeType(ctx, graph, resolver, type);
                results[type.StableId.ToString()] = result;

                totalAssignable += result.AssignableInterfaces.Count;
                totalNonAssignable += result.NonAssignableInterfaces.Count;
            }
        }

        ctx.Log("SafeToExtendAnalyzer",
            $"Analysis complete: {totalAssignable} assignable, {totalNonAssignable} non-assignable interfaces");

        return results;
    }

    /// <summary>
    /// Analyzes a single type to determine which interfaces are safe to extend.
    /// </summary>
    private static SafeToExtendResult AnalyzeType(
        BuildContext ctx,
        SymbolGraph graph,
        TypeNameResolver resolver,
        TypeSymbol type)
    {
        var assignable = new List<TypeReference>();
        var nonAssignable = new List<(TypeReference, string)>();

        // Step 1: Build the type's full member signature map (including inherited members)
        var typeSurface = BuildMemberSignatureMap(type, resolver, ctx, graph);

        // Step 2: Filter to interfaces that exist in the graph
        var candidateInterfaces = type.Interfaces
            .Where(i => IsInterfaceAvailable(ctx, i, graph))
            // Filter known conflicting non-generic System.Collections interfaces.
            // These are almost always explicitly implemented in CLR and cannot be modeled
            // as merged extends in TS without TS2320/TS2430.
            .Where(i => !IsConflictingNonGenericInterface(i))
            .OrderBy(i => GetTypeFullName(i)) // Deterministic ordering
            .ToList();

        // Prefer generic variants when both generic and non-generic versions exist.
        // Example: IQueryable`1 extends IQueryable; extending both is redundant and can conflict.
        candidateInterfaces = PreferGenericInterfaceVariants(candidateInterfaces);

        // Step 2.5: Build transitive extends set - interfaces that are already covered
        // by other interfaces in our candidate list (to avoid redundant extends)
        var transitivelyExtended = BuildTransitiveExtendsSet(candidateInterfaces, graph);

        // Step 3: Check each candidate interface for conflicts
        // Also track accumulated base signatures to detect TS2320 (multi-base conflicts)
        var accumulatedBaseSignatures = new Dictionary<string, (string Signature, string SourceInterface)>();

        foreach (var ifaceRef in candidateInterfaces)
        {
            var ifaceFullName = GetTypeFullName(ifaceRef);

            // Skip interfaces that are transitively extended by other candidates
            // This prevents TS2320 when extending both IEnumerable<T> and IEnumerable
            // (IEnumerable<T> already extends IEnumerable via declaration merging)
            if (transitivelyExtended.Contains(ifaceFullName))
            {
                nonAssignable.Add((ifaceRef, "Transitively extended by another interface"));
                continue;
            }

            var ifaceSymbol = FindInterface(graph, ifaceRef);

            if (ifaceSymbol == null)
            {
                // External interface (filtered out by --lib):
                // We cannot analyze member conflicts, but we still want assignability for common CLR interfaces
                // like IQueryable<T>/IEnumerable<T>. We apply conservative filtering above to avoid known
                // TS2320/TS2430 hazards (non-generic System.Collections interfaces, redundant non-generic variants).
                assignable.Add(ifaceRef);
                continue;
            }

            // Build signature map for this interface
            var ifaceSignatures = BuildInterfaceMemberSignatureMap(ifaceSymbol, ifaceRef, resolver, ctx, graph);

            // Check for conflicts
            var conflict = CheckForConflicts(
                typeSurface,
                ifaceSignatures,
                accumulatedBaseSignatures,
                ifaceFullName);

            if (conflict != null)
            {
                nonAssignable.Add((ifaceRef, conflict));
                ctx.Log("SafeToExtendAnalyzer",
                    $"  {type.ClrFullName} cannot extend {ifaceFullName}: {conflict}");
            }
            else
            {
                assignable.Add(ifaceRef);

                // Add this interface's signatures to accumulated set
                foreach (var (key, sig) in ifaceSignatures)
                {
                    if (!accumulatedBaseSignatures.ContainsKey(key))
                    {
                        accumulatedBaseSignatures[key] = (sig, ifaceFullName);
                    }
                }
            }
        }

        return new SafeToExtendResult(
            assignable.ToImmutableArray(),
            nonAssignable.ToImmutableArray());
    }

    /// <summary>
    /// Builds a set of interface full names that are transitively extended by other interfaces
    /// in the candidate list. These should be skipped to avoid redundant extends that cause TS2320.
    ///
    /// Example: If candidates include IEnumerable_1&lt;T&gt; and IEnumerable, and IEnumerable_1 already
    /// extends IEnumerable (via declaration merging), then IEnumerable is in the transitive set
    /// and should not be directly extended.
    /// </summary>
    private static HashSet<string> BuildTransitiveExtendsSet(
        IReadOnlyList<TypeReference> candidates,
        SymbolGraph graph)
    {
        var transitiveSet = new HashSet<string>();
        var candidateFullNames = candidates.Select(GetTypeFullName).ToHashSet();

        foreach (var ifaceRef in candidates)
        {
            var ifaceSymbol = FindInterface(graph, ifaceRef);
            if (ifaceSymbol == null) continue;

            // Get all interfaces that this interface extends
            foreach (var baseIfaceRef in ifaceSymbol.Interfaces)
            {
                var baseFullName = GetTypeFullName(baseIfaceRef);

                // If a base interface is also in our candidate list, mark it as transitively extended
                if (candidateFullNames.Contains(baseFullName))
                {
                    transitiveSet.Add(baseFullName);
                }

                // Also check if the base is in the graph and has further bases
                // This handles multi-level transitivity
                var baseSymbol = FindInterface(graph, baseIfaceRef);
                if (baseSymbol != null)
                {
                    CollectTransitiveBases(baseSymbol, candidateFullNames, transitiveSet, graph);
                }
            }
        }

        return transitiveSet;
    }

    /// <summary>
    /// Recursively collects transitive base interfaces.
    /// </summary>
    private static void CollectTransitiveBases(
        TypeSymbol ifaceSymbol,
        HashSet<string> candidateFullNames,
        HashSet<string> transitiveSet,
        SymbolGraph graph)
    {
        foreach (var baseIfaceRef in ifaceSymbol.Interfaces)
        {
            var baseFullName = GetTypeFullName(baseIfaceRef);
            if (candidateFullNames.Contains(baseFullName))
            {
                transitiveSet.Add(baseFullName);
            }

            var baseSymbol = FindInterface(graph, baseIfaceRef);
            if (baseSymbol != null)
            {
                CollectTransitiveBases(baseSymbol, candidateFullNames, transitiveSet, graph);
            }
        }
    }

    /// <summary>
    /// Builds a signature map for a type's full surface (own members + inherited from base classes).
    /// Key: "memberName:kind" (e.g., "Add:method", "Count:property")
    /// Value: Normalized TS signature (sorted for overloads to ensure order-independence)
    /// </summary>
    private static Dictionary<string, string> BuildMemberSignatureMap(
        TypeSymbol type,
        TypeNameResolver resolver,
        BuildContext ctx,
        SymbolGraph graph)
    {
        // Collect overloads as lists first, then normalize
        var methodOverloads = new Dictionary<string, List<string>>();
        var propertySignatures = new Dictionary<string, string>();

        // First, collect signatures from base class hierarchy (bottom-up, so derived overrides base)
        CollectBaseClassSignatures(type.BaseType, resolver, ctx, graph, methodOverloads, propertySignatures);

        // Then add the type's own ClassSurface members (override base if present)
        // Methods - only ClassSurface, use TsEmitName for accurate matching with emitted output
        foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface && !m.IsStatic))
        {
            // Use TsEmitName to match what's actually emitted, not ClrName
            var emitName = string.IsNullOrEmpty(method.TsEmitName) ? method.ClrName : method.TsEmitName;
            var key = $"{emitName}:method";
            var sig = BuildMethodSignature(method, resolver, ctx);

            if (!methodOverloads.TryGetValue(key, out var list))
            {
                list = new List<string>();
                methodOverloads[key] = list;
            }
            list.Add(sig);
        }

        // Properties - only ClassSurface, use TsEmitName for accurate matching
        foreach (var prop in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface && !p.IsStatic))
        {
            var emitName = string.IsNullOrEmpty(prop.TsEmitName) ? prop.ClrName : prop.TsEmitName;
            var key = $"{emitName}:property";
            var sig = BuildPropertySignature(prop, resolver, ctx);
            propertySignatures[key] = sig;
        }

        // Normalize: sort overloads and join with delimiter for order-independent comparison
        var signatures = new Dictionary<string, string>();
        foreach (var (key, overloads) in methodOverloads)
        {
            overloads.Sort(StringComparer.Ordinal);
            signatures[key] = string.Join("|", overloads);
        }
        foreach (var (key, sig) in propertySignatures)
        {
            signatures[key] = sig;
        }

        return signatures;
    }

    /// <summary>
    /// Recursively collects member signatures from base class hierarchy.
    /// Uses list-based overload collection for order-independent comparison.
    /// </summary>
    private static void CollectBaseClassSignatures(
        TypeReference? baseTypeRef,
        TypeNameResolver resolver,
        BuildContext ctx,
        SymbolGraph graph,
        Dictionary<string, List<string>> methodOverloads,
        Dictionary<string, string> propertySignatures)
    {
        if (baseTypeRef == null) return;

        var baseFullName = GetTypeFullName(baseTypeRef);

        // Find the base type in the graph
        var baseSymbol = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == baseFullName);

        if (baseSymbol == null) return;

        // First recurse to grandparent (so child signatures override parent)
        CollectBaseClassSignatures(baseSymbol.BaseType, resolver, ctx, graph, methodOverloads, propertySignatures);

        // Build substitution map for generic base type
        var substitutionMap = BuildBaseTypeSubstitutionMap(baseSymbol, baseTypeRef);

        // Add base's ClassSurface members (with generic substitution), use TsEmitName
        foreach (var method in baseSymbol.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface && !m.IsStatic))
        {
            var emitName = string.IsNullOrEmpty(method.TsEmitName) ? method.ClrName : method.TsEmitName;
            var key = $"{emitName}:method";
            var sig = BuildMethodSignature(method, resolver, ctx, substitutionMap);

            if (!methodOverloads.TryGetValue(key, out var list))
            {
                list = new List<string>();
                methodOverloads[key] = list;
            }
            list.Add(sig);
        }

        foreach (var prop in baseSymbol.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface && !p.IsStatic))
        {
            var emitName = string.IsNullOrEmpty(prop.TsEmitName) ? prop.ClrName : prop.TsEmitName;
            var key = $"{emitName}:property";
            var sig = BuildPropertySignature(prop, resolver, ctx, substitutionMap);
            propertySignatures[key] = sig;
        }
    }

    /// <summary>
    /// Builds substitution map for generic base type reference.
    /// </summary>
    private static Dictionary<string, TypeReference> BuildBaseTypeSubstitutionMap(
        TypeSymbol baseSymbol,
        TypeReference baseTypeRef)
    {
        var map = new Dictionary<string, TypeReference>();

        if (baseTypeRef is not NamedTypeReference namedRef)
            return map;

        if (namedRef.TypeArguments.Count == 0)
            return map;

        if (baseSymbol.GenericParameters.Length != namedRef.TypeArguments.Count)
            return map;

        for (int i = 0; i < baseSymbol.GenericParameters.Length; i++)
        {
            var param = baseSymbol.GenericParameters[i];
            var arg = namedRef.TypeArguments[i];
            map[param.Name] = arg;
        }

        return map;
    }

    /// <summary>
    /// Builds a signature map for an interface's members.
    /// Applies generic substitution based on the type reference.
    /// Uses TsEmitName consistently (same as type surface) for accurate matching.
    /// Uses sorted overload lists for order-independent comparison.
    ///
    /// CRITICAL: Must include inherited members from parent interfaces!
    /// Example: IProducerConsumerCollection_1 extends ICollection which has CopyTo(Array, int)
    /// </summary>
    private static Dictionary<string, string> BuildInterfaceMemberSignatureMap(
        TypeSymbol ifaceSymbol,
        TypeReference ifaceRef,
        TypeNameResolver resolver,
        BuildContext ctx,
        SymbolGraph graph)
    {
        // Collect overloads as lists first, then normalize
        var methodOverloads = new Dictionary<string, List<string>>();
        var propertySignatures = new Dictionary<string, string>();

        // Build substitution map for generic parameters
        var substitutionMap = BuildSubstitutionMap(ifaceSymbol, ifaceRef);

        // First, collect inherited members from parent interfaces (bottom-up)
        CollectParentInterfaceSignatures(
            ifaceSymbol.Interfaces,
            resolver,
            ctx,
            graph,
            methodOverloads,
            propertySignatures,
            substitutionMap);

        // Then add this interface's own members (overrides parent if present)
        // Methods - use TsEmitName for consistency with type surface
        foreach (var method in ifaceSymbol.Members.Methods)
        {
            // FIX #2: Use TsEmitName for interface members, matching type surface behavior
            var emitName = string.IsNullOrEmpty(method.TsEmitName) ? method.ClrName : method.TsEmitName;
            var key = $"{emitName}:method";
            var sig = BuildMethodSignature(method, resolver, ctx, substitutionMap);

            if (!methodOverloads.TryGetValue(key, out var list))
            {
                list = new List<string>();
                methodOverloads[key] = list;
            }
            list.Add(sig);
        }

        // Properties - use TsEmitName for consistency
        foreach (var prop in ifaceSymbol.Members.Properties)
        {
            var emitName = string.IsNullOrEmpty(prop.TsEmitName) ? prop.ClrName : prop.TsEmitName;
            var key = $"{emitName}:property";
            var sig = BuildPropertySignature(prop, resolver, ctx, substitutionMap);
            propertySignatures[key] = sig;
        }

        // Normalize: sort overloads and join with delimiter for order-independent comparison
        var signatures = new Dictionary<string, string>();
        foreach (var (key, overloads) in methodOverloads)
        {
            overloads.Sort(StringComparer.Ordinal);
            signatures[key] = string.Join("|", overloads);
        }
        foreach (var (key, sig) in propertySignatures)
        {
            signatures[key] = sig;
        }

        return signatures;
    }

    /// <summary>
    /// Recursively collects member signatures from parent interfaces.
    /// This ensures we see all inherited members (e.g., ICollection.CopyTo(Array, int)
    /// that IProducerConsumerCollection_1 inherits).
    /// </summary>
    private static void CollectParentInterfaceSignatures(
        IReadOnlyList<TypeReference> parentInterfaces,
        TypeNameResolver resolver,
        BuildContext ctx,
        SymbolGraph graph,
        Dictionary<string, List<string>> methodOverloads,
        Dictionary<string, string> propertySignatures,
        Dictionary<string, TypeReference>? outerSubstitutionMap)
    {
        foreach (var parentIfaceRef in parentInterfaces)
        {
            var parentFullName = GetTypeFullName(parentIfaceRef);

            // Find the parent interface in the graph
            var parentSymbol = graph.Namespaces
                .SelectMany(ns => ns.Types)
                .FirstOrDefault(t => t.ClrFullName == parentFullName && t.Kind == TypeKind.Interface);

            if (parentSymbol == null) continue;

            // Build substitution map for this parent interface
            // Compose with outer map if needed
            var parentSubMap = BuildSubstitutionMap(parentSymbol, parentIfaceRef);

            // Apply outer substitutions to the parent's substitution map
            var composedMap = ComposeSubstitutionMaps(outerSubstitutionMap, parentSubMap);

            // Recurse to grandparent interfaces first
            CollectParentInterfaceSignatures(
                parentSymbol.Interfaces,
                resolver,
                ctx,
                graph,
                methodOverloads,
                propertySignatures,
                composedMap);

            // Collect this parent's methods
            foreach (var method in parentSymbol.Members.Methods)
            {
                var emitName = string.IsNullOrEmpty(method.TsEmitName) ? method.ClrName : method.TsEmitName;
                var key = $"{emitName}:method";
                var sig = BuildMethodSignature(method, resolver, ctx, composedMap);

                if (!methodOverloads.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    methodOverloads[key] = list;
                }
                list.Add(sig);
            }

            // Collect this parent's properties
            foreach (var prop in parentSymbol.Members.Properties)
            {
                var emitName = string.IsNullOrEmpty(prop.TsEmitName) ? prop.ClrName : prop.TsEmitName;
                var key = $"{emitName}:property";
                var sig = BuildPropertySignature(prop, resolver, ctx, composedMap);
                propertySignatures[key] = sig;
            }
        }
    }

    /// <summary>
    /// Composes two substitution maps: applies outer substitutions to inner map values.
    /// Example: If outer has T→int and inner has U→T, result has U→int.
    /// </summary>
    private static Dictionary<string, TypeReference>? ComposeSubstitutionMaps(
        Dictionary<string, TypeReference>? outer,
        Dictionary<string, TypeReference>? inner)
    {
        if (inner == null || inner.Count == 0) return outer;
        if (outer == null || outer.Count == 0) return inner;

        var composed = new Dictionary<string, TypeReference>(inner);

        // Apply outer substitutions to inner values
        foreach (var (key, value) in inner)
        {
            composed[key] = SubstituteTypeRef(value, outer);
        }

        // Also include outer mappings not in inner
        foreach (var (key, value) in outer)
        {
            if (!composed.ContainsKey(key))
            {
                composed[key] = value;
            }
        }

        return composed;
    }

    /// <summary>
    /// Builds method signature string for comparison.
    /// Format: "(param1Type, param2Type?, ...restType[]) => returnType"
    /// Includes parameter modifiers (optional, rest) for accurate TS signature matching.
    /// </summary>
    private static string BuildMethodSignature(
        MethodSymbol method,
        TypeNameResolver resolver,
        BuildContext ctx,
        Dictionary<string, TypeReference>? substitutionMap = null)
    {
        // FIX #3: Include parameter modifiers in signature
        var paramParts = method.Parameters.Select(p =>
        {
            var typeStr = PrintTypeWithSubstitution(p.Type, resolver, ctx, substitutionMap);

            // Encode parameter modifiers to match what TS sees
            if (p.IsParams)
            {
                // params T[] → ...T[] in TS (rest parameter)
                return $"...{typeStr}";
            }
            else if (p.HasDefaultValue)
            {
                // T = default → T? in TS (optional parameter)
                return $"{typeStr}?";
            }
            else
            {
                return typeStr;
            }
        }).ToList();

        var returnType = PrintTypeWithSubstitution(method.ReturnType, resolver, ctx, substitutionMap);

        return $"({string.Join(", ", paramParts)}) => {returnType}";
    }

    /// <summary>
    /// Builds property signature string for comparison.
    /// Format: "readonly? propertyType"
    /// </summary>
    private static string BuildPropertySignature(
        PropertySymbol prop,
        TypeNameResolver resolver,
        BuildContext ctx,
        Dictionary<string, TypeReference>? substitutionMap = null)
    {
        var typeStr = PrintTypeWithSubstitution(prop.PropertyType, resolver, ctx, substitutionMap);
        var prefix = prop.HasSetter ? "" : "readonly ";
        return $"{prefix}{typeStr}";
    }

    /// <summary>
    /// Prints a type reference with optional generic substitution.
    ///
    /// CRITICAL: When a generic parameter T is substituted with a primitive type (e.g., System.Char),
    /// the extends clause prints the interface with CLR type names:
    ///   extends IEnumerator_1$instance&lt;Char&gt;
    ///
    /// TypeScript sees T as Char (the CLR type name), not char (the primitive).
    /// So we must lift substituted primitives to their CLR names to match what tsc sees.
    /// </summary>
    private static string PrintTypeWithSubstitution(
        TypeReference typeRef,
        TypeNameResolver resolver,
        BuildContext ctx,
        Dictionary<string, TypeReference>? substitutionMap)
    {
        // Special case: generic parameter being substituted
        // When T gets substituted with a primitive (like System.Char), we need to
        // lift to CLR type name because that's what the extends clause uses.
        if (substitutionMap != null && typeRef is GenericParameterReference gp)
        {
            if (substitutionMap.TryGetValue(gp.Name, out var substituted))
            {
                // Print the substituted type
                var printed = TypeRefPrinter.Print(substituted, resolver, ctx);

                // Lift to CLR type name if it's a liftable primitive
                // This matches what TypeRefPrinter does for type arguments in the extends clause
                var clrName = PrimitiveLift.GetClrSimpleName(printed);
                return clrName ?? printed;
            }
        }

        // For non-generic-parameter cases, apply AST-level substitution and print
        var substitutedRef = substitutionMap != null
            ? SubstituteTypeRef(typeRef, substitutionMap)
            : typeRef;

        return TypeRefPrinter.Print(substitutedRef, resolver, ctx);
    }

    /// <summary>
    /// Recursively substitutes type references at AST level.
    /// This replaces generic parameters with their concrete type arguments.
    /// </summary>
    private static TypeReference SubstituteTypeRef(
        TypeReference typeRef,
        Dictionary<string, TypeReference> substitutionMap)
    {
        return typeRef switch
        {
            GenericParameterReference gp when substitutionMap.TryGetValue(gp.Name, out var sub) => sub,

            NamedTypeReference named when named.TypeArguments.Count > 0 =>
                named with
                {
                    TypeArguments = named.TypeArguments
                        .Select(arg => SubstituteTypeRef(arg, substitutionMap))
                        .ToImmutableArray()
                },

            ArrayTypeReference arr =>
                arr with { ElementType = SubstituteTypeRef(arr.ElementType, substitutionMap) },

            ByRefTypeReference byref =>
                byref with { ReferencedType = SubstituteTypeRef(byref.ReferencedType, substitutionMap) },

            PointerTypeReference ptr =>
                ptr with { PointeeType = SubstituteTypeRef(ptr.PointeeType, substitutionMap) },

            NestedTypeReference nested =>
                nested with { FullReference = (NamedTypeReference)SubstituteTypeRef(nested.FullReference, substitutionMap) },

            _ => typeRef
        };
    }

    /// <summary>
    /// Builds substitution map for generic interface instantiation.
    /// E.g., ICollection&lt;KeyValuePair&lt;TKey, TValue&gt;&gt; with IDictionary&lt;TKey, TValue&gt;
    /// </summary>
    private static Dictionary<string, TypeReference> BuildSubstitutionMap(
        TypeSymbol ifaceSymbol,
        TypeReference ifaceRef)
    {
        var map = new Dictionary<string, TypeReference>();

        if (ifaceRef is not NamedTypeReference namedRef)
            return map;

        if (namedRef.TypeArguments.Count == 0)
            return map;

        if (ifaceSymbol.GenericParameters.Length != namedRef.TypeArguments.Count)
            return map; // Arity mismatch - defensive

        for (int i = 0; i < ifaceSymbol.GenericParameters.Length; i++)
        {
            var param = ifaceSymbol.GenericParameters[i];
            var arg = namedRef.TypeArguments[i];
            map[param.Name] = arg;
        }

        return map;
    }

    /// <summary>
    /// Checks for conflicts between type surface, interface, and accumulated bases.
    /// Returns null if safe, or a reason string if conflict detected.
    /// </summary>
    private static string? CheckForConflicts(
        Dictionary<string, string> typeSurface,
        Dictionary<string, string> ifaceSignatures,
        Dictionary<string, (string Signature, string SourceInterface)> accumulatedBases,
        string ifaceFullName)
    {
        foreach (var (key, ifaceSig) in ifaceSignatures)
        {
            var memberName = key.Split(':')[0];
            var memberKind = key.Split(':')[1];

            // Check 1: TS2430 - Conflict with type's own surface
            if (typeSurface.TryGetValue(key, out var typeSig))
            {
                if (!AreSignaturesCompatible(typeSig, ifaceSig, memberKind))
                {
                    return $"TS2430: Member '{memberName}' has incompatible signature. " +
                           $"Type has: {typeSig}, Interface requires: {ifaceSig}";
                }
            }

            // Check 2: TS2320 - Conflict with other base interfaces
            if (accumulatedBases.TryGetValue(key, out var baseSig))
            {
                if (!AreSignaturesCompatible(baseSig.Signature, ifaceSig, memberKind))
                {
                    return $"TS2320: Member '{memberName}' conflicts between {baseSig.SourceInterface} and {ifaceFullName}. " +
                           $"Signatures: {baseSig.Signature} vs {ifaceSig}";
                }
            }
        }

        return null; // No conflicts
    }

    /// <summary>
    /// Checks if two signatures are compatible in TypeScript.
    /// For TS2320 (multiple inheritance), ALL signatures must be IDENTICAL.
    /// TypeScript declaration merging requires exact match between all parent types.
    /// </summary>
    private static bool AreSignaturesCompatible(string sig1, string sig2, string kind)
    {
        // For TS2320, both properties and methods must be exactly identical
        // because TypeScript declaration merging requires all parent types
        // to have identical member signatures.
        //
        // Example: If TypeInfo has GetField with 2 overloads and IReflect has 1 overload,
        // they are NOT identical and will cause TS2320.
        return sig1 == sig2;
    }

    // Helper methods
    private static string GetTypeFullName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            GenericParameterReference gp => gp.Name,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    private static bool IsInterfaceInGraph(TypeReference ifaceRef, SymbolGraph graph)
    {
        var fullName = GetTypeFullName(ifaceRef);
        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Any(t => t.ClrFullName == fullName && t.Kind == TypeKind.Interface);
    }

    private static bool IsInterfaceAvailable(BuildContext ctx, TypeReference ifaceRef, SymbolGraph graph)
    {
        // Present in the filtered graph
        if (IsInterfaceInGraph(ifaceRef, graph))
            return true;

        // Present in the pre-filter library namespace index (external type)
        var fullName = GetTypeFullName(ifaceRef);
        return ctx.LibraryNamespaceIndex?.ContainsKey(fullName) ?? false;
    }

    private static bool IsConflictingNonGenericInterface(TypeReference ifaceRef)
    {
        if (ifaceRef is not NamedTypeReference named)
            return false;

        // Non-generic System.Collections interfaces conflict with generic ones (IEnumerator vs IEnumerator<T> etc.)
        // C# solves this via explicit interface implementation; TS cannot.
        return named.FullName switch
        {
            "System.Collections.IEnumerable" => true,
            "System.Collections.IEnumerator" => true,
            "System.Collections.ICollection" => true,
            "System.Collections.IList" => true,
            "System.Collections.IDictionary" => true,
            "System.Collections.IDictionaryEnumerator" => true,
            "System.Collections.IComparer" => true,
            "System.Collections.IEqualityComparer" => true,
            "System.Collections.IStructuralComparable" => true,
            "System.Collections.IStructuralEquatable" => true,
            _ => false
        };
    }

    private static List<TypeReference> PreferGenericInterfaceVariants(List<TypeReference> interfaces)
    {
        // If we have both Foo and Foo`1, drop Foo. (and similarly for other arities)
        // This is a purely syntactic filter based on CLR full names.
        var fullNames = interfaces
            .Select(GetTypeFullName)
            .ToHashSet(StringComparer.Ordinal);

        static string StripArity(string fullName)
        {
            var tick = fullName.IndexOf('`');
            return tick >= 0 ? fullName.Substring(0, tick) : fullName;
        }

        var genericBases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in fullNames)
        {
            if (name.Contains('`'))
            {
                genericBases.Add(StripArity(name));
            }
        }

        return interfaces
            .Where(i =>
            {
                var name = GetTypeFullName(i);
                if (name.Contains('`')) return true;
                return !genericBases.Contains(name);
            })
            .ToList();
    }

    private static TypeSymbol? FindInterface(SymbolGraph graph, TypeReference ifaceRef)
    {
        var fullName = GetTypeFullName(ifaceRef);
        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName && t.Kind == TypeKind.Interface);
    }
}
