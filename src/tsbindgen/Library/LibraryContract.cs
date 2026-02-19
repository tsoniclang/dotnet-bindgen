using System.Collections.Immutable;
using System.Collections.Generic;
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
    /// All npm package names that contribute to this merged contract.
    /// </summary>
    public required ImmutableHashSet<string> PackageNames { get; init; }

    /// <summary>
    /// Mapping from CLR full name to npm package name.
    /// Used to decide which package to import a CLR type from in library mode.
    /// </summary>
    public required ImmutableDictionary<string, string> ClrFullNameToPackage { get; init; }

    /// <summary>
    /// Mapping from CLR full name to ALL packages that provide that CLR type when merging --lib contracts.
    /// Only contains ambiguous CLR full names (those that appear in more than one package) that were NOT
    /// resolved via --lib-type-override.
    ///
    /// Airplane-grade: tsbindgen will only error if an ambiguous CLR type is actually referenced during
    /// emission (when an import source is required). This avoids failing upfront when multiple --lib
    /// packages contain duplicate CLR full names that are not used by the user's emitted surface
    /// (common with internal helper types duplicated across assemblies).
    /// </summary>
    public required ImmutableDictionary<string, ImmutableArray<string>> AmbiguousClrFullNameToPackages { get; init; }

    /// <summary>
    /// Mapping from CLR namespace to all packages that contribute types in that namespace.
    /// Useful for diagnostics and for emitters that need a single owning module per namespace.
    /// </summary>
    public required ImmutableDictionary<string, ImmutableHashSet<string>> NamespaceToPackages { get; init; }

    /// <summary>
    /// Get the package name for a given CLR full name.
    /// </summary>
    public string GetPackageForClrFullName(string clrFullName)
    {
        if (ClrFullNameToPackage.TryGetValue(clrFullName, out var pkg))
        {
            return pkg;
        }

        if (AmbiguousClrFullNameToPackages.TryGetValue(clrFullName, out var pkgs))
        {
            throw new InvalidOperationException(
                $"Ambiguous library type ownership for CLR type '{clrFullName}'. " +
                $"It appears in multiple packages: {string.Join(", ", pkgs)}.\n" +
                "Provide explicit overrides (ClrFullName=packageName) to resolve these conflicts.");
        }

        throw new KeyNotFoundException($"No package mapping found for CLR type '{clrFullName}'.");
    }

    /// <summary>
    /// Get the unique package that owns a namespace module.
    /// Throws if the namespace is split across multiple packages.
    /// </summary>
    public string GetUniquePackageForNamespace(string namespaceName)
    {
        if (!NamespaceToPackages.TryGetValue(namespaceName, out var pkgs) || pkgs.Count == 0)
        {
            throw new KeyNotFoundException($"No package mapping found for namespace '{namespaceName}'.");
        }

        if (pkgs.Count != 1)
        {
            throw new InvalidOperationException(
                $"Namespace '{namespaceName}' is split across multiple packages: {string.Join(", ", pkgs.OrderBy(p => p))}. " +
                "This emitter requires a unique owning package for the namespace module.");
        }

        return pkgs.Single();
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
    public static LibraryContract Merge(
        IReadOnlyList<LibraryContract> contracts,
        IReadOnlyDictionary<string, string>? clrTypePackageOverrides = null)
    {
        if (contracts.Count == 0)
            throw new ArgumentException("Cannot merge empty list of contracts");

        if (contracts.Count == 1)
            return contracts[0];

        var packageNames = contracts.Select(c => c.PackageName).Distinct().ToList();
        var mergedPackageName = packageNames.Count == 1 ? packageNames[0] : string.Join("+", packageNames);

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

        // Merge CLR name to namespace mappings.
        // Airplane-grade: the same CLR full name must map to the same namespace in all contracts.
        // If it ever differs, that's a broken input set; for determinism we take the first (contract order).
        var clrFullNameToNamespace = contracts
            .SelectMany(c => c.ClrFullNameToNamespace)
            .GroupBy(kvp => kvp.Key)
            .ToImmutableDictionary(g => g.Key, g => g.First().Value);

        // Merge facade families.
        // These are optional (families.json may not exist). If multiple contracts define the same family key,
        // they are expected to be identical; for determinism we take the first (contract order).
        var facadeFamilies = contracts
            .SelectMany(c => c.FacadeFamilies)
            .GroupBy(kvp => kvp.Key)
            .ToImmutableDictionary(g => g.Key, g => g.First().Value);

        // Build per-type package mapping.
        // Airplane-grade: never "pick a package for a namespace"; pick a package for each CLR type.
        var clrToPackages = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var contract in contracts)
        {
            foreach (var clr in contract.AllowedClrFullNames)
            {
                if (!clrToPackages.TryGetValue(clr, out var pkgs))
                {
                    pkgs = new HashSet<string>(StringComparer.Ordinal);
                    clrToPackages[clr] = pkgs;
                }
                pkgs.Add(contract.PackageName);
            }
        }

        var clrFullNameToPackage = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguousClrFullNameToPackages = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);

        foreach (var (clr, pkgs) in clrToPackages.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (pkgs.Count == 1)
            {
                clrFullNameToPackage[clr] = pkgs.Single();
                continue;
            }

            // Conflict: the same CLR full name appears in multiple library packages.
            // This is usually a broken dependency graph (dotnet would produce CS0433), but allow a manual override.
            if (clrTypePackageOverrides != null &&
                clrTypePackageOverrides.TryGetValue(clr, out var chosenPkg))
            {
                if (!pkgs.Contains(chosenPkg))
                {
                    throw new InvalidOperationException(
                        $"Library type override for '{clr}' chose package '{chosenPkg}', but available packages are: {string.Join(", ", pkgs.OrderBy(p => p))}.");
                }

                clrFullNameToPackage[clr] = chosenPkg;
                continue;
            }

            // Keep ambiguity, but do not fail upfront. Only error if the type is actually referenced
            // and an import source is required.
            ambiguousClrFullNameToPackages[clr] = pkgs.OrderBy(p => p, StringComparer.Ordinal).ToImmutableArray();
        }

        var packageNameSet = contracts.Select(c => c.PackageName).ToImmutableHashSet(StringComparer.Ordinal);

        // Build namespace-to-packages for quick lookups.
        var namespaceToPackages = clrToPackages
            .GroupBy(kvp => clrFullNameToNamespace[kvp.Key])
            .ToImmutableDictionary(
                g => g.Key,
                g => g.SelectMany(kvp => kvp.Value).ToImmutableHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        return new LibraryContract
        {
            PackageName = mergedPackageName,
            AllowedTypeStableIds = allowedTypeStableIds,
            AllowedMemberStableIds = allowedMemberStableIds,
            AllowedBindingStableIds = allowedBindingStableIds,
            AllowedClrFullNames = allowedClrFullNames,
            NamespaceToTypes = namespaceToTypes,
            ClrFullNameToNamespace = clrFullNameToNamespace,
            FacadeFamilies = facadeFamilies,
            PackageNames = packageNameSet,
            ClrFullNameToPackage = clrFullNameToPackage.ToImmutableDictionary(StringComparer.Ordinal),
            AmbiguousClrFullNameToPackages = ambiguousClrFullNameToPackages.ToImmutableDictionary(StringComparer.Ordinal),
            NamespaceToPackages = namespaceToPackages
        };
    }
}
