using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Renaming;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Model.Types;

namespace tsbindgen.Shape;

/// <summary>
/// Adds base class overloads when derived class differs.
/// In TypeScript, all overloads must be present on the derived class.
/// PURE - returns new SymbolGraph.
/// </summary>
public static class BaseOverloadAdder
{
    private sealed record ClosedMethodSignature(
        ImmutableArray<ParameterSymbol> Parameters,
        TypeReference ReturnType);

    public static SymbolGraph AddOverloads(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("BaseOverloadAdder", "Adding base class overloads...");

        // DEBUG: Check for duplicates BEFORE we do anything
        var allTypes = graph.Namespaces.SelectMany(ns => ns.Types).ToList();
        foreach (var type in allTypes)
        {
            var methodDuplicates = type.Members.Methods
                .GroupBy(m => m.StableId)
                .Where(g => g.Count() > 1)
                .ToList();

            if (methodDuplicates.Any())
            {
                var details = string.Join("\n", methodDuplicates.Select(g => $"  Method {g.Key}: {g.Count()} duplicates"));
                ctx.Log("BaseOverloadAdder", $"WARNING: Type {type.ClrFullName} ALREADY has duplicates at entry:\n{details}");
            }
        }

        var classes = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Where(t => t.Kind == TypeKind.Class && t.BaseType != null)
            .ToList();

        // FIX TS2416: Sort classes by inheritance depth (base classes first)
        // This ensures base classes get their BaseOverload methods added before derived classes look for them
        var sortedClasses = SortByInheritanceDepth(graph, classes);

        int totalAdded = 0;
        var updatedGraph = graph;


        foreach (var derivedClass in sortedClasses)
        {
            var (newGraph, added) = AddOverloadsForClass(ctx, updatedGraph, derivedClass);
            updatedGraph = newGraph;
            totalAdded += added;
        }

        ctx.Log("BaseOverloadAdder", $"Added {totalAdded} base overloads");
        return updatedGraph;
    }

    /// <summary>
    /// Sort classes by inheritance depth (base classes first, derived classes later).
    /// This ensures base classes get their BaseOverload methods added before derived classes look for them.
    /// </summary>
    private static List<TypeSymbol> SortByInheritanceDepth(SymbolGraph graph, List<TypeSymbol> classes)
    {
        // Calculate depth for each class (0 = no base class in graph, 1 = base has no base class, etc.)
        var depthMap = new Dictionary<string, int>();

        int GetDepth(TypeSymbol type)
        {
            if (depthMap.TryGetValue(type.ClrFullName, out var cached))
                return cached;

            if (type.BaseType == null)
            {
                depthMap[type.ClrFullName] = 0;
                return 0;
            }

            // Try to find base class in graph
            var baseTypeRef = type.BaseType as Model.Types.NamedTypeReference;
            if (baseTypeRef != null && graph.TryGetType(baseTypeRef.FullName, out var baseType) && baseType != null)
            {
                // Base class is in graph, recurse
                var depth = 1 + GetDepth(baseType);
                depthMap[type.ClrFullName] = depth;
                return depth;
            }
            else
            {
                // Base class is external (e.g., System.Object) - depth is 0
                depthMap[type.ClrFullName] = 0;
                return 0;
            }
        }

        // Calculate depths
        foreach (var cls in classes)
        {
            GetDepth(cls);
        }

        // Sort by depth (ascending - base classes first)
        return classes.OrderBy(c => depthMap[c.ClrFullName]).ToList();
    }

    /// <summary>
    /// Collect all methods from the base class hierarchy.
    /// Groups methods by name, collecting all overloads across the entire inheritance chain.
    /// </summary>
    private static Dictionary<string, List<MethodSymbol>> CollectHierarchyMethods(SymbolGraph graph, TypeSymbol baseClass)
    {
        var allMethods = new List<MethodSymbol>();
        var visited = new HashSet<string>();  // Track visited types to avoid cycles

        void WalkHierarchy(TypeSymbol currentClass)
        {
            if (visited.Contains(currentClass.ClrFullName))
                return;
            visited.Add(currentClass.ClrFullName);

            // Add this class's methods.
            // IMPORTANT: only consider ClassSurface methods when reasoning about base-class overload coverage.
            // ViewOnly methods (e.g. ExplicitView interface members) are not emitted on the class surface and
            // must not block injection of missing base overloads.
            allMethods.AddRange(currentClass.Members.Methods.Where(m => !m.IsStatic && m.EmitScope == EmitScope.ClassSurface));

            // Recurse to base class
            if (currentClass.BaseType != null)
            {
                var baseTypeRef = currentClass.BaseType as Model.Types.NamedTypeReference;
                if (baseTypeRef != null && graph.TryGetType(baseTypeRef.FullName, out var nextBase) && nextBase != null)
                {
                    WalkHierarchy(nextBase);
                }
            }
        }

        WalkHierarchy(baseClass);

        // Group by method name
        return allMethods
            .GroupBy(m => m.ClrName)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    private static (SymbolGraph UpdatedGraph, int AddedCount) AddOverloadsForClass(BuildContext ctx, SymbolGraph graph, TypeSymbol derivedClass)
    {
        // DEBUG: Log which type we're processing
        ctx.Log("BaseOverloadAdder", $"Processing {derivedClass.ClrFullName} (Kind: {derivedClass.Kind})");

        // Find the base class
        var baseClass = FindBaseClass(graph, derivedClass);
        if (baseClass == null)
            return (graph, 0); // External base or System.Object

        // Build a generic substitution map for the entire inheritance chain so we can close
        // base class method signatures (e.g. RoutingHost<TSelf>.get(...) returning TSelf) into
        // the derived class context (e.g. TSelf -> Router).
        var inheritanceSubstitution = BuildInheritanceSubstitutionMap(graph, derivedClass);

        // Find methods in derived that override or hide base methods
        var derivedMethodsByName = derivedClass.Members.Methods
            .Where(m => !m.IsStatic && m.EmitScope == EmitScope.ClassSurface)
            .GroupBy(m => m.ClrName)
            .ToDictionary(g => g.Key, g => g.ToList());

        // FIX TS2416: Collect methods from ENTIRE base class hierarchy, not just immediate base
        // Example: ArrayConverter overrides TypeConverter.getPropertiesSupported(context)
        //          but TypeConverter also has getPropertiesSupported() without params
        //          CollectionConverter (immediate base) doesn't override either
        //          We need to find the parameterless overload from TypeConverter
        var baseMethodsByName = CollectHierarchyMethods(graph, baseClass);

        var addedMethods = new List<MethodSymbol>();


        // For each base method name, check if derived has all the same overloads
        // Sort by method name for deterministic iteration
        foreach (var (methodName, baseMethods) in baseMethodsByName.OrderBy(kvp => kvp.Key))
        {
            if (!derivedMethodsByName.TryGetValue(methodName, out var derivedMethods))
            {
                // Derived doesn't override this method at all - keep base methods
                continue;
            }

            // Check each base method to see if derived has the same signature
            // FIX: Compare by StableId instead of re-canonicalizing (same fix as ExplicitImplSynthesizer)
            foreach (var baseMethod in baseMethods)
            {
                var closedBase = CloseMethodSignature(baseMethod, inheritanceSubstitution);

                var derivedHasCompatible = derivedMethods.Any(dm =>
                {
                    var closedDerived = CloseMethodSignature(dm, inheritanceSubstitution);

                    // Base overload matching: exact parameter shape (CLR) + covariant return allowed.
                    // TypeScript assignability allows covariant returns, so a derived overload with
                    // the same parameters and a more-derived return type already satisfies the base.
                    if (!ParametersMatch(closedDerived.Parameters, closedBase.Parameters))
                        return false;

                    return IsReturnAssignable(closedDerived.ReturnType, closedBase.ReturnType, graph);
                });

                if (!derivedHasCompatible)
                {
                    // Derived doesn't have this base overload - add it
                    var addedMethod = CreateBaseOverloadMethod(ctx, derivedClass, baseMethod, closedBase);
                    addedMethods.Add(addedMethod);
                }
                else
                {
                    // Derived already has a compatible overload.
                }
            }
        }

        if (addedMethods.Count == 0)
            return (graph, 0);

        // DEDUPLICATION: If base hierarchy has same method at multiple levels, deduplicate by StableId
        var uniqueMethods = addedMethods.GroupBy(m => m.StableId).Select(g => g.First()).ToList();

        if (addedMethods.Count != uniqueMethods.Count)
        {
            ctx.Log("BaseOverloadAdder",
                $"Deduplicated {addedMethods.Count - uniqueMethods.Count} duplicate base overloads " +
                $"(method appears at multiple hierarchy levels)");
        }

        addedMethods = uniqueMethods;

        ctx.Log("BaseOverloadAdder", $"Adding {addedMethods.Count} base overloads to {derivedClass.ClrFullName}");

        // VALIDATION: Check for duplicates WITHIN the added list (should be none after deduplication)
        var internalDuplicates = addedMethods.GroupBy(m => m.StableId).Where(g => g.Count() > 1).ToList();
        if (internalDuplicates.Any())
        {
            var details = string.Join("\n", internalDuplicates.Select(g => $"  {g.Key} ({g.Count()} copies)"));
            throw new InvalidOperationException(
                $"BaseOverloadAdder: Added list contains INTERNAL duplicates for {derivedClass.ClrFullName}:\n{details}\n" +
                $"This indicates base overload logic added the same method multiple times.");
        }

        // VALIDATION: Check if adding these methods would create duplicates with existing
        var existingStableIds = derivedClass.Members.Methods.Select(m => m.StableId).ToHashSet();
        var duplicates = addedMethods.Where(m => existingStableIds.Contains(m.StableId)).ToList();
        if (duplicates.Count > 0)
        {
            // Airplane-grade robustness: do not fail generation for redundant additions.
            // If we computed a StableId that already exists on the derived type, we can
            // safely skip it (it contributes nothing) and continue.
            ctx.Log("BaseOverloadAdder",
                $"Skipping {duplicates.Count} redundant base overload(s) already present on {derivedClass.ClrFullName}");
            addedMethods = addedMethods.Where(m => !existingStableIds.Contains(m.StableId)).ToList();
        }

        // Add to derived class (immutably)
        if (addedMethods.Count == 0)
            return (graph, 0);

        var updatedGraph = graph.WithUpdatedType(derivedClass.StableId.ToString(), t => t with
        {
            Members = t.Members with
            {
                Methods = t.Members.Methods.Concat(addedMethods).ToImmutableArray()
            }
        });

        return (updatedGraph, addedMethods.Count);
    }

    private static MethodSymbol CreateBaseOverloadMethod(BuildContext ctx, TypeSymbol derivedClass, MethodSymbol baseMethod, ClosedMethodSignature closedBase)
    {
        // Base scope without #static/#instance suffix - ReserveMemberName will add it
        var typeScope = ScopeFactory.ClassBase(derivedClass);

        var stableId = new MemberStableId
        {
            AssemblyName = derivedClass.StableId.AssemblyName,
            DeclaringClrFullName = derivedClass.ClrFullName,
            MemberName = baseMethod.ClrName,
            CanonicalSignature = ctx.CanonicalizeMethod(
                baseMethod.ClrName,
                closedBase.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                GetTypeFullName(closedBase.ReturnType))
        };

        // Reserve name with BaseOverload reason
        ctx.Renamer.ReserveMemberName(
            stableId,
            baseMethod.ClrName,
            typeScope,
            "BaseOverload",
            isStatic: false);

        // Create the method with BaseOverload provenance
        return new MethodSymbol
        {
            StableId = stableId,
            ClrName = baseMethod.ClrName,
            ReturnType = closedBase.ReturnType,
            Parameters = closedBase.Parameters,
            GenericParameters = baseMethod.GenericParameters,
            IsStatic = false,
            IsAbstract = baseMethod.IsAbstract,
            IsVirtual = baseMethod.IsVirtual,
            IsOverride = false, // Not an override, it's the base signature
            IsSealed = baseMethod.IsSealed,
            IsNew = false,
            Visibility = baseMethod.Visibility,
            Provenance = MemberProvenance.BaseOverload,
            EmitScope = EmitScope.ClassSurface,
            Documentation = baseMethod.Documentation
        };
    }

    private static ClosedMethodSignature CloseMethodSignature(MethodSymbol method, Dictionary<GenericParameterId, TypeReference> substitution)
    {
        var parameters = method.Parameters
            .Select(p => p with { Type = SubstituteTypeRef(p.Type, substitution) })
            .ToImmutableArray();

        var returnType = SubstituteTypeRef(method.ReturnType, substitution);

        return new ClosedMethodSignature(parameters, returnType);
    }

    private static bool ParametersMatch(ImmutableArray<ParameterSymbol> left, ImmutableArray<ParameterSymbol> right)
    {
        if (left.Length != right.Length)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            var lp = left[i];
            var rp = right[i];

            // Match the same way StableId canonicalization does (full CLR name),
            // and intentionally ignore NRT nullability state for overload-coverage purposes.
            if (GetTypeFullName(lp.Type) != GetTypeFullName(rp.Type))
                return false;

            if (lp.IsRef != rp.IsRef)
                return false;
            if (lp.IsOut != rp.IsOut)
                return false;
            if (lp.IsIn != rp.IsIn)
                return false;
            if (lp.IsParams != rp.IsParams)
                return false;

            if (lp.HasDefaultValue != rp.HasDefaultValue)
                return false;
            if (lp.HasDefaultValue && !Equals(lp.DefaultValue, rp.DefaultValue))
                return false;
        }

        return true;
    }

    private static bool IsReturnAssignable(TypeReference derivedReturn, TypeReference baseReturn, SymbolGraph graph)
    {
        // Exact match (same canonical CLR name) - ignore NRT nullability state here
        if (GetTypeFullName(derivedReturn) == GetTypeFullName(baseReturn))
            return true;

        // Everything is assignable to object
        if (baseReturn is NamedTypeReference bnr && bnr.FullName == "System.Object")
            return true;

        // Named-to-named: check CLR inheritance chain
        if (derivedReturn is NamedTypeReference d && baseReturn is NamedTypeReference b)
        {
            if (d.FullName == b.FullName)
                return true;

            // Walk base class chain for derived type
            if (graph.TryGetType(d.FullName, out var derivedType) && derivedType != null)
            {
                // Direct/indirect base classes
                var current = derivedType;
                while (current.BaseType is NamedTypeReference baseRef)
                {
                    if (baseRef.FullName == b.FullName)
                        return true;

                    if (!graph.TryGetType(baseRef.FullName, out var next) || next == null)
                        break;

                    current = next;
                }

                // Interfaces
                foreach (var iface in derivedType.Interfaces)
                {
                    if (iface is NamedTypeReference inr && inr.FullName == b.FullName)
                        return true;
                }
            }
        }

        return false;
    }

    private static Dictionary<GenericParameterId, TypeReference> BuildInheritanceSubstitutionMap(SymbolGraph graph, TypeSymbol derivedType)
    {
        // Maps generic parameter IDs (from any base type in the chain) to the corresponding
        // closed type argument as seen from the derived type.
        var map = new Dictionary<GenericParameterId, TypeReference>();

        // Walk derived → base → base → ...
        var current = derivedType;
        while (current.BaseType != null)
        {
            // Resolve base type symbol + reference (which may include type arguments)
            var baseRef = current.BaseType;

            NamedTypeReference? baseNamed = baseRef switch
            {
                NamedTypeReference n => n,
                NestedTypeReference nested => nested.FullReference,
                _ => null
            };

            if (baseNamed == null)
                break;

            if (!graph.TryGetType(baseNamed.FullName, out var baseSymbol) || baseSymbol == null)
                break;

            // Bind baseSymbol<T...> generic parameters to the constructed baseNamed<TArg...>
            if (baseSymbol.GenericParameters.Length > 0 && baseNamed.TypeArguments.Count == baseSymbol.GenericParameters.Length)
            {
                for (var i = 0; i < baseSymbol.GenericParameters.Length; i++)
                {
                    var gp = baseSymbol.GenericParameters[i];
                    var arg = SubstituteTypeRef(baseNamed.TypeArguments[i], map);
                    map[gp.Id] = arg;
                }
            }

            current = baseSymbol;
        }

        return map;
    }

    private static TypeReference SubstituteTypeRef(TypeReference typeRef, Dictionary<GenericParameterId, TypeReference> substitution)
    {
        return typeRef switch
        {
            GenericParameterReference gp when substitution.TryGetValue(gp.Id, out var repl) => repl,

            NamedTypeReference named when named.TypeArguments.Count > 0 => named with
            {
                TypeArguments = named.TypeArguments.Select(t => SubstituteTypeRef(t, substitution)).ToList()
            },

            ArrayTypeReference arr => arr with { ElementType = SubstituteTypeRef(arr.ElementType, substitution) },
            PointerTypeReference ptr => ptr with { PointeeType = SubstituteTypeRef(ptr.PointeeType, substitution) },
            ByRefTypeReference byref => byref with { ReferencedType = SubstituteTypeRef(byref.ReferencedType, substitution) },
            NestedTypeReference nested => nested with
            {
                DeclaringType = SubstituteTypeRef(nested.DeclaringType, substitution),
                FullReference = (NamedTypeReference)SubstituteTypeRef(nested.FullReference, substitution)
            },

            _ => typeRef
        };
    }

    private static TypeSymbol? FindBaseClass(SymbolGraph graph, TypeSymbol derivedClass)
    {
        if (derivedClass.BaseType == null)
            return null;

        var baseFullName = GetTypeFullName(derivedClass.BaseType);

        // Skip System.Object and System.ValueType
        if (baseFullName == "System.Object" || baseFullName == "System.ValueType")
            return null;

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == baseFullName && t.Kind == TypeKind.Class);
    }

    private static string GetTypeFullName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            GenericParameterReference gp => gp.Name,
            ArrayTypeReference arr => $"{GetTypeFullName(arr.ElementType)}[]",
            PointerTypeReference ptr => $"{GetTypeFullName(ptr.PointeeType)}*",
            ByRefTypeReference byref => $"{GetTypeFullName(byref.ReferencedType)}&",
            _ => typeRef.ToString() ?? "Unknown"
        };
    }
}
