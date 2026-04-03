using System.Collections.Immutable;
using System.Text;
using tsbindgen.Model.Symbols;
using tsbindgen.Emit.Printers;

namespace tsbindgen.Emit;

/// <summary>
/// Emits TypeScript sentinel-ladder aliases for multi-arity type families.
///
/// The sentinel-ladder pattern uses a unique symbol to detect unspecified type parameters,
/// allowing a single type alias to dispatch to the correct arity at compile time:
///
///   declare const __unspecified: unique symbol;
///   export type __ = typeof __unspecified;
///
///   export type ValueTuple&lt;T1 = __, T2 = __, ...&gt; =
///     [T1] extends [__] ? Internal.ValueTuple :
///     [T2] extends [__] ? Internal.ValueTuple_1&lt;T1&gt; :
///     Internal.ValueTuple_2&lt;T1, T2&gt;;
///
/// For delegate families (Action, Func), callable signatures are included to support
/// TypeScript lambda assignment.
/// </summary>
public static class MultiArityAliasEmit
{
    /// <summary>
    /// Emit the sentinel symbol declaration. Should be emitted once per file
    /// before any multi-arity family aliases.
    /// </summary>
    public static void EmitSentinelDeclaration(StringBuilder sb)
    {
        sb.AppendLine("// Multi-arity family sentinel (detects unspecified type parameters)");
        sb.AppendLine("declare const __unspecified: unique symbol;");
        sb.AppendLine("export type __ = typeof __unspecified;");
        sb.AppendLine();
    }

    /// <summary>
    /// Emit a sentinel-ladder alias for a multi-arity family.
    /// Handles both delegate (callable) and non-delegate families.
    /// </summary>
    public static void Emit(StringBuilder sb, MultiArityFamily family, TypeNameResolver resolver, BuildContext ctx, string? currentNamespace = null)
    {
        if (family.IsDelegateFamily)
            EmitDelegateFamily(sb, family, resolver, ctx, currentNamespace);
        else
            EmitNonDelegateFamily(sb, family, resolver, ctx, currentNamespace);
    }

    /// <summary>
    /// Emit a sentinel-ladder alias for a non-delegate family (e.g., ValueTuple, Tuple).
    /// Uses explicit k-based generation from MinArity to MaxArity.
    /// Propagates type constraints from max-arity member to facade parameters.
    /// </summary>
    private static void EmitNonDelegateFamily(StringBuilder sb, MultiArityFamily family, TypeNameResolver resolver, BuildContext ctx, string? currentNamespace)
    {
        var minArity = family.MinArity;
        var maxArity = family.MaxArity;

        // Get constraints from max-arity member (most complete constraint info)
        var maxArityMember = family.Members.First(m => m.Arity == maxArity);
        var constraints = maxArityMember.GenericParameters;

        if (minArity == maxArity)
        {
            // Single member only - emit simple type alias with constraints
            var member = family.Members[0];
            if (member.Arity == 0)
            {
                sb.AppendLine($"export type {family.PublicStem} = Internal.{member.InternalExportName};");
            }
            else
            {
                // For single-member generic families, use extends clause (no sentinel dispatch)
                var typeArgs = string.Join(", ", Enumerable.Range(1, member.Arity).Select(n => FormatTypeParamWithConstraint(n, constraints, resolver, ctx, currentNamespace)));
                sb.AppendLine($"export type {family.PublicStem}<{typeArgs}> = Internal.{member.InternalExportName}<{string.Join(", ", Enumerable.Range(1, member.Arity).Select(n => $"T{n}"))}>;");
            }
            sb.AppendLine();
            return;
        }

        // Type parameters: T1 through TmaxArity, all defaulting to __ while still carrying
        // the same broad-value closure as the selected internal arity member.
        sb.AppendLine($"export type {family.PublicStem}<");
        for (int i = 1; i <= maxArity; i++)
        {
            sb.AppendLine($"  {FormatTypeParamWithConstraint(i, constraints, resolver, ctx, currentNamespace)} = __,");
        }
        sb.AppendLine("> =");

        // Explicit k-based conditional ladder with constraint guards
        // For each arity k from minArity to maxArity:
        //   [T{k+1}] extends [__] ? (nested constraint check with result) :
        // The constraint guard ensures types passed to constrained internal types satisfy those constraints
        for (int k = minArity; k <= maxArity; k++)
        {
            var member = family.Members.First(m => m.Arity == k);
            var typeArgs = k == 0
                ? ""
                : $"<{string.Join(", ", Enumerable.Range(1, k).Select(n => $"T{n}"))}>";

            var internalType = $"Internal.{member.InternalExportName}{typeArgs}";

            // Build nested constraint check that returns internal type or never
            var constraintCheck = BuildNestedConstraintCheck(k, member.GenericParameters, resolver, ctx, internalType, currentNamespace);
            var resultExpr = constraintCheck ?? internalType;

            if (k < maxArity)
            {
                // Condition: is T{k+1} unspecified?
                var conditionIndex = k + 1;
                sb.AppendLine($"  [T{conditionIndex}] extends [__] ? {resultExpr} :");
            }
            else
            {
                // Last branch
                sb.AppendLine($"  {resultExpr};");
            }
        }
        sb.AppendLine();
    }

    /// <summary>
    /// Emit a sentinel-ladder alias for a delegate family (Action, Func).
    /// Includes callable signatures for TypeScript lambda compatibility:
    ///   ((...args) => ReturnType) | Internal.Delegate_N&lt;...&gt;
    /// Uses explicit k-based generation from MinArity to MaxArity.
    /// Propagates type constraints from max-arity member to facade parameters.
    /// </summary>
    private static void EmitDelegateFamily(StringBuilder sb, MultiArityFamily family, TypeNameResolver resolver, BuildContext ctx, string? currentNamespace)
    {
        // Func-like delegates have return type as last type parameter
        // Detect by checking if CLR base name ends with "Func"
        var isFunc = family.ClrBaseName.EndsWith("Func", StringComparison.Ordinal);
        var minArity = family.MinArity;
        var maxArity = family.MaxArity;

        // Get constraints from max-arity member (most complete constraint info)
        var maxArityMember = family.Members.First(m => m.Arity == maxArity);
        var constraints = maxArityMember.GenericParameters;

        if (minArity == maxArity)
        {
            // Single member only with constraints
            var member = family.Members[0];
            var arity = member.Arity;
            var callSig = BuildCallableSignature(arity, isFunc);
            if (arity == 0)
            {
                sb.AppendLine($"export type {family.PublicStem} = (({callSig}) | Internal.{member.InternalExportName});");
            }
            else
            {
                // For single-member generic families, use extends clause (no sentinel dispatch)
                var typeArgs = string.Join(", ", Enumerable.Range(1, arity).Select(n => FormatTypeParamWithConstraint(n, constraints, resolver, ctx, currentNamespace)));
                sb.AppendLine($"export type {family.PublicStem}<{typeArgs}> = (({callSig}) | Internal.{member.InternalExportName}<{string.Join(", ", Enumerable.Range(1, arity).Select(n => $"T{n}"))}>);");
            }
            sb.AppendLine();
            return;
        }

        // Type parameters: T1 through TmaxArity, all defaulting to __ while still carrying
        // the same broad-value closure as the selected internal arity member.
        sb.AppendLine($"export type {family.PublicStem}<");
        for (int i = 1; i <= maxArity; i++)
        {
            sb.AppendLine($"  {FormatTypeParamWithConstraint(i, constraints, resolver, ctx, currentNamespace)} = __,");
        }
        sb.AppendLine("> =");

        // Explicit k-based conditional ladder with callable signatures and constraint guards
        for (int k = minArity; k <= maxArity; k++)
        {
            var member = family.Members.First(m => m.Arity == k);
            var callSig = BuildCallableSignature(k, isFunc);
            var typeArgs = k == 0
                ? ""
                : $"<{string.Join(", ", Enumerable.Range(1, k).Select(n => $"T{n}"))}>";

            var delegateType = $"(({callSig}) | Internal.{member.InternalExportName}{typeArgs})";

            // Build nested constraint check that returns delegate type or never
            var constraintCheck = BuildNestedConstraintCheck(k, member.GenericParameters, resolver, ctx, delegateType, currentNamespace);
            var resultExpr = constraintCheck ?? delegateType;

            if (k < maxArity)
            {
                var conditionIndex = k + 1;
                sb.AppendLine($"  [T{conditionIndex}] extends [__] ? {resultExpr} :");
            }
            else
            {
                sb.AppendLine($"  {resultExpr};");
            }
        }
        sb.AppendLine();
    }

    /// <summary>
    /// Build a callable signature for a delegate with given arity.
    /// For Func-like: last type param is return type.
    /// For Action-like: all params are arguments, returns void.
    /// </summary>
    private static string BuildCallableSignature(int arity, bool isFunc)
    {
        if (isFunc && arity >= 1)
        {
            // Func: last type param is return type
            var argCount = arity - 1;
            var retIdx = arity;
            return argCount == 0
                ? $"() => T{retIdx}"
                : $"({string.Join(", ", Enumerable.Range(1, argCount).Select(n => $"arg{n}: T{n}"))}) => T{retIdx}";
        }
        else
        {
            // Action: all type params are arguments, returns void
            return arity == 0
                ? "() => void"
                : $"({string.Join(", ", Enumerable.Range(1, arity).Select(n => $"arg{n}: T{n}"))}) => void";
        }
    }

    /// <summary>
    /// Format a type parameter with constraint for single-arity facade (e.g., "T1 extends IEquatable_1<T1>").
    /// </summary>
    private static string FormatTypeParamWithConstraint(
        int position,
        ImmutableArray<GenericParameterSymbol> constraints,
        TypeNameResolver resolver,
        BuildContext ctx,
        string? currentNamespace = null)
    {
        var paramName = $"T{position}";
        var constraintPart = FormatConstraintForPosition(position, constraints, resolver, ctx, currentNamespace);
        return $"{paramName}{constraintPart}";
    }

    /// <summary>
    /// Format the constraint portion for a type parameter at given position.
    /// Returns " extends (Constraint | __)" if constraint exists, empty string otherwise.
    /// The union with __ allows the sentinel default value to satisfy the constraint.
    /// Uses primitive constraint relaxation from AliasEmit.
    /// </summary>
    private static string FormatConstraintForPosition(
        int position,
        ImmutableArray<GenericParameterSymbol> genericParams,
        TypeNameResolver resolver,
        BuildContext ctx,
        string? currentNamespace = null)
    {
        // Position is 1-based, array is 0-based
        var index = position - 1;
        if (index < 0 || index >= genericParams.Length)
            return "";

        var gp = genericParams[index];
        if (gp.Constraints.Length == 0)
            return $" extends {AliasEmit.WrapConstraintIfNeeded(AliasEmit.BuildConstraintText(gp, Array.Empty<string>()))} | __";

        // The facade parameter name is T{position}, need to substitute original param name
        var facadeParamName = $"T{position}";

        // Print and relax constraints (same logic as InternalIndexEmitter)
        var constraintStrings = gp.Constraints.Select(c =>
        {
            // Print the constraint, substituting original param name with facade name
            var printed = TypeRefPrinter.Print(c, resolver, ctx);

            // Replace original param name with facade param name in the constraint
            // e.g., IEquatable_1<T> becomes IEquatable_1<T1>
            if (gp.Name != facadeParamName)
            {
                printed = SubstituteTypeParam(printed, gp.Name, facadeParamName);
            }

            if (currentNamespace != null &&
                printed != "never" && printed != "any" &&
                !TypeRefPrinter.IsOpaqueTypeText(printed) &&
                AliasEmit.RequiresFacadeInternalQualification(printed) &&
                IsSameNamespace(c, currentNamespace))
            {
                printed = $"Internal.{printed}";
            }

            // Relax value semantics constraints for primitives
            if (AliasEmit.IsValueSemanticsConstraint(c, facadeParamName))
            {
                return AliasEmit.RelaxConstraintForPrimitives(printed, facadeParamName);
            }

            return printed;
        })
        .Where(c => c != "any" && !TypeRefPrinter.IsOpaqueTypeText(c))
        .ToList();

        var constraintText = AliasEmit.BuildConstraintText(gp, constraintStrings);
        return $" extends {AliasEmit.WrapConstraintIfNeeded(constraintText)} | __";
    }

    /// <summary>
    /// Build a nested constraint check that returns resultExpr if all constraints pass, otherwise never.
    /// Returns the complete nested conditional: [T1] extends [C1] ? [T2] extends [C2] ? resultExpr : never : never
    /// Returns null if no constraints exist (caller should use resultExpr directly).
    /// </summary>
    private static string? BuildNestedConstraintCheck(
        int arity,
        ImmutableArray<GenericParameterSymbol> genericParams,
        TypeNameResolver resolver,
        BuildContext ctx,
        string resultExpr,
        string? currentNamespace = null)
    {
        if (genericParams.Length == 0)
            return null;

        var guards = new List<string>();

        for (int i = 0; i < arity && i < genericParams.Length; i++)
        {
            var gp = genericParams[i];
            var position = i + 1; // 1-based
            var facadeParamName = $"T{position}";

            // Print and relax constraints (same logic as FormatConstraintForPosition)
            var constraintStrings = gp.Constraints.Select(c =>
            {
                var printed = TypeRefPrinter.Print(c, resolver, ctx);

                // Replace original param name with facade param name
                if (gp.Name != facadeParamName)
                {
                    printed = SubstituteTypeParam(printed, gp.Name, facadeParamName);
                }

                // FACADE FIX: Same-namespace constraint types need Internal. prefix
                // Re-exported types in facade aren't locally available, but Internal.* is
                // Only prefix if constraint type is from same namespace
                // Skip built-in TS types (never, any) and explicit opaque placeholders
                if (currentNamespace != null &&
                    printed != "never" && printed != "any" &&
                    !TypeRefPrinter.IsOpaqueTypeText(printed) &&
                    AliasEmit.RequiresFacadeInternalQualification(printed) &&
                    IsSameNamespace(c, currentNamespace))
                {
                    printed = $"Internal.{printed}";
                }

                // Relax value semantics constraints for primitives
                if (AliasEmit.IsValueSemanticsConstraint(c, facadeParamName))
                {
                    return AliasEmit.RelaxConstraintForPrimitives(printed, facadeParamName);
                }

                return printed;
            })
            .Where(c => c != "any" && !TypeRefPrinter.IsOpaqueTypeText(c))
            .ToList();

            var constraintText = AliasEmit.BuildConstraintText(gp, constraintStrings);
            guards.Add($"[{facadeParamName}] extends [{constraintText}]");
        }

        if (guards.Count == 0)
            return null;

        // Build nested conditional: [T1] extends [C1] ? [T2] extends [C2] ? resultExpr : never : never
        // Start with the result and wrap backwards with each guard
        var result = resultExpr;
        for (int i = guards.Count - 1; i >= 0; i--)
        {
            result = $"{guards[i]} ? {result} : never";
        }
        return result;
    }

    /// <summary>
    /// Check if a constraint type reference is from the same namespace as current.
    /// Used to determine if Internal. prefix is needed in facade constraint guards.
    /// </summary>
    private static bool IsSameNamespace(Model.Types.TypeReference typeRef, string currentNamespace)
    {
        if (typeRef is not Model.Types.NamedTypeReference named)
            return false;

        // Skip TypeScript built-in types (never, any)
        if (named.FullName == "never" || named.FullName == "any")
            return false;

        // Extract namespace from CLR full name
        var fullName = named.FullName;

        // Remove assembly suffix if present (e.g., "System.Data.DataRow, System.Data")
        var commaIndex = fullName.IndexOf(',');
        if (commaIndex >= 0)
        {
            fullName = fullName.Substring(0, commaIndex).Trim();
        }

        // Extract namespace from type name
        var lastDot = fullName.LastIndexOf('.');
        var typeNamespace = lastDot >= 0 ? fullName.Substring(0, lastDot) : "";

        return typeNamespace == currentNamespace;
    }

    /// <summary>
    /// Substitute type parameter name in a printed constraint.
    /// E.g., "IEquatable_1<T>" with oldName="T", newName="T1" becomes "IEquatable_1<T1>"
    /// </summary>
    private static string SubstituteTypeParam(string printed, string oldName, string newName)
    {
        // Simple word-boundary replacement for the type parameter
        // Need to be careful not to replace partial matches (e.g., "TKey" when replacing "T")
        var result = new StringBuilder();
        var i = 0;
        while (i < printed.Length)
        {
            // Check if we're at a potential match position
            if (i + oldName.Length <= printed.Length &&
                printed.Substring(i, oldName.Length) == oldName)
            {
                // Check for word boundary before
                var charBefore = i > 0 ? printed[i - 1] : ' ';
                var charAfter = i + oldName.Length < printed.Length ? printed[i + oldName.Length] : ' ';

                // Type param is a word if surrounded by non-identifier chars
                bool isWordBoundaryBefore = !char.IsLetterOrDigit(charBefore) && charBefore != '_';
                bool isWordBoundaryAfter = !char.IsLetterOrDigit(charAfter) && charAfter != '_';

                if (isWordBoundaryBefore && isWordBoundaryAfter)
                {
                    result.Append(newName);
                    i += oldName.Length;
                    continue;
                }
            }
            result.Append(printed[i]);
            i++;
        }
        return result.ToString();
    }
}
