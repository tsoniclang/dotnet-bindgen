using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Renaming;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;

namespace tsbindgen.Shape;

/// <summary>
/// Adds base class overloads when derived class differs.
/// In TypeScript, all overloads must be present on the derived class.
/// PURE - returns new SymbolGraph.
/// </summary>
public static class BaseOverloadAdder
{
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

            // Add this class's methods
            allMethods.AddRange(currentClass.Members.Methods.Where(m => !m.IsStatic));

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

        // Find methods in derived that override or hide base methods
        var derivedMethodsByName = derivedClass.Members.Methods
            .Where(m => !m.IsStatic)
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
                // Build the StableId that the derived method would have if it existed
                var expectedStableId = new MemberStableId
                {
                    AssemblyName = derivedClass.StableId.AssemblyName,
                    DeclaringClrFullName = derivedClass.ClrFullName,
                    MemberName = baseMethod.ClrName,
                    CanonicalSignature = ctx.CanonicalizeMethod(
                        baseMethod.ClrName,
                        baseMethod.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                        GetTypeFullName(baseMethod.ReturnType))
                };

                var derivedHas = derivedMethods.Any(dm => dm.StableId.Equals(expectedStableId));

                if (!derivedHas)
                {
                    // Derived doesn't have this base overload - add it
                    ctx.Log("BaseOverloadAdder", $"Adding base overload to {derivedClass.ClrFullName}: {baseMethod.ClrName} -> StableId: {expectedStableId}");
                    var addedMethod = CreateBaseOverloadMethod(ctx, derivedClass, baseMethod);
                    addedMethods.Add(addedMethod);
                }
                else
                {
                    ctx.Log("BaseOverloadAdder", $"Derived already has: {baseMethod.ClrName} -> StableId: {expectedStableId}");
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
        var addedStableIds = addedMethods.Select(m => m.StableId).ToList();
        var duplicates = addedStableIds.Where(id => existingStableIds.Contains(id)).ToList();

        if (duplicates.Any())
        {
            var details = string.Join("\n", duplicates.Select(id => $"  {id}"));
            throw new InvalidOperationException(
                $"BaseOverloadAdder: Attempting to add duplicate methods to {derivedClass.ClrFullName}:\n{details}\n" +
                $"This would create duplicates with existing. Check comparison logic.");
        }

        // Add to derived class (immutably)
        var updatedGraph = graph.WithUpdatedType(derivedClass.StableId.ToString(), t => t with
        {
            Members = t.Members with
            {
                Methods = t.Members.Methods.Concat(addedMethods).ToImmutableArray()
            }
        });

        return (updatedGraph, addedMethods.Count);
    }

    private static MethodSymbol CreateBaseOverloadMethod(BuildContext ctx, TypeSymbol derivedClass, MethodSymbol baseMethod)
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
                baseMethod.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                GetTypeFullName(baseMethod.ReturnType))
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
            ReturnType = baseMethod.ReturnType,
            Parameters = baseMethod.Parameters,
            GenericParameters = baseMethod.GenericParameters,
            IsStatic = false,
            IsAbstract = baseMethod.IsAbstract,
            IsVirtual = baseMethod.IsVirtual,
            IsOverride = false, // Not an override, it's the base signature
            IsSealed = false,
            IsNew = false,
            Visibility = baseMethod.Visibility,
            Provenance = MemberProvenance.BaseOverload,
            EmitScope = EmitScope.ClassSurface,
            Documentation = baseMethod.Documentation
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
