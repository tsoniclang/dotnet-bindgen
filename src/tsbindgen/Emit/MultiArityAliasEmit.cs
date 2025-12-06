using System.Text;

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
    public static void Emit(StringBuilder sb, MultiArityFamily family, BuildContext ctx)
    {
        if (family.IsDelegateFamily)
            EmitDelegateFamily(sb, family, ctx);
        else
            EmitNonDelegateFamily(sb, family, ctx);
    }

    /// <summary>
    /// Emit a sentinel-ladder alias for a non-delegate family (e.g., ValueTuple, Tuple).
    /// Maps each arity to the corresponding internal type.
    /// </summary>
    private static void EmitNonDelegateFamily(StringBuilder sb, MultiArityFamily family, BuildContext ctx)
    {
        var maxArity = family.MaxArity;

        if (maxArity == 0)
        {
            // Single non-generic member only - emit simple type alias
            var member = family.Members[0];
            sb.AppendLine($"export type {family.PublicStem} = Internal.{member.InternalExportName};");
            sb.AppendLine();
            return;
        }

        // Type parameters with defaults
        sb.AppendLine($"export type {family.PublicStem}<");
        for (int i = 1; i <= maxArity; i++)
        {
            sb.AppendLine($"  T{i} = __,");
        }
        sb.AppendLine("> =");

        // Conditional ladder - each branch checks if the NEXT parameter is unspecified
        for (int i = 0; i < family.Members.Length; i++)
        {
            var member = family.Members[i];
            var typeArgs = member.Arity == 0
                ? ""
                : $"<{string.Join(", ", Enumerable.Range(1, member.Arity).Select(n => $"T{n}"))}>";
            var isLast = i == family.Members.Length - 1;

            if (!isLast)
            {
                // Check if the NEXT parameter slot is unspecified
                var conditionIndex = member.Arity + 1;
                sb.AppendLine($"  [T{conditionIndex}] extends [__] ? Internal.{member.InternalExportName}{typeArgs} :");
            }
            else
            {
                // Last branch - no condition needed
                sb.AppendLine($"  Internal.{member.InternalExportName}{typeArgs};");
            }
        }
        sb.AppendLine();
    }

    /// <summary>
    /// Emit a sentinel-ladder alias for a delegate family (Action, Func).
    /// Includes callable signatures for TypeScript lambda compatibility:
    ///   ((...args) => ReturnType) | Internal.Delegate_N&lt;...&gt;
    /// </summary>
    private static void EmitDelegateFamily(StringBuilder sb, MultiArityFamily family, BuildContext ctx)
    {
        // Func-like delegates have return type as last type parameter
        // Detect by checking if CLR base name ends with "Func"
        var isFunc = family.ClrBaseName.EndsWith("Func", StringComparison.Ordinal);
        var maxArity = family.MaxArity;

        if (maxArity == 0)
        {
            // Non-generic delegate (e.g., Action with no type params)
            var member = family.Members[0];
            sb.AppendLine($"export type {family.PublicStem} = ((() => void) | Internal.{member.InternalExportName});");
            sb.AppendLine();
            return;
        }

        // Type parameters with defaults
        sb.AppendLine($"export type {family.PublicStem}<");
        for (int i = 1; i <= maxArity; i++)
        {
            sb.AppendLine($"  T{i} = __,");
        }
        sb.AppendLine("> =");

        // Conditional ladder with callable signatures
        for (int i = 0; i < family.Members.Length; i++)
        {
            var member = family.Members[i];
            var arity = member.Arity;
            var isLast = i == family.Members.Length - 1;

            // Build callable signature
            string callSig;
            if (isFunc && arity >= 1)
            {
                // Func: last type param is return type
                var argCount = arity - 1;
                var retIdx = arity;
                callSig = argCount == 0
                    ? $"() => T{retIdx}"
                    : $"({string.Join(", ", Enumerable.Range(1, argCount).Select(n => $"arg{n}: T{n}"))}) => T{retIdx}";
            }
            else
            {
                // Action: all type params are arguments, returns void
                callSig = arity == 0
                    ? "() => void"
                    : $"({string.Join(", ", Enumerable.Range(1, arity).Select(n => $"arg{n}: T{n}"))}) => void";
            }

            var typeArgs = arity == 0
                ? ""
                : $"<{string.Join(", ", Enumerable.Range(1, arity).Select(n => $"T{n}"))}>";

            if (!isLast)
            {
                // Check if the NEXT parameter slot is unspecified
                var condIdx = arity + 1;
                sb.AppendLine($"  [T{condIdx}] extends [__] ? (({callSig}) | Internal.{member.InternalExportName}{typeArgs}) :");
            }
            else
            {
                // Last branch - no condition needed
                sb.AppendLine($"  (({callSig}) | Internal.{member.InternalExportName}{typeArgs});");
            }
        }
        sb.AppendLine();
    }
}
