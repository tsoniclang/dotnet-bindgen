using System.Collections.Immutable;
using tsbindgen.Emit;

namespace tsbindgen.Library;

/// <summary>
/// Represents the contract defined by an existing tsbindgen library package.
/// Loaded from bindings.json and families.json files.
/// Used to filter emission to only symbols present in the library.
/// </summary>
public sealed record LibraryContract
{
    /// <summary>
    /// npm package name from package.json (e.g., "@tsonic/dotnet").
    /// Used for generating import specifiers for external library types.
    /// </summary>
    public required string PackageName { get; init; }

    /// <summary>
    /// Set of allowed type StableIds (format: "AssemblyName:ClrFullName").
    /// A type is emittable iff its StableId exists in this set.
    /// </summary>
    public required ImmutableHashSet<string> AllowedTypeStableIds { get; init; }

    /// <summary>
    /// Set of allowed member StableIds (format: "AssemblyName:DeclaringType::MemberNameSignature").
    /// A member is emittable iff its StableId exists in this set.
    /// </summary>
    public required ImmutableHashSet<string> AllowedMemberStableIds { get; init; }

    /// <summary>
    /// Set of binding StableIds from bindings.json.
    /// Used to validate that all emitted members have corresponding bindings.
    /// </summary>
    public required ImmutableHashSet<string> AllowedBindingStableIds { get; init; }

    /// <summary>
    /// Mapping from namespace name to set of type StableIds in that namespace.
    /// Used to preserve namespace structure from the library.
    /// </summary>
    public required ImmutableDictionary<string, ImmutableHashSet<string>> NamespaceToTypes { get; init; }

    /// <summary>
    /// Set of CLR full names (extracted from AllowedTypeStableIds).
    /// Format: "System.Exception", "System.Collections.Generic.List`1", etc.
    /// Used for per-type membership checks in import planning.
    /// Derived from AllowedTypeStableIds by stripping assembly prefix.
    /// </summary>
    public required ImmutableHashSet<string> AllowedClrFullNames { get; init; }

    /// <summary>
    /// Mapping from CLR full name to namespace.
    /// Used to determine which namespace facade to import a type from.
    /// Example: "System.Exception" → "System"
    /// </summary>
    public required ImmutableDictionary<string, string> ClrFullNameToNamespace { get; init; }

    /// <summary>
    /// Canonical facade family index.
    /// Maps CLR base name (e.g., "System.ValueTuple") to family entry with stem/namespace/arity info.
    /// ImportPlanner uses this to determine if a type belongs to a multi-arity family
    /// WITHOUT recomputing from AllowedClrFullNames (prevents drift).
    /// Empty if families.json doesn't exist.
    /// </summary>
    public required ImmutableDictionary<string, FacadeFamilyEntry> FacadeFamilies { get; init; }

    /// <summary>
    /// Mapping from namespace to package name.
    /// Used when multiple libraries are merged to determine which package to import from.
    /// Example: "System" → "@tsonic/dotnet", "Tsonic.Runtime" → "@tsonic/core"
    /// </summary>
    public required ImmutableDictionary<string, string> NamespaceToPackage { get; init; }

    /// <summary>
    /// Get the package name for a given namespace.
    /// Returns the specific package if known, otherwise falls back to PackageName.
    /// </summary>
    public string GetPackageForNamespace(string namespaceName)
    {
        return NamespaceToPackage.TryGetValue(namespaceName, out var pkg) ? pkg : PackageName;
    }

    /// <summary>
    /// Total number of types in the contract.
    /// </summary>
    public int TypeCount => AllowedTypeStableIds.Count;

    /// <summary>
    /// Total number of members in the contract.
    /// </summary>
    public int MemberCount => AllowedMemberStableIds.Count;

    /// <summary>
    /// Total number of namespaces in the contract.
    /// </summary>
    public int NamespaceCount => NamespaceToTypes.Count;

    /// <summary>
    /// Merge multiple library contracts into one.
    /// Used when multiple --lib arguments are provided.
    /// </summary>
    public static LibraryContract Merge(IReadOnlyList<LibraryContract> contracts)
    {
        if (contracts.Count == 0)
            throw new ArgumentException("Cannot merge empty list of contracts");

        if (contracts.Count == 1)
            return contracts[0];

        // Use first contract's package name (or combine them)
        var packageNames = contracts.Select(c => c.PackageName).Distinct().ToList();
        var packageName = packageNames.Count == 1 ? packageNames[0] : string.Join("+", packageNames);

        // Merge all sets
        var allowedTypeStableIds = contracts
            .SelectMany(c => c.AllowedTypeStableIds)
            .ToImmutableHashSet();

        var allowedMemberStableIds = contracts
            .SelectMany(c => c.AllowedMemberStableIds)
            .ToImmutableHashSet();

        var allowedBindingStableIds = contracts
            .SelectMany(c => c.AllowedBindingStableIds)
            .ToImmutableHashSet();

        var allowedClrFullNames = contracts
            .SelectMany(c => c.AllowedClrFullNames)
            .ToImmutableHashSet();

        // Merge namespace-to-types (union of all types per namespace)
        var namespaceToTypes = contracts
            .SelectMany(c => c.NamespaceToTypes)
            .GroupBy(kvp => kvp.Key)
            .ToImmutableDictionary(
                g => g.Key,
                g => g.SelectMany(kvp => kvp.Value).ToImmutableHashSet());

        // Merge CLR name to namespace mappings (last wins for conflicts)
        var clrFullNameToNamespace = contracts
            .SelectMany(c => c.ClrFullNameToNamespace)
            .GroupBy(kvp => kvp.Key)
            .ToImmutableDictionary(g => g.Key, g => g.First().Value);

        // Merge facade families (last wins for conflicts)
        var facadeFamilies = contracts
            .SelectMany(c => c.FacadeFamilies)
            .GroupBy(kvp => kvp.Key)
            .ToImmutableDictionary(g => g.Key, g => g.First().Value);

        // Build namespace-to-package mapping from all contracts
        var namespaceToPackage = contracts
            .SelectMany(c => c.NamespaceToTypes.Keys.Select(ns => (ns, c.PackageName)))
            .GroupBy(t => t.ns)
            .ToImmutableDictionary(g => g.Key, g => g.First().PackageName);

        return new LibraryContract
        {
            PackageName = packageName,
            AllowedTypeStableIds = allowedTypeStableIds,
            AllowedMemberStableIds = allowedMemberStableIds,
            AllowedBindingStableIds = allowedBindingStableIds,
            AllowedClrFullNames = allowedClrFullNames,
            NamespaceToTypes = namespaceToTypes,
            ClrFullNameToNamespace = clrFullNameToNamespace,
            FacadeFamilies = facadeFamilies,
            NamespaceToPackage = namespaceToPackage
        };
    }
}
