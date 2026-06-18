using System.Collections.Generic;
using System.Linq;
using System.Text;
using DotnetBindgen.Emit.Shared;
using DotnetBindgen.Model;
using DotnetBindgen.Model.Symbols;
using DotnetBindgen.Model.Types;

namespace DotnetBindgen.Emit;

/// <summary>
/// Unified type alias emission logic.
/// Ensures consistent generic parameter handling across all alias emission sites:
/// - Facade exports
/// - Internal convenience exports
/// - View composition aliases
/// This prevents TS2315 "Type is not generic" errors by guaranteeing LHS and RHS arity match.
/// </summary>
internal static class AliasEmit
{
    /// <summary>
    /// Emits a type alias with proper generic parameter handling.
    /// Guarantees LHS and RHS have matching arity and parameter names.
    /// </summary>
    /// <param name="sb">StringBuilder to append to</param>
    /// <param name="aliasName">LHS alias name (e.g., "Foo")</param>
    /// <param name="sourceType">Source type symbol (determines arity and constraints)</param>
    /// <param name="rhsExpression">RHS expression base (e.g., "Internal.Foo" or "Foo$instance & __Foo$views")</param>
    /// <param name="resolver">Type name resolver for printing constraints</param>
    /// <param name="ctx">Build context</param>
    /// <param name="withConstraints">Whether to include constraints on LHS (default: false for simple re-exports)</param>
    /// <param name="facadeMode">When true, prefixes constraint type references with "Internal." for facade exports</param>
    internal static void EmitGenericAlias(
        StringBuilder sb,
        string aliasName,
        TypeSymbol sourceType,
        string rhsExpression,
        TypeNameResolver resolver,
        BuildContext ctx,
        bool withConstraints = false,
        bool facadeMode = false,
        SymbolGraph? graph = null)
    {
        var gps = sourceType.GenericParameters;

        // Non-generic: trivial case
        if (gps.Length == 0)
        {
            sb.Append("export type ");
            sb.Append(aliasName);
            sb.Append(" = ");
            sb.Append(rhsExpression);
            sb.AppendLine(";");
            return;
        }

        // Generic: emit with type parameters
        sb.Append("export type ");
        sb.Append(aliasName);

        // LHS: Generate type parameters (with or without constraints)
        if (withConstraints)
        {
            var typeParamsLHS = GenerateTypeParametersWithConstraints(sourceType, resolver, ctx, facadeMode, graph);
            sb.Append(typeParamsLHS);
        }
        else
        {
            // Simple parameter list without constraints
            sb.Append('<');
            sb.Append(string.Join(", ", gps.Select(gp => gp.Name)));
            sb.Append('>');
        }

        sb.Append(" = ");
        sb.Append(rhsExpression);

        // RHS: Generate type arguments (parameter names only, no constraints)
        sb.Append('<');
        sb.Append(string.Join(", ", gps.Select(gp => gp.Name)));
        sb.AppendLine(">;");
    }

    /// <summary>
    /// Generates generic type parameters WITH constraints for LHS of alias.
    /// Example: "<T extends IFoo, U extends IBar>"
    /// </summary>
    /// <param name="facadeMode">When true, prefixes constraint references with "Internal." for facade exports</param>
    internal static string GenerateTypeParametersWithConstraints(
        TypeSymbol sourceType,
        TypeNameResolver resolver,
        BuildContext ctx,
        bool facadeMode = false,
        SymbolGraph? graph = null)
    {
        var gps = sourceType.GenericParameters;
        if (gps.Length == 0)
            return string.Empty;

        var parts = new List<string>(gps.Length);

        foreach (var gp in gps)
        {
            // Collect type constraints (interfaces/classes)
            var typeConstraints = gp.Constraints
                .Where(c => c is not null && !IsSuppressedTypeConstraint(c))
                .ToList();

            if (typeConstraints.Count == 0)
            {
                parts.Add($"{gp.Name} extends {BuildConstraintText(gp, Array.Empty<string>())}");
            }
            else
            {
                // Print each constraint using TypeRefPrinter
                // PRIMITIVE CONSTRAINT RELAXATION: Widen value semantics constraints
                var constraintStrings = typeConstraints
                    .Select(c => PrintExplicitConstraint(c, gp, resolver, ctx, facadeMode, sourceType.Namespace, graph: graph))
                    .Where(c => c != "any" && !Printers.TypeRefPrinter.IsOpaqueTypeText(c))
                    .ToArray();
                parts.Add($"{gp.Name} extends {BuildConstraintText(gp, constraintStrings)}");
            }
        }

        return $"<{string.Join(", ", parts)}>";
    }

    /// <summary>
    /// Generates generic type arguments for RHS of alias.
    /// Example: "<T, U>" (parameter names only, no constraints)
    /// </summary>
    internal static string GenerateTypeArguments(TypeSymbol sourceType)
    {
        var gps = sourceType.GenericParameters;
        if (gps.Length == 0)
            return string.Empty;

        var names = gps.Select(gp => gp.Name);
        return $"<{string.Join(", ", names)}>";
    }

    /// <summary>
    /// Checks if a constraint is a special C# constraint that doesn't translate to TypeScript.
    /// Special constraints: struct (System.ValueType), class (System.Object), new()
    /// </summary>
    internal static bool IsSuppressedTypeConstraint(Model.Types.TypeReference constraint)
    {
        // The CLR "class" special constraint can surface as System.Object.
        // Suppress it here so generic parameters can use the more precise TS-side
        // reference-like fallback from GetImplicitConstraintText().
        if (constraint is Model.Types.NamedTypeReference named)
        {
            return named.FullName is "System.Object"
                && named.TypeArguments.Count == 0;
        }
        return false;
    }

    internal static string PrintExplicitConstraint(
        TypeReference constraint,
        GenericParameterSymbol gp,
        TypeNameResolver resolver,
        BuildContext ctx,
        bool facadeMode = false,
        string? sourceNamespace = null,
        string? emittedGenericParameterName = null,
        Func<string, string>? transformPrintedConstraint = null,
        SymbolGraph? graph = null)
    {
        if (IsSuppressedTypeConstraint(constraint))
            return string.Empty;

        var outputGenericParameterName = emittedGenericParameterName ?? gp.Name;

        if (IsValueSemanticsConstraint(constraint, gp.Name))
        {
            var printed = Printers.TypeRefPrinter.Print(constraint, resolver, ctx);
            if (transformPrintedConstraint != null)
                printed = transformPrintedConstraint(printed);

            if (facadeMode &&
                sourceNamespace != null &&
                RequiresFacadeInternalQualification(printed) &&
                IsSameNamespaceConstraint(constraint, sourceNamespace))
            {
                printed = "Internal." + printed;
            }

            return RelaxConstraintForPrimitives(printed, outputGenericParameterName);
        }

        var nominalConstraint = TryPrintNominalConstraint(constraint, ctx, graph);
        if (nominalConstraint != null)
            return nominalConstraint;

        var fallback = Printers.TypeRefPrinter.Print(constraint, resolver, ctx);
        if (transformPrintedConstraint != null)
            fallback = transformPrintedConstraint(fallback);

        if (fallback.TrimStart().StartsWith("extends ", StringComparison.Ordinal))
            return string.Empty;

        if (facadeMode &&
            sourceNamespace != null &&
            RequiresFacadeInternalQualification(fallback) &&
            IsSameNamespaceConstraint(constraint, sourceNamespace))
        {
            fallback = "Internal." + fallback;
        }

        return fallback;
    }

    private static string? TryPrintNominalConstraint(TypeReference constraint, BuildContext ctx, SymbolGraph? graph)
    {
        var named = constraint switch
        {
            NamedTypeReference n => n,
            NestedTypeReference nested => nested.FullReference,
            _ => null
        };

        if (named == null)
            return null;

        var canonical = StripAssemblyQualification(named.FullName);
        var graphType = FindTypeByClrFullName(graph, canonical);
        var isInterface =
            named.InterfaceStableId != null ||
            graphType?.Kind == TypeKind.Interface;

        var brandNames = isInterface
            ? [NameUtilities.GetClrInterfaceBrandPropertyName(canonical)]
            : GetNominalTypeConstraintBrandNames(canonical, ctx, graph);

        return string.Join(" & ", brandNames.Select(brandName => $"{{ readonly {brandName}: never }}"));
    }

    private static IReadOnlyList<string> GetNominalTypeConstraintBrandNames(string clrFullName, BuildContext ctx, SymbolGraph? graph)
    {
        var fullNames = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void AddType(string fullName)
        {
            var canonical = StripAssemblyQualification(fullName);
            if (canonical == "System.Object" || !visited.Add(canonical))
                return;

            fullNames.Add(canonical);

            var symbol = FindTypeByClrFullName(graph, canonical);
            if (symbol != null)
            {
                if (symbol.Kind == TypeKind.Struct)
                    fullNames.Add("System.ValueType");

                var baseFullName = GetNamedTypeFullName(symbol.BaseType);
                if (baseFullName != null)
                    AddType(baseFullName);
                return;
            }

            if (ctx.LibraryContract?.BaseClrFullNameByClrFullName.TryGetValue(canonical, out var externalBaseFullName) == true)
                AddType(externalBaseFullName);
        }

        AddType(clrFullName);
        return fullNames.Select(NameUtilities.GetClrTypeBrandPropertyName).ToArray();
    }

    private static TypeSymbol? FindTypeByClrFullName(SymbolGraph? graph, string clrFullName)
    {
        if (graph == null)
            return null;

        var canonical = StripAssemblyQualification(clrFullName);
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                if (type.ClrFullName == canonical)
                    return type;

                var nested = FindNestedTypeByClrFullName(type, canonical);
                if (nested != null)
                    return nested;
            }
        }

        if (graph.TypeIndex.TryGetValue(canonical, out var indexed))
            return indexed;

        return null;
    }

    private static TypeSymbol? FindNestedTypeByClrFullName(TypeSymbol type, string clrFullName)
    {
        foreach (var nested in type.NestedTypes)
        {
            if (nested.ClrFullName == clrFullName)
                return nested;

            var match = FindNestedTypeByClrFullName(nested, clrFullName);
            if (match != null)
                return match;
        }

        return null;
    }

    private static string? GetNamedTypeFullName(TypeReference? typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            _ => null
        };
    }

    internal static string GetImplicitConstraintText(GenericParameterSymbol gp)
    {
        var special = gp.SpecialConstraints;

        if ((special & GenericParameterConstraints.ValueType) != 0)
            return $"{{ readonly {NameUtilities.GetClrTypeBrandPropertyName("System.ValueType")}: never }}";

        if ((special & GenericParameterConstraints.ReferenceType) != 0)
        {
            return (special & GenericParameterConstraints.NotNullable) != 0
                ? "object"
                : "object | null";
        }

        if ((special & GenericParameterConstraints.NotNullable) != 0)
            return "NonNullable<unknown>";

        return "unknown";
    }

    internal static bool ContainsGenericParameter(TypeReference typeRef, string name)
    {
        return typeRef switch
        {
            GenericParameterReference gp => gp.Name == name,
            NamedTypeReference named => named.TypeArguments.Any(arg => ContainsGenericParameter(arg, name)),
            ArrayTypeReference array => ContainsGenericParameter(array.ElementType, name),
            PointerTypeReference pointer => ContainsGenericParameter(pointer.PointeeType, name),
            ByRefTypeReference byref => ContainsGenericParameter(byref.ReferencedType, name),
            NestedTypeReference nested => nested.FullReference.TypeArguments.Any(arg => ContainsGenericParameter(arg, name)),
            FunctionPointerTypeReference fnptr =>
                ContainsGenericParameter(fnptr.ReturnType, name) ||
                fnptr.ParameterTypes.Any(arg => ContainsGenericParameter(arg, name)) ||
                fnptr.CallingConventionTypes.Any(arg => ContainsGenericParameter(arg, name)),
            _ => false
        };
    }

    private static string StripAssemblyQualification(string fullName)
    {
        var commaIndex = fullName.IndexOf(',');
        return commaIndex >= 0 ? fullName.Substring(0, commaIndex).Trim() : fullName;
    }

    internal static string BuildConstraintText(
        GenericParameterSymbol gp,
        IEnumerable<string> explicitConstraints)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddPart(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
                return;

            if (!IsValidConstraintTypeExpression(trimmed))
                return;

            var wrapped = WrapConstraintIfNeeded(trimmed);
            if (seen.Add(wrapped))
                normalized.Add(wrapped);
        }

        AddPart(GetImplicitConstraintText(gp));

        foreach (var explicitConstraint in explicitConstraints)
            AddPart(explicitConstraint);

        return string.Join(" & ", normalized);
    }

    private static bool IsValidConstraintTypeExpression(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("extends ", StringComparison.Ordinal))
            return false;

        if (trimmed.Contains(" extends ", StringComparison.Ordinal))
            return false;

        if (trimmed.Contains(" implements ", StringComparison.Ordinal))
            return false;

        return true;
    }

    internal static string WrapConstraintIfNeeded(string text)
    {
        if (text.IndexOf('|') >= 0 && !(text.StartsWith("(") && text.EndsWith(")")))
            return $"({text})";

        return text;
    }

    internal static bool RequiresFacadeInternalQualification(string printedConstraint)
    {
        if (string.IsNullOrWhiteSpace(printedConstraint))
            return false;

        if (printedConstraint.StartsWith("Internal.", StringComparison.Ordinal))
            return false;

        if (printedConstraint.Contains('.', StringComparison.Ordinal))
            return false;

        if (printedConstraint.StartsWith("__OpaqueClrType<", StringComparison.Ordinal))
            return false;

        if (printedConstraint == "unknown" ||
            printedConstraint == "object" ||
            printedConstraint == "Function" ||
            printedConstraint == "void" ||
            printedConstraint == "never" ||
            printedConstraint == "null" ||
            printedConstraint == "undefined" ||
            printedConstraint.StartsWith("ptr<", StringComparison.Ordinal) ||
            printedConstraint.StartsWith("fnptr<", StringComparison.Ordinal) ||
            printedConstraint.StartsWith("NonNullable<", StringComparison.Ordinal))
        {
            return false;
        }

        if (PrimitiveLift.GetTsCarrierKinds().Contains(printedConstraint, StringComparer.Ordinal))
            return false;

        return true;
    }

    private static bool IsSameNamespaceConstraint(TypeReference typeRef, string currentNamespace)
    {
        return typeRef switch
        {
            NamedTypeReference named => GetNamespaceFromClrFullName(named.FullName) == currentNamespace,
            NestedTypeReference nested => GetNamespaceFromClrFullName(nested.FullReference.FullName) == currentNamespace,
            ArrayTypeReference array => IsSameNamespaceConstraint(array.ElementType, currentNamespace),
            ByRefTypeReference byref => IsSameNamespaceConstraint(byref.ReferencedType, currentNamespace),
            PointerTypeReference pointer => IsSameNamespaceConstraint(pointer.PointeeType, currentNamespace),
            _ => false
        };
    }

    private static string GetNamespaceFromClrFullName(string clrFullName)
    {
        var canonical = clrFullName;
        var commaIndex = canonical.IndexOf(',');
        if (commaIndex >= 0)
            canonical = canonical.Substring(0, commaIndex).Trim();

        var nestedIndex = canonical.IndexOf('+');
        if (nestedIndex >= 0)
            canonical = canonical.Substring(0, nestedIndex);

        var lastDot = canonical.LastIndexOf('.');
        return lastDot < 0 ? string.Empty : canonical.Substring(0, lastDot);
    }

    /// <summary>
    /// PRIMITIVE CONSTRAINT RELAXATION: Checks if a constraint is a CLR "value semantics" interface
    /// that requires relaxation to admit TS primitives (branded number/string/boolean).
    ///
    /// When CLR primitives like Int32 are emitted as simple type aliases (Int32 = int),
    /// the branded primitives don't structurally satisfy interfaces like IEquatable_1<T>
    /// because they lack the Equals() method. To fix this, we widen such constraints:
    ///   T extends IEquatable_1<T>  →  T extends (IEquatable_1<T> | number | string | boolean)
    ///
    /// This allows:
    /// - Branded numerics (byte, int, etc.) to satisfy via | number
    /// - Branded char to satisfy via | string
    /// - Boolean to satisfy via | boolean
    /// </summary>
    /// <param name="constraint">The constraint type reference</param>
    /// <param name="typeParamName">The name of the type parameter being constrained (e.g., "T")</param>
    /// <returns>True if this is a value semantics constraint that needs relaxation</returns>
    internal static bool IsValueSemanticsConstraint(TypeReference constraint, string typeParamName)
    {
        if (constraint is not NamedTypeReference named)
            return false;

        var fullName = named.FullName;

        // IEquatable<T> where T is the same type parameter
        if (fullName == "System.IEquatable`1" && named.TypeArguments.Count == 1)
        {
            // Check if the type argument is the same type parameter
            if (named.TypeArguments[0] is GenericParameterReference gpRef &&
                gpRef.Name == typeParamName)
            {
                return true;
            }
        }

        // IComparable<T> where T is the same type parameter
        if (fullName == "System.IComparable`1" && named.TypeArguments.Count == 1)
        {
            if (named.TypeArguments[0] is GenericParameterReference gpRef &&
                gpRef.Name == typeParamName)
            {
                return true;
            }
        }

        // IComparable (non-generic) - always relax
        if (fullName == "System.IComparable" && named.TypeArguments.Count == 0)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// The primitive carrier union, derived from PrimitiveLift.Rules.
    /// Cached for performance since it's computed once and used repeatedly.
    /// Per @tsonic/core/types.js: "number | string | boolean" (no bigint - all numerics are number-carried)
    /// </summary>
    private static readonly string PrimitiveCarrierUnion =
        string.Join(" | ", PrimitiveLift.GetTsCarrierKinds());

    /// <summary>
    /// Relaxes a value semantics constraint by adding primitive type alternatives.
    ///
    /// IEquatable_1&lt;T&gt; becomes:
    ///   (IEquatable_1&lt;T&gt; | number | string | boolean)
    ///
    /// NOTE: A tighter conditional pattern like `(T extends number ? number : never)` would
    /// preserve full fidelity for non-primitives, but TypeScript treats this as a circular
    /// constraint (TS2313). The simple union pattern is safe because:
    /// - It only affects constraint satisfaction, not the actual type of T
    /// - The practical set of types that pass the union matches CLR primitives
    /// - Non-primitives still work correctly (they satisfy IEquatable_1&lt;T&gt; structurally)
    ///
    /// The carrier union is derived from PrimitiveLift.Rules to guarantee it covers
    /// all primitives as the mapping evolves (per @tsonic/core/types.js contract).
    /// </summary>
    /// <param name="printedConstraint">The already-printed constraint string</param>
    /// <param name="typeParamName">The type parameter name (reserved for future use)</param>
    /// <returns>The relaxed constraint with primitive alternatives</returns>
    internal static string RelaxConstraintForPrimitives(string printedConstraint, string typeParamName = "T")
    {
        // Union derived from PrimitiveLift.Rules - guaranteed to cover all primitives
        return $"({printedConstraint} | {PrimitiveCarrierUnion})";
    }
}
