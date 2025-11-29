using System.Collections.Immutable;
using System.Linq;
using System.Text;
using tsbindgen.Model;
using tsbindgen.Model.Symbols;
using tsbindgen.Model.Symbols.MemberSymbols;
using tsbindgen.Renaming;

namespace tsbindgen.Emit.Printers;

/// <summary>
/// Prints TypeScript method signatures from MethodSymbol.
/// Handles generic methods, parameters, return types, and modifiers.
/// </summary>
public static class MethodPrinter
{
    /// <summary>
    /// METHOD CONSTRAINTS (Pattern A - Shadowing): Enriches method generic parameters with class constraints.
    /// When a method generic parameter has the same name as a class generic but no constraints of its own,
    /// inherit the class generic's constraints for TypeScript emission.
    ///
    /// This fixes TS2344 errors where:
    ///   class Foo<T extends IEquatable_1<T>> {
    ///       static Bar<T>(x: T): Foo<T>;  // method T unconstrained
    ///   }
    ///
    /// Becomes:
    ///   class Foo<T extends IEquatable_1<T>> {
    ///       static Bar<T extends IEquatable_1<T>>(x: T): Foo<T>;  // inherited constraint
    ///   }
    /// </summary>
    private static ImmutableArray<GenericParameterSymbol> EnrichMethodGenericParametersWithClassConstraints(
        MethodSymbol method,
        TypeSymbol declaringType)
    {
        // Build lookup: class generic name → constraints
        var classConstraints = declaringType.GenericParameters
            .Where(gp => gp.Constraints.Length > 0)
            .ToDictionary(gp => gp.Name, gp => gp.Constraints);

        if (classConstraints.Count == 0)
        {
            // No class constraints to inherit
            return method.GenericParameters;
        }

        var enriched = new List<GenericParameterSymbol>(method.GenericParameters.Length);
        bool anyEnriched = false;

        foreach (var methodGp in method.GenericParameters)
        {
            // Pattern A: Shadowing fallback
            // If method parameter has no constraints but class has constraints for same name, inherit them
            if (methodGp.Constraints.Length == 0 &&
                classConstraints.TryGetValue(methodGp.Name, out var inheritedConstraints))
            {
                // Create enriched parameter with class constraints
                enriched.Add(methodGp with { Constraints = inheritedConstraints });
                anyEnriched = true;
            }
            else
            {
                // Keep as-is (method has its own constraints, or no matching class constraint)
                enriched.Add(methodGp);
            }
        }

        return anyEnriched
            ? enriched.ToImmutableArray()
            : method.GenericParameters;
    }


    /// <summary>
    /// Print a method signature to TypeScript.
    /// </summary>
    public static string Print(MethodSymbol method, TypeSymbol declaringType, TypeNameResolver resolver, BuildContext ctx)
    {
        // Get the final TS name from Renamer using correct scope
        var scope = ScopeFactory.ClassSurface(declaringType, method.IsStatic);
        var finalName = ctx.Renamer.GetFinalMemberName(method.StableId, scope);

        return PrintWithName(method, declaringType, finalName, resolver, ctx);
    }

    /// <summary>
    /// TS2416/TS2420 FIX: Print a method signature with a custom name (for overload sets).
    /// Used when emitting TypeScript overloads with CLR-cased names instead of Renamer names.
    /// </summary>
    /// <param name="emitAbstract">Optional: override whether to emit abstract keyword (for TS2512 fix)</param>
    public static string PrintWithName(MethodSymbol method, TypeSymbol declaringType, string methodName, TypeNameResolver resolver, BuildContext ctx, bool? emitAbstract = null)
    {
        var sb = new StringBuilder();

        // TS2304 FIX: Compute allowed type parameters for this method signature
        // Include both class-level and method-level generic parameters
        // Any GenericParameterReference NOT in this set will be demoted to 'unknown'
        var allowedTypeParams = new HashSet<string>();
        foreach (var gp in declaringType.GenericParameters)
        {
            allowedTypeParams.Add(gp.Name);
        }
        foreach (var gp in method.GenericParameters)
        {
            allowedTypeParams.Add(gp.Name);
        }

        // Modifiers
        // IMPORTANT: Don't emit static/abstract modifiers for interface members
        // - TypeScript interfaces don't support static members (C# 11 feature)
        // - TypeScript interface methods are implicitly abstract
        var isInterface = declaringType.Kind == TypeKind.Interface;

        if (method.IsStatic && !isInterface)
            sb.Append("static ");

        // TS2512 FIX: Use group-level abstract status if provided, otherwise use method's individual status
        var shouldEmitAbstract = emitAbstract ?? method.IsAbstract;
        if (shouldEmitAbstract && !isInterface)
            sb.Append("abstract ");

        // Method name (provided by caller)
        sb.Append(methodName);

        // Generic parameters: <T, U>
        // METHOD CONSTRAINTS: Enrich with class constraints for shadowing case (Pattern A)
        var enrichedGenericParams = EnrichMethodGenericParametersWithClassConstraints(method, declaringType);
        if (enrichedGenericParams.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", enrichedGenericParams.Select(gp => PrintGenericParameter(gp, resolver, ctx))));
            sb.Append('>');
        }

        // Parameters: (a: int, b: string)
        // TS2304 FIX: Pass allowed type parameters to catch free type variables
        sb.Append('(');
        sb.Append(string.Join(", ", method.Parameters.Select(p => PrintParameter(p, resolver, ctx, allowedTypeParams))));
        sb.Append(')');

        // Return type: : int
        // TS2304 FIX: Pass allowed type parameters to catch free type variables
        sb.Append(": ");
        sb.Append(TypeRefPrinter.Print(method.ReturnType, resolver, ctx, allowedTypeParams));

        return sb.ToString();
    }

    private static string PrintGenericParameter(GenericParameterSymbol gp, TypeNameResolver resolver, BuildContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append(gp.Name);

        // Print constraints from the IReadOnlyList<TypeReference>
        if (gp.Constraints.Length > 0)
        {
            sb.Append(" extends ");

            // TS2344 FIX: Filter out "any" from constraints
            // C# value type constraints (struct, unmanaged) can't be represented in TS and emit as "any"
            // "any & IFoo" is invalid - just use "IFoo"
            var printedConstraints = gp.Constraints
                .Select(c => TypeRefPrinter.Print(c, resolver, ctx))
                .Where(c => c != "any" && c != "unknown")  // Filter out fallback types
                .ToArray();

            if (printedConstraints.Length == 0)
            {
                // All constraints were unrepresentable - use "unknown" (never "any")
                sb.Append("unknown");
            }
            else if (printedConstraints.Length == 1)
            {
                sb.Append(printedConstraints[0]);
            }
            else
            {
                // Multiple constraints: T extends IFoo & IBar
                sb.Append(string.Join(" & ", printedConstraints));
            }
        }

        return sb.ToString();
    }

    private static string PrintParameter(
        ParameterSymbol param,
        TypeNameResolver resolver,
        BuildContext ctx,
        HashSet<string>? allowedTypeParams = null)
    {
        var sb = new StringBuilder();

        // Parameter name - sanitize reserved words (break → break_, finally → finally_)
        sb.Append(TypeScriptReservedWords.SanitizeParameterName(param.Name));

        // Optional parameter: name?
        if (param.HasDefaultValue)
            sb.Append('?');

        // Parameter type: name: int
        sb.Append(": ");

        // Handle ref/out parameters
        if (param.IsOut || param.IsRef)
        {
            // TypeScript has no ref/out
            // Map to { value: T } wrapper (metadata tracks original semantics)
            var innerType = TypeRefPrinter.Print(param.Type, resolver, ctx, allowedTypeParams);
            sb.Append($"{{ value: {innerType} }}");
        }
        else if (param.IsParams)
        {
            // params T[] → ...args: T[]
            // Note: params keyword handled by caller (adds ... to parameter name)
            sb.Append(TypeRefPrinter.Print(param.Type, resolver, ctx, allowedTypeParams));
        }
        else
        {
            sb.Append(TypeRefPrinter.Print(param.Type, resolver, ctx, allowedTypeParams));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Print method with params array handling.
    /// Converts params T[] parameter to ...name: T[]
    /// </summary>
    public static string PrintWithParamsExpansion(MethodSymbol method, TypeSymbol declaringType, TypeNameResolver resolver, BuildContext ctx)
    {
        // Check if last parameter is params
        var hasParams = method.Parameters.Length > 0 && method.Parameters[^1].IsParams;

        if (!hasParams)
            return Print(method, declaringType, resolver, ctx);

        // TS2304 FIX: Compute allowed type parameters
        var allowedTypeParams = new HashSet<string>();
        foreach (var gp in declaringType.GenericParameters)
        {
            allowedTypeParams.Add(gp.Name);
        }
        foreach (var gp in method.GenericParameters)
        {
            allowedTypeParams.Add(gp.Name);
        }

        // Build method signature with params expansion
        var sb = new StringBuilder();

        // Get final name using correct scope
        var scope = ScopeFactory.ClassSurface(declaringType, method.IsStatic);
        var finalName = ctx.Renamer.GetFinalMemberName(method.StableId, scope);

        // Modifiers
        // IMPORTANT: Don't emit static/abstract modifiers for interface members
        var isInterface = declaringType.Kind == TypeKind.Interface;

        if (method.IsStatic && !isInterface)
            sb.Append("static ");

        if (method.IsAbstract && !isInterface)
            sb.Append("abstract ");

        // Method name
        sb.Append(finalName);

        // Generic parameters
        if (method.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", method.GenericParameters.Select(gp => PrintGenericParameter(gp, resolver, ctx))));
            sb.Append('>');
        }

        // Parameters with params expansion
        sb.Append('(');

        // Regular parameters
        if (method.Parameters.Length > 1)
        {
            var regularParams = method.Parameters.Take(method.Parameters.Length - 1);
            sb.Append(string.Join(", ", regularParams.Select(p => PrintParameter(p, resolver, ctx, allowedTypeParams))));
            sb.Append(", ");
        }

        // Params parameter with ... prefix
        var paramsParam = method.Parameters[^1];
        sb.Append("...");
        sb.Append(TypeScriptReservedWords.SanitizeParameterName(paramsParam.Name));
        sb.Append(": ");
        sb.Append(TypeRefPrinter.Print(paramsParam.Type, resolver, ctx, allowedTypeParams));

        sb.Append(')');

        // Return type
        sb.Append(": ");
        sb.Append(TypeRefPrinter.Print(method.ReturnType, resolver, ctx, allowedTypeParams));

        return sb.ToString();
    }

    /// <summary>
    /// Print multiple method overloads.
    /// Used for methods with same name but different signatures.
    /// </summary>
    public static IEnumerable<string> PrintOverloads(IEnumerable<MethodSymbol> overloads, TypeSymbol declaringType, TypeNameResolver resolver, BuildContext ctx)
    {
        foreach (var method in overloads)
        {
            yield return Print(method, declaringType, resolver, ctx);
        }
    }

    /// <summary>
    /// Print method as a property getter/setter.
    /// Used for property accessors in interfaces.
    /// </summary>
    public static string PrintAsPropertyAccessor(MethodSymbol method, bool isGetter, TypeSymbol declaringType, TypeNameResolver resolver, BuildContext ctx)
    {
        // TS2304 FIX: Compute allowed type parameters
        var allowedTypeParams = new HashSet<string>();
        foreach (var gp in declaringType.GenericParameters)
        {
            allowedTypeParams.Add(gp.Name);
        }
        foreach (var gp in method.GenericParameters)
        {
            allowedTypeParams.Add(gp.Name);
        }

        var sb = new StringBuilder();

        // Get property name from method name (get_Foo → Foo)
        var propertyName = method.ClrName;
        if (propertyName.StartsWith("get_") || propertyName.StartsWith("set_"))
            propertyName = propertyName.Substring(4);

        // Modifiers
        if (method.IsStatic)
            sb.Append("static ");

        // Property name
        sb.Append(propertyName);

        // Type
        sb.Append(": ");

        if (isGetter)
        {
            // Getter returns the property type
            sb.Append(TypeRefPrinter.Print(method.ReturnType, resolver, ctx, allowedTypeParams));
        }
        else
        {
            // Setter takes property type as parameter
            if (method.Parameters.Length > 0)
                sb.Append(TypeRefPrinter.Print(method.Parameters[0].Type, resolver, ctx, allowedTypeParams));
            else
                sb.Append("unknown"); // Fallback (never use 'any')
        }

        return sb.ToString();
    }
}
