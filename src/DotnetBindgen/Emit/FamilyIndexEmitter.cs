using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetBindgen.Plan;

namespace DotnetBindgen.Emit;

/// <summary>
/// Represents a facade family entry in families.json.
/// Used by ImportPlanner to determine stem import names without recomputation.
/// </summary>
public sealed record FacadeFamilyEntry(
    /// <summary>Public stem name (e.g., "ValueTuple")</summary>
    string Stem,

    /// <summary>Namespace containing the family (e.g., "System")</summary>
    string Namespace,

    /// <summary>Minimum arity in the family (0 or 1)</summary>
    int MinArity,

    /// <summary>Maximum arity in the family</summary>
    int MaxArity,

    /// <summary>True if this is a delegate family</summary>
    bool IsDelegate
);

/// <summary>
/// Emits families.json at package root.
/// Contains the canonical facade family index for consumer packages.
///
/// Purpose: ImportPlanner in consumer builds can look up whether a CLR base name
/// has a facade stem alias WITHOUT recomputing from AllowedClrFullNames.
/// This prevents drift between what facade emitter does and what ImportPlanner assumes.
/// </summary>
public static class FamilyIndexEmitter
{
    public static void Emit(BuildContext ctx, EmissionPlan plan, string outputDirectory)
    {
        ctx.Log("FamilyIndexEmitter", "Generating families.json...");

        var familyIndex = new Dictionary<string, FacadeFamilyEntry>();

        // Collect families from all namespaces
        foreach (var nsOrder in plan.EmissionOrder.Namespaces)
        {
            var ns = nsOrder.Namespace;

            // Detect multi-arity families (same logic as FacadeEmitter uses)
            var multiArityFamilies = MultiArityFamilyDetect.FromNamespace(ns, ctx.Renamer, ctx);

            foreach (var family in multiArityFamilies)
            {
                familyIndex[family.ClrBaseName] = new FacadeFamilyEntry(
                    Stem: family.PublicStem,
                    Namespace: ns.Name,
                    MinArity: family.MinArity,
                    MaxArity: family.MaxArity,
                    IsDelegate: family.IsDelegateFamily
                );

                ctx.Log("FamilyIndexEmitter",
                    $"  {family.ClrBaseName} → {family.PublicStem} (ns: {ns.Name}, arities: {family.MinArity}..{family.MaxArity})");
            }
        }

        // Write to package root
        var outputFile = Path.Combine(outputDirectory, "families.json");
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        var json = JsonSerializer.Serialize(familyIndex, jsonOptions);
        File.WriteAllText(outputFile, json);

        ctx.Log("FamilyIndexEmitter", $"Generated families.json with {familyIndex.Count} families");
    }
}
