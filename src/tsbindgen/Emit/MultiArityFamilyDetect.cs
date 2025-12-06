using System.Collections.Immutable;
using tsbindgen.Model.Symbols;
using tsbindgen.Plan;

namespace tsbindgen.Emit;

/// <summary>
/// Represents a single member of a multi-arity type family.
/// </summary>
public sealed record MultiArityMember(
    /// <summary>Generic arity (0 for non-generic)</summary>
    int Arity,

    /// <summary>Internal export name (e.g., "ValueTuple_2")</summary>
    string InternalExportName,

    /// <summary>Full CLR name (e.g., "System.ValueTuple`2")</summary>
    string ClrFullName
);

/// <summary>
/// Represents a detected multi-arity type family (e.g., ValueTuple, Action, Func).
///
/// A multi-arity family is a set of types that share the same CLR base name
/// but have different generic arities. This is common in the BCL for delegates
/// (Action, Func) and tuples (ValueTuple, Tuple).
/// </summary>
public sealed record MultiArityFamily(
    /// <summary>CLR base name without arity suffix (e.g., "System.ValueTuple")</summary>
    string ClrBaseName,

    /// <summary>Public facade stem name (e.g., "ValueTuple")</summary>
    string PublicStem,

    /// <summary>Maximum arity present in the family</summary>
    int MaxArity,

    /// <summary>True if a non-generic (arity 0) member exists</summary>
    bool HasArityZero,

    /// <summary>All members sorted by arity</summary>
    ImmutableArray<MultiArityMember> Members,

    /// <summary>True if this is a delegate family (Action/Func) - gets callable signatures</summary>
    bool IsDelegateFamily
);

/// <summary>
/// Detects multi-arity type families from namespace exports.
///
/// Detection is based on CLR identity (backtick-arity naming), NOT TypeScript names.
/// This ensures correct handling of ANY multi-arity family from ANY assembly,
/// not just hardcoded BCL types.
///
/// A multi-arity family exists when:
/// 1. Multiple types share the same CLR base name (e.g., "System.ValueTuple")
/// 2. They have different generic arities (e.g., 0, 1, 2, ...)
/// </summary>
public static class MultiArityFamilyDetect
{
    /// <summary>
    /// Detect all multi-arity families from namespace exports.
    /// Keyed by CLR base name, not TypeScript name.
    /// </summary>
    /// <param name="exports">The exports for a namespace</param>
    /// <param name="ctx">Build context for logging</param>
    /// <returns>Immutable array of detected families</returns>
    public static ImmutableArray<MultiArityFamily> FromExports(
        IReadOnlyList<ExportStatement> exports,
        BuildContext ctx)
    {
        // Filter out nested types (contain '+' in CLR name) - they are not arity variants
        // e.g., FrozenDictionary`2+Enumerator is a nested type, not an arity variant of FrozenDictionary
        var topLevelExports = exports
            .Where(e => !e.SourceType.ClrFullName.Contains('+'))
            .ToList();

        // Group by CLR base name (strip backtick-arity)
        var familyGroups = topLevelExports
            .GroupBy(e => ExtractClrBaseName(e.SourceType.ClrFullName))
            .Where(g => g.Select(e => e.Arity).Distinct().Count() >= 2) // Must have different arities
            .ToList();

        var families = new List<MultiArityFamily>();

        foreach (var group in familyGroups)
        {
            var members = group
                .Select(e => new MultiArityMember(
                    Arity: e.Arity,
                    InternalExportName: e.ExportName,
                    ClrFullName: e.SourceType.ClrFullName))
                .OrderBy(m => m.Arity)
                .ToImmutableArray();

            var publicStem = GetStem(group.First().ExportName);
            var isDelegateFamily = group.Any(e => e.SourceType.Kind == TypeKind.Delegate);

            families.Add(new MultiArityFamily(
                ClrBaseName: group.Key,
                PublicStem: publicStem,
                MaxArity: members.Max(m => m.Arity),
                HasArityZero: members.Any(m => m.Arity == 0),
                Members: members,
                IsDelegateFamily: isDelegateFamily
            ));

            ctx.Log("MultiArityFamilyDetect",
                $"Detected: {publicStem} (CLR: {group.Key}, arities: {string.Join(",", members.Select(m => m.Arity))}, delegate: {isDelegateFamily})");
        }

        return families.ToImmutableArray();
    }

    /// <summary>
    /// Extract CLR base name without arity suffix.
    ///
    /// Examples:
    ///   System.ValueTuple`2 → System.ValueTuple
    ///   System.Action`16 → System.Action
    ///   MyLib.Result`2 → MyLib.Result
    ///   System.String → System.String (unchanged)
    /// </summary>
    public static string ExtractClrBaseName(string clrFullName)
    {
        var backtickIndex = clrFullName.LastIndexOf('`');
        return backtickIndex >= 0 ? clrFullName.Substring(0, backtickIndex) : clrFullName;
    }

    /// <summary>
    /// Get the stem name by stripping generic arity suffix (_1, _2, ...).
    /// Used to determine the public facade name for a family.
    /// </summary>
    private static string GetStem(string exportName)
    {
        if (string.IsNullOrWhiteSpace(exportName))
            return exportName;

        var lastUnderscore = exportName.LastIndexOf('_');
        if (lastUnderscore >= 0 && lastUnderscore < exportName.Length - 1)
        {
            var suffix = exportName.Substring(lastUnderscore + 1);
            if (int.TryParse(suffix, out _))
            {
                return exportName.Substring(0, lastUnderscore);
            }
        }

        return exportName;
    }
}
