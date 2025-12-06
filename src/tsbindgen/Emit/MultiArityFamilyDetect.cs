using System.Collections.Immutable;
using tsbindgen.Model.Symbols;
using tsbindgen.Renaming;

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

    /// <summary>Minimum arity present in the family (0 or 1)</summary>
    int MinArity,

    /// <summary>All members sorted by arity (guaranteed contiguous from MinArity to MaxArity)</summary>
    ImmutableArray<MultiArityMember> Members,

    /// <summary>True if this is a delegate family - gets callable signatures</summary>
    bool IsDelegateFamily
);

/// <summary>
/// Detects multi-arity type families from namespace symbols.
///
/// Detection is based on CLR identity (backtick-arity naming), NOT TypeScript names.
/// This ensures correct handling of ANY multi-arity family from ANY assembly,
/// not just hardcoded BCL types.
///
/// A multi-arity family exists when:
/// 1. Multiple types share the same CLR base name (e.g., "System.ValueTuple")
/// 2. They have CONTIGUOUS generic arities (e.g., 0,1,2,... or 1,2,3,...)
/// </summary>
public static class MultiArityFamilyDetect
{
    /// <summary>
    /// Detect all multi-arity families from namespace symbols.
    /// Uses canonical TypeSymbol information before any facade renaming.
    /// </summary>
    /// <param name="ns">The namespace symbol containing types</param>
    /// <param name="renamer">Renamer to get internal export names</param>
    /// <param name="ctx">Build context for logging</param>
    /// <returns>Immutable array of detected families with contiguous arities</returns>
    public static ImmutableArray<MultiArityFamily> FromNamespace(
        NamespaceSymbol ns,
        SymbolRenamer renamer,
        BuildContext ctx)
    {
        // Get only top-level public types (exclude nested types via DeclaringType check)
        var topLevelPublicTypes = ns.Types
            .Where(t => t.Accessibility == Accessibility.Public && t.DeclaringType == null)
            .ToList();

        // Group by CLR base name (strip backtick-arity)
        var familyGroups = topLevelPublicTypes
            .GroupBy(t => ExtractClrBaseName(t.ClrFullName))
            .Where(g => g.Select(t => t.Arity).Distinct().Count() >= 2) // Must have different arities
            .ToList();

        var families = new List<MultiArityFamily>();

        foreach (var group in familyGroups)
        {
            var types = group.OrderBy(t => t.Arity).ToList();
            var arities = types.Select(t => t.Arity).ToList();

            // Validate contiguous arities
            var minArity = arities.Min();
            var maxArity = arities.Max();
            var expectedArities = Enumerable.Range(minArity, maxArity - minArity + 1).ToList();

            if (!arities.SequenceEqual(expectedArities))
            {
                // Non-contiguous arities - skip this family, let per-arity exports stand
                ctx.Log("MultiArityFamilyDetect",
                    $"SKIP: {group.Key} has non-contiguous arities [{string.Join(",", arities)}], " +
                    $"expected [{string.Join(",", expectedArities)}]");
                continue;
            }

            // Build members with internal export names from renamer
            var members = types
                .Select(t => new MultiArityMember(
                    Arity: t.Arity,
                    InternalExportName: renamer.GetFinalTypeName(t),
                    ClrFullName: t.ClrFullName))
                .ToImmutableArray();

            // Public stem is the internal name without arity suffix
            var publicStem = GetStem(members[0].InternalExportName);

            // Delegate detection is metadata-based (TypeKind), not name-based
            var isDelegateFamily = types.Any(t => t.Kind == TypeKind.Delegate);

            families.Add(new MultiArityFamily(
                ClrBaseName: group.Key,
                PublicStem: publicStem,
                MaxArity: maxArity,
                MinArity: minArity,
                Members: members,
                IsDelegateFamily: isDelegateFamily
            ));

            ctx.Log("MultiArityFamilyDetect",
                $"Detected: {publicStem} (CLR: {group.Key}, arities: {minArity}..{maxArity}, delegate: {isDelegateFamily})");
        }

        // HARD FAIL: Detect PublicStem collisions within namespace
        // Two unrelated CLR base names mapping to the same stem is a correctness bug
        var stemGroups = families.GroupBy(f => f.PublicStem, StringComparer.Ordinal);
        foreach (var stemGroup in stemGroups)
        {
            if (stemGroup.Count() > 1)
            {
                var colliders = string.Join(", ", stemGroup.Select(f => f.ClrBaseName));
                throw new InvalidOperationException(
                    $"Facade stem collision in namespace '{ns.Name}': " +
                    $"multiple multi-arity families map to stem '{stemGroup.Key}'. " +
                    $"Colliding CLR base names: [{colliders}]");
            }
        }

        return families.ToImmutableArray();
    }

    /// <summary>
    /// Extract CLR base name without arity suffix.
    ///
    /// Examples:
    ///   System.ValueTuple`2 -> System.ValueTuple
    ///   System.Action`16 -> System.Action
    ///   MyLib.Result`2 -> MyLib.Result
    ///   System.String -> System.String (unchanged)
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
    public static string GetStem(string exportName)
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

    /// <summary>
    /// Check if a CLR full name belongs to a multi-arity family.
    /// Used by ImportPlanner to determine if stem import is valid.
    /// </summary>
    public static bool IsInFamily(string clrFullName, IReadOnlyDictionary<string, MultiArityFamily> familyIndex)
    {
        var baseName = ExtractClrBaseName(clrFullName);
        return familyIndex.ContainsKey(baseName);
    }

    /// <summary>
    /// Get the public stem for a CLR type if it belongs to a family.
    /// Returns null if not part of a family.
    /// </summary>
    public static string? GetFamilyStem(string clrFullName, IReadOnlyDictionary<string, MultiArityFamily> familyIndex)
    {
        var baseName = ExtractClrBaseName(clrFullName);
        return familyIndex.TryGetValue(baseName, out var family) ? family.PublicStem : null;
    }
}
