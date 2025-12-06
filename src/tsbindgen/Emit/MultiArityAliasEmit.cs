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
    /// Uses explicit k-based generation from MinArity to MaxArity.
    /// </summary>
    private static void EmitNonDelegateFamily(StringBuilder sb, MultiArityFamily family, BuildContext ctx)
    {
        var minArity = family.MinArity;
        var maxArity = family.MaxArity;

        if (minArity == maxArity)
        {
            // Single member only - emit simple type alias
            var member = family.Members[0];
            var typeArgs = member.Arity == 0
                ? ""
                : $"<{string.Join(", ", Enumerable.Range(1, member.Arity).Select(n => $"T{n}"))}>";
            sb.AppendLine($"export type {family.PublicStem}{typeArgs} = Internal.{member.InternalExportName}{typeArgs};");
            sb.AppendLine();
            return;
        }

        // Type parameters: T1 through TmaxArity, all defaulting to __
        sb.AppendLine($"export type {family.PublicStem}<");
        for (int i = 1; i <= maxArity; i++)
        {
            sb.AppendLine($"  T{i} = __,");
        }
        sb.AppendLine("> =");

        // Explicit k-based conditional ladder
        // For each arity k from minArity to maxArity-1:
        //   [T{k+1}] extends [__] ? Internal.Stem_k<T1..Tk> :
        // Final branch (arity = maxArity):
        //   Internal.Stem_max<T1..Tmax>;
        for (int k = minArity; k <= maxArity; k++)
        {
            var member = family.Members.First(m => m.Arity == k);
            var typeArgs = k == 0
                ? ""
                : $"<{string.Join(", ", Enumerable.Range(1, k).Select(n => $"T{n}"))}>";

            if (k < maxArity)
            {
                // Condition: is T{k+1} unspecified?
                var conditionIndex = k + 1;
                sb.AppendLine($"  [T{conditionIndex}] extends [__] ? Internal.{member.InternalExportName}{typeArgs} :");
            }
            else
            {
                // Last branch - no condition
                sb.AppendLine($"  Internal.{member.InternalExportName}{typeArgs};");
            }
        }
        sb.AppendLine();
    }

    /// <summary>
    /// Emit a sentinel-ladder alias for a delegate family (Action, Func).
    /// Includes callable signatures for TypeScript lambda compatibility:
    ///   ((...args) => ReturnType) | Internal.Delegate_N&lt;...&gt;
    /// Uses explicit k-based generation from MinArity to MaxArity.
    /// </summary>
    private static void EmitDelegateFamily(StringBuilder sb, MultiArityFamily family, BuildContext ctx)
    {
        // Func-like delegates have return type as last type parameter
        // Detect by checking if CLR base name ends with "Func"
        var isFunc = family.ClrBaseName.EndsWith("Func", StringComparison.Ordinal);
        var minArity = family.MinArity;
        var maxArity = family.MaxArity;

        if (minArity == maxArity)
        {
            // Single member only
            var member = family.Members[0];
            var arity = member.Arity;
            var callSig = BuildCallableSignature(arity, isFunc);
            var typeArgs = arity == 0
                ? ""
                : $"<{string.Join(", ", Enumerable.Range(1, arity).Select(n => $"T{n}"))}>";
            sb.AppendLine($"export type {family.PublicStem}{typeArgs} = (({callSig}) | Internal.{member.InternalExportName}{typeArgs});");
            sb.AppendLine();
            return;
        }

        // Type parameters: T1 through TmaxArity, all defaulting to __
        sb.AppendLine($"export type {family.PublicStem}<");
        for (int i = 1; i <= maxArity; i++)
        {
            sb.AppendLine($"  T{i} = __,");
        }
        sb.AppendLine("> =");

        // Explicit k-based conditional ladder with callable signatures
        for (int k = minArity; k <= maxArity; k++)
        {
            var member = family.Members.First(m => m.Arity == k);
            var callSig = BuildCallableSignature(k, isFunc);
            var typeArgs = k == 0
                ? ""
                : $"<{string.Join(", ", Enumerable.Range(1, k).Select(n => $"T{n}"))}>";

            if (k < maxArity)
            {
                var conditionIndex = k + 1;
                sb.AppendLine($"  [T{conditionIndex}] extends [__] ? (({callSig}) | Internal.{member.InternalExportName}{typeArgs}) :");
            }
            else
            {
                sb.AppendLine($"  (({callSig}) | Internal.{member.InternalExportName}{typeArgs});");
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
}
