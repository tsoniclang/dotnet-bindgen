using System.Collections.Immutable;
using System.Linq;
using DotnetBindgen.Core;
using DotnetBindgen.Emit;
using DotnetBindgen.Emit.Printers;
using DotnetBindgen.Model;
using DotnetBindgen.Model.Symbols;
using DotnetBindgen.Model.Symbols.MemberSymbols;
using DotnetBindgen.Model.Types;
using DotnetBindgen.Plan;

namespace DotnetBindgen.Analysis;

/// <summary>
/// Analyzes the symbol graph to find and group extension methods by target type.
/// Pure functional analyzer - returns ExtensionMethodsPlan without mutating input.
/// </summary>
public static class ExtensionMethodAnalyzer
{
    /// <summary>
    /// Analyze the symbol graph and build a plan for emitting extension method buckets.
    /// Groups all extension methods by their target type (generic definition).
    /// </summary>
    /// <param name="ctx">Build context for logging</param>
    /// <param name="graph">Symbol graph to analyze</param>
    /// <returns>Plan containing all extension method buckets</returns>
    public static ExtensionMethodsPlan Analyze(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("ExtensionMethodAnalyzer", "Starting extension method analysis...");

        // Step 1: Collect all extension methods from all types
        var allExtensionMethods = new List<(MethodSymbol Method, string DeclaringNamespace)>();
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Extension methods must be in static classes
                if (!type.IsStatic)
                    continue;

                foreach (var method in type.Members.Methods)
                {
                    if (method.IsExtensionMethod)
                    {
                        allExtensionMethods.Add((method, type.Namespace));
                    }
                }
            }
        }

        ctx.Log("ExtensionMethodAnalyzer", $"Found {allExtensionMethods.Count} extension methods");

        // Step 2: Group by declaring namespace (C# using scope) + target type (generic definition)
        var buckets = new Dictionary<(string DeclaringNamespace, ExtensionTargetKey TargetKey), List<MethodSymbol>>();
        var targetTypeMap = new Dictionary<ExtensionTargetKey, TypeSymbol>();

        foreach (var (method, declaringNamespace) in allExtensionMethods)
        {
            if (method.ExtensionTarget == null)
            {
                ctx.Log("ExtensionMethodAnalyzer",
                    $"WARNING: Extension method {method.ClrName} marked as IsExtensionMethod but has null ExtensionTarget");
                continue;
            }

            // Get the target type reference (must be a NamedTypeReference)
            if (method.ExtensionTarget is not NamedTypeReference namedRef)
            {
                ctx.Log("ExtensionMethodAnalyzer",
                    $"WARNING: Extension method {method.ClrName} target is not a named type (kind: {method.ExtensionTarget.Kind})");
                continue;
            }

            // Create key from the type reference (use FullName without type arguments for grouping)
            // Example: System.Collections.Generic.List`1
            var key = new ExtensionTargetKey
            {
                FullName = namedRef.FullName,
                Arity = namedRef.Arity
            };

            // Try to resolve the target type symbol from the graph
            var targetTypeSymbol = FindTypeByClrFullName(graph, namedRef.FullName);
            if (targetTypeSymbol == null)
            {
                // Library mode (--lib) filters external types out of the graph, but extension methods
                // can still target those external receivers (e.g., EFCore extensions on IQueryable<T>).
                // Create a minimal stub so we can still emit a bucket interface and the per-namespace
                // ExtensionMethods helper type.
                targetTypeSymbol = CreateExternalTargetTypeStub(namedRef);
                ctx.Log("ExtensionMethodAnalyzer",
                    $"  Using external target stub for '{namedRef.FullName}' (extension method {method.ClrName})");
            }

            // STRICT MODE: Skip methods with unresolvable type signatures ('any' erasures)
            // This prevents TBG905 errors by filtering at plan creation time
            if (HasErasedAnyTypes(ctx, graph, method, targetTypeSymbol, importPlan: null))
            {
                ctx.Log("ExtensionMethodAnalyzer",
                    $"  Skipping {method.ClrName} on {namedRef.FullName} - contains erased 'any' types");
                continue;
            }

            // Add to bucket
            var bucketKey = (DeclaringNamespace: declaringNamespace, TargetKey: key);

            if (!buckets.ContainsKey(bucketKey))
            {
                buckets[bucketKey] = new List<MethodSymbol>();

                // Target type symbol is shared across declaring namespaces; only store once.
                if (!targetTypeMap.ContainsKey(key))
                {
                    targetTypeMap[key] = targetTypeSymbol;
                }
            }

            buckets[bucketKey].Add(method);
        }

        ctx.Log("ExtensionMethodAnalyzer", $"Grouped into {buckets.Count} (declaring namespace × target type) buckets");

        // Step 3: Build bucket plans
        var bucketPlans = new List<ExtensionBucketPlan>();
        foreach (var entry in buckets
                     .OrderBy(e => e.Key.DeclaringNamespace, StringComparer.Ordinal)
                     .ThenBy(e => e.Key.TargetKey.FullName, StringComparer.Ordinal)
                     .ThenBy(e => e.Key.TargetKey.Arity))
        {
            var declaringNamespace = entry.Key.DeclaringNamespace;
            var key = entry.Key.TargetKey;
            var methods = entry.Value;

            var targetType = targetTypeMap[key];
            var plan = new ExtensionBucketPlan
            {
                Key = key,
                DeclaringNamespace = declaringNamespace,
                TargetType = targetType,
                Methods = methods.ToImmutableArray()
            };
            bucketPlans.Add(plan);

            ctx.Log("ExtensionMethodAnalyzer",
                $"  Bucket: {plan.BucketInterfaceName} ({methods.Count} methods for {key.FullName} in {declaringNamespace})");
        }

        // Step 4: Return final plan
        var finalPlan = new ExtensionMethodsPlan
        {
            Buckets = bucketPlans.ToImmutableArray()
        };

        ctx.Log("ExtensionMethodAnalyzer",
            $"Extension method analysis complete: {finalPlan.Buckets.Length} buckets, {finalPlan.TotalMethodCount} total methods");

        return finalPlan;
    }

    private static TypeSymbol CreateExternalTargetTypeStub(NamedTypeReference typeRef)
    {
        // Sanitize CLR name into a TS emit name (no casing transforms; only arity + reserved words).
        var sanitized = typeRef.Name.Replace('`', '_').Replace('+', '_');
        var result = TypeScriptReservedWords.SanitizeTypeName(sanitized);

        // Heuristic: treat "I*" as interface; otherwise class. This is only used for bucket naming,
        // not for actual emission of the external type.
        var kind = typeRef.Name.StartsWith("I", StringComparison.Ordinal) ? TypeKind.Interface : TypeKind.Class;

        // Stub generic parameters (names are not observable to callers; they only affect bucket method signatures).
        var genericParams = ImmutableArray<Model.Symbols.GenericParameterSymbol>.Empty;
        if (typeRef.Arity > 0)
        {
            var list = new List<Model.Symbols.GenericParameterSymbol>(typeRef.Arity);
            for (var i = 0; i < typeRef.Arity; i++)
            {
                var name = typeRef.Arity == 1 ? "T" : $"T{i + 1}";
                list.Add(new Model.Symbols.GenericParameterSymbol
                {
                    Id = new GenericParameterId
                    {
                        DeclaringTypeName = typeRef.FullName,
                        Position = i,
                        IsMethodParameter = false
                    },
                    Name = name,
                    Position = i,
                    Constraints = ImmutableArray<Model.Types.TypeReference>.Empty,
                    RawConstraintTypes = null,
                    Variance = Variance.None,
                    SpecialConstraints = GenericParameterConstraints.None
                });
            }
            genericParams = list.ToImmutableArray();
        }

        return new TypeSymbol
        {
            StableId = new DotnetBindgen.Renaming.TypeStableId
            {
                AssemblyName = typeRef.AssemblyName,
                ClrFullName = typeRef.FullName
            },
            ClrFullName = typeRef.FullName,
            ClrName = typeRef.Name,
            TsEmitName = result.Sanitized,
            Accessibility = Accessibility.Public,
            Namespace = typeRef.Namespace,
            Kind = kind,
            Arity = typeRef.Arity,
            GenericParameters = genericParams,
            BaseType = null,
            Interfaces = ImmutableArray<Model.Types.TypeReference>.Empty,
            Members = TypeMembers.Empty,
            NestedTypes = ImmutableArray<TypeSymbol>.Empty,
            IsValueType = typeRef.IsValueType,
            IsAbstract = false,
            IsSealed = false,
            IsStatic = false,
            DeclaringType = null,
            Documentation = null
        };
    }

    /// <summary>
    /// Find a TypeSymbol in the graph by its CLR full name.
    /// </summary>
    private static TypeSymbol? FindTypeByClrFullName(SymbolGraph graph, string clrFullName)
    {
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                if (type.ClrFullName == clrFullName)
                {
                    return type;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Check if a method's signature contains erased 'any' types.
    /// Returns true if TypeRefPrinter would produce 'any' for return type, parameters, or constraints.
    /// STRICT MODE: Methods with 'any' types are filtered out during plan generation.
    /// </summary>
    private static bool HasErasedAnyTypes(
        BuildContext ctx,
        SymbolGraph graph,
        MethodSymbol method,
        TypeSymbol declaringType,
        ImportPlan? importPlan)
    {
        // Create type name resolver for the method's declaring type namespace
        var resolver = new TypeNameResolver(ctx, graph, importPlan, declaringType.Namespace);

        // Check return type
        if (method.ReturnType != null)
        {
            var returnTypeString = TypeRefPrinter.Print(
                method.ReturnType,
                resolver,
                ctx,
                forValuePosition: false);

            if (returnTypeString == "any")
                return true;
        }

        // Check parameter types (skip first parameter - it's the 'this' parameter)
        foreach (var param in method.Parameters.Skip(1))
        {
            var paramTypeString = TypeRefPrinter.Print(
                param.Type,
                resolver,
                ctx,
                forValuePosition: false);

            if (paramTypeString == "any")
                return true;
        }

        // Check generic constraints
        foreach (var genParam in method.GenericParameters)
        {
            foreach (var constraint in genParam.Constraints)
            {
                var constraintTypeString = TypeRefPrinter.Print(
                    constraint,
                    resolver,
                    ctx,
                    forValuePosition: false);

                if (constraintTypeString == "any")
                    return true;
            }
        }

        return false;
    }
}
